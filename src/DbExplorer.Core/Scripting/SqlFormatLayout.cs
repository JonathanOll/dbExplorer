using DbExplorer.Core.Model;

namespace DbExplorer.Core.Scripting;

/// <summary>
/// Lays out one statement (a token tree) as text: one clause per line, lists one item per line, subqueries indented, short
/// parenthesised runs kept inline. It never reorders or drops a token; the caller verifies that.
/// </summary>
internal sealed class SqlFormatLayout
{
    private enum CK
    {
        Prefix, Select, From, Where, Having, GroupBy, OrderBy, Limit, SetOp, With, Insert, Update, Delete, Merge, Using,
        MergeWhen, Values, Set, Returning, Other,
    }

    private sealed class Clause(CK kind, int extra)
    {
        public CK Kind { get; } = kind;
        public int Extra { get; } = extra;
        public List<FLeaf> Kw { get; } = new();
        public List<FNode> Body { get; } = new();
    }

    private sealed class Item
    {
        public List<FNode> Nodes { get; } = new();
        public FLeaf? Comma { get; set; }
        public List<FLeaf> Trail { get; } = new();
    }

    private readonly SqlFormatOptions o;
    private readonly DbKind kind;
    private readonly string eol;
    private SqlFormatEmitter em;

    public SqlFormatLayout(SqlFormatOptions options, DbKind dbKind, string newline)
    {
        o = options;
        kind = dbKind;
        eol = newline;
        em = new SqlFormatEmitter(o, eol, false);
    }

    /// <summary>True when the formatter knows the layout of this statement (otherwise the caller falls back to light normalisation).</summary>
    public static bool IsFormattable(List<FNode> nodes)
    {
        var words = new List<string>();
        foreach (var n in nodes)
        {
            if (n is FLeaf { IsComment: true }) continue;
            if (n is FLeaf l && l.Tok.Kind == FKind.Word) words.Add(l.Tok.Lower);
            else if (words.Count == 0) return false;
            if (words.Count >= 8 || n is FGroup && words[0] != "explain") break;
        }
        if (words.Count == 0) return false;
        switch (words[0])
        {
            case "select" or "with" or "insert" or "update" or "delete" or "merge" or "values":
                return true;
            case "create":
                if (words.Skip(1).Any(w => w is "function" or "procedure" or "proc" or "trigger" or "type" or "extension" or "schema" or "database" or "sequence" or "role" or "user" or "domain")) return false;
                return words.Skip(1).Any(w => w is "table" or "view" or "index");
            case "explain":
                return words.Skip(1).Any(w => w is "select" or "with" or "insert" or "update" or "delete" or "values");
        }
        return false;
    }

    /// <summary>Formats the statement; <paramref name="endsWithLineComment"/> tells the caller a ";" needs a new line.</summary>
    public string Run(List<FNode> nodes, out bool endsWithLineComment)
    {
        MarkColumnList(nodes);
        FormatSeq(nodes, 0);
        endsWithLineComment = em.BreakPending;
        return em.ToString();
    }

    // ------------------------------------------------------------------ statements and clauses

    private static void MarkColumnList(List<FNode> nodes)
    {
        var words = 0;
        var isTable = false;
        var first = true;
        foreach (var n in nodes)
        {
            if (n is FLeaf { IsComment: true }) continue;
            if (n is FLeaf l)
            {
                if (first && !l.IsWord("create")) return;
                first = false;
                if (l.IsWord("table")) isTable = true;
                if (l.IsWord("as")) return;
                if (++words > 12) return;
            }
            else if (n is FGroup g)
            {
                if (isTable && g.Open.Raw == "(") g.ForceBreak = true;
                return;
            }
        }
    }

    private void FormatSeq(List<FNode> nodes, int b)
    {
        foreach (var c in Segment(nodes)) RenderClause(c, b);
    }

    private List<Clause> Segment(List<FNode> nodes)
    {
        var list = new List<Clause>();
        var cur = new Clause(CK.Prefix, 0);
        list.Add(cur);
        var firstSig = nodes.FindIndex(n => n is not FLeaf { IsComment: true });
        var caseDepth = 0;
        var sawUpdate = false;
        var mergeMode = false;
        var deleteMode = false;
        var mergeAction = false;

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node is FLeaf { Tok.Kind: FKind.Word } leaf)
            {
                var w = leaf.Tok.Lower;
                if (w == "case") caseDepth++;
                else if (w == "end" && caseDepth > 0) caseDepth--;
                if (w == "update") sawUpdate = true;

                if (caseDepth == 0)
                {
                    if (w == "then" && cur.Kind == CK.MergeWhen) mergeAction = true;

                    if (w == "from" && cur.Kind == CK.Delete && cur.Body.Count == 0)
                    {
                        cur.Body.Add(node);
                        continue;
                    }
                    if (TryClause(nodes, i, cur, firstSig, sawUpdate, mergeMode, deleteMode, mergeAction, out var ck, out var len))
                    {
                        if (ck == CK.MergeWhen) mergeAction = false;
                        var extra = mergeAction && ck is CK.Update or CK.Insert or CK.Delete or CK.Set or CK.Values ? 1 : 0;
                        cur = new Clause(ck, extra);
                        for (var k = 0; k < len; k++) cur.Kw.Add((FLeaf)nodes[i + k]);
                        list.Add(cur);
                        i += len - 1;
                        if (ck == CK.Merge) mergeMode = true;
                        if (ck == CK.Delete) deleteMode = true;
                        continue;
                    }
                }
            }
            cur.Body.Add(node);
        }
        return list;
    }

    private bool TryClause(List<FNode> nodes, int i, Clause cur, int firstSig, bool sawUpdate, bool mergeMode, bool deleteMode,
        bool mergeAction, out CK ck, out int len)
    {
        ck = CK.Other;
        len = 1;
        var w = ((FLeaf)nodes[i]).Tok.Lower;
        string? Next() => i + 1 < nodes.Count && nodes[i + 1] is FLeaf { Tok.Kind: FKind.Word } nl ? nl.Tok.Lower : null;
        string? Prev() => i > 0 && nodes[i - 1] is FLeaf { Tok.Kind: FKind.Word } pl ? pl.Tok.Lower : null;
        var statementStart = i == firstSig || cur.Kind is CK.With || cur.Kind == CK.Prefix || mergeAction;

        switch (w)
        {
            case "select": ck = CK.Select; return true;
            case "from":
                if (Prev() == "distinct") return false;
                ck = CK.From; return true;
            case "where": ck = CK.Where; return true;
            case "having": ck = CK.Having; return true;
            case "group" when Next() == "by": ck = CK.GroupBy; len = 2; return true;
            case "order" when Next() == "by": ck = CK.OrderBy; len = 2; return true;
            case "limit" or "offset": ck = CK.Limit; return true;
            case "fetch" when Next() is "first" or "next": ck = CK.Limit; return true;
            case "union" or "intersect" or "except":
                if (cur.Kind == CK.Prefix) return false;
                if (w == "except" && i + 1 < nodes.Count && nodes[i + 1] is FGroup) return false;
                ck = CK.SetOp;
                if (Next() is "all" or "distinct") len = 2;
                return true;
            case "with" when i == firstSig:
                ck = CK.With;
                if (Next() == "recursive") len = 2;
                return true;
            case "insert" when statementStart: ck = CK.Insert; return true;
            case "update" when statementStart: ck = CK.Update; return true;
            case "delete" when statementStart: ck = CK.Delete; return true;
            case "merge" when statementStart: ck = CK.Merge; return true;
            case "values":
                if (Prev() == "default") return false;
                ck = CK.Values; return true;
            case "set" when sawUpdate && cur.Kind != CK.Prefix || w == "set" && cur.Kind == CK.Update: ck = CK.Set; return true;
            case "using" when (mergeMode || deleteMode) && cur.Kind != CK.From: ck = CK.Using; return true;
            case "when" when mergeMode: ck = CK.MergeWhen; return true;
            case "returning" when kind != DbKind.SqlServer: ck = CK.Returning; return true;
            case "output" when kind == DbKind.SqlServer && cur.Kind != CK.Prefix: ck = CK.Returning; return true;
            case "on" when Next() == "conflict": ck = CK.Other; len = 2; return true;
        }
        return false;
    }

    private void RenderClause(Clause c, int b)
    {
        var ind = b + c.Extra;
        if (c.Kind != CK.Prefix) em.NewLine(ind);
        foreach (var k in c.Kw) AppendTok(k.Tok);
        // A comment on its own line right after the keyword: the body goes below it, indented.
        if (c.Kind is not (CK.Prefix or CK.SetOp) && c.Body.Count > 0 && c.Body[0] is FLeaf { IsComment: true, Tok.OwnLine: true })
            em.NewLine(ind + 1);

        switch (c.Kind)
        {
            case CK.Prefix:
            case CK.Insert:
            case CK.Update:
            case CK.Delete:
            case CK.Merge:
            case CK.Limit:
            case CK.MergeWhen:
            case CK.Other:
                RenderFlow(c.Body);
                break;
            case CK.SetOp:
                RenderFlow(c.Body);
                break;
            case CK.Select:
            {
                var idx = RenderSelectModifiers(c.Body);
                var rest = idx == 0 ? c.Body : c.Body.GetRange(idx, c.Body.Count - idx);
                RenderList(rest, ind, breakWhenMany: true);
                break;
            }
            case CK.Returning:
            case CK.Set:
            case CK.Values:
                RenderList(c.Body, ind, breakWhenMany: true);
                break;
            case CK.GroupBy:
            case CK.OrderBy:
                RenderList(c.Body, ind, breakWhenMany: false);
                break;
            case CK.Where:
            case CK.Having:
                RenderCond(c.Body, ind + 1);
                break;
            case CK.From:
            case CK.Using:
                RenderFrom(c.Body, ind);
                break;
            case CK.With:
                RenderWith(c.Body, ind);
                break;
        }
    }

    /// <summary>DISTINCT / ALL / DISTINCT ON (...) / TOP n [PERCENT] [WITH TIES] stay on the SELECT line. Returns how many nodes it used.</summary>
    private int RenderSelectModifiers(List<FNode> body)
    {
        var i = 0;
        while (i < body.Count)
        {
            if (body[i] is FLeaf { IsComment: true }) break;
            if (body[i] is FLeaf l && l.IsWord("distinct"))
            {
                AppendTok(l.Tok);
                i++;
                if (i + 1 < body.Count && body[i] is FLeaf on && on.IsWord("on") && body[i + 1] is FGroup g)
                {
                    AppendTok(on.Tok);
                    RenderGroup(g);
                    i += 2;
                }
            }
            else if (body[i] is FLeaf l2 && l2.IsWord("all"))
            {
                AppendTok(l2.Tok);
                i++;
            }
            else if (kind == DbKind.SqlServer && body[i] is FLeaf l3 && l3.IsWord("top") && i + 1 < body.Count)
            {
                AppendTok(l3.Tok);
                i++;
                if (body[i] is FGroup tg) RenderGroup(tg);
                else if (body[i] is FLeaf tl) AppendTok(tl.Tok);
                i++;
                if (i < body.Count && body[i] is FLeaf pc && pc.IsWord("percent")) { AppendTok(pc.Tok); i++; }
                if (i + 1 < body.Count && body[i] is FLeaf wi && wi.IsWord("with") && body[i + 1] is FLeaf ti && ti.IsWord("ties"))
                {
                    AppendTok(wi.Tok);
                    AppendTok(ti.Tok);
                    i += 2;
                }
            }
            else break;
        }
        return i;
    }

    // ------------------------------------------------------------------ lists

    private static List<Item> SplitItems(List<FNode> nodes)
    {
        var items = new List<Item> { new() };
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            if (n is FLeaf { Tok.Kind: FKind.Comma } comma)
            {
                var cur = items[^1];
                cur.Comma = comma;
                while (i + 1 < nodes.Count && nodes[i + 1] is FLeaf { IsComment: true } c && !c.Tok.OwnLine)
                {
                    cur.Trail.Add(c);
                    i++;
                }
                items.Add(new Item());
            }
            else items[^1].Nodes.Add(n);
        }
        return items;
    }

    private void RenderList(List<FNode> nodes, int ind, bool breakWhenMany)
    {
        if (nodes.Count == 0) return;
        var items = SplitItems(nodes);
        var broken = breakWhenMany && items.Count > 1;
        if (!broken)
        {
            var flat = FlatOf(nodes);
            if (flat != null && em.Column + 1 + flat.Length > o.MaxLineWidth) broken = true;
        }
        if (!broken)
        {
            RenderFlow(nodes);
            return;
        }
        EmitBroken(items, ind + 1);
    }

    /// <summary>One item per line at <paramref name="ind"/>, commas trailing or leading.</summary>
    private void EmitBroken(List<Item> items, int ind)
    {
        var leading = o.CommaPosition == CommaPosition.Leading;
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Nodes.Count == 0 && it.Comma == null) continue;
            em.NewLine(ind);
            if (leading && i > 0 && items[i - 1].Comma is { } pc)
            {
                AppendTok(pc.Tok);
                foreach (var t in items[i - 1].Trail) AppendTok(t.Tok);
            }
            RenderFlow(it.Nodes);
            if (!leading && it.Comma is { } c)
            {
                AppendTok(c.Tok);
                foreach (var t in it.Trail) AppendTok(t.Tok);
            }
        }
    }

    // ------------------------------------------------------------------ conditions

    private sealed class Term(FLeaf? connector)
    {
        public FLeaf? Connector { get; } = connector;
        public List<FNode> Nodes { get; } = new();
    }

    private static List<Term> SplitConds(List<FNode> nodes)
    {
        var terms = new List<Term> { new(null) };
        var between = false;
        var caseDepth = 0;
        foreach (var n in nodes)
        {
            if (n is FLeaf { Tok.Kind: FKind.Word } l)
            {
                var w = l.Tok.Lower;
                if (w == "case") caseDepth++;
                else if (w == "end" && caseDepth > 0) caseDepth--;
                else if (caseDepth == 0)
                {
                    if (w == "between") between = true;
                    else if (w == "and" && between) between = false;
                    else if (w is "and" or "or")
                    {
                        terms.Add(new Term(l));
                        continue;
                    }
                }
            }
            terms[^1].Nodes.Add(n);
        }
        return terms;
    }

    /// <summary>First term inline (after the keyword already written), the others on their own line starting with AND / OR.</summary>
    private void RenderCond(List<FNode> nodes, int andIndent)
    {
        var terms = SplitConds(nodes);
        for (var i = 0; i < terms.Count; i++)
        {
            var t = terms[i];
            if (i > 0)
            {
                em.NewLine(andIndent);
                AppendTok(t.Connector!.Tok);
            }
            RenderFlow(t.Nodes);
        }
    }

    // ------------------------------------------------------------------ FROM, WITH

    private static int JoinStart(List<FNode> nodes, int i, out int len)
    {
        len = 0;
        var j = i;
        while (j < nodes.Count && nodes[j] is FLeaf { Tok.Kind: FKind.Word } m && m.Tok.Lower is "natural" or "inner" or "left" or "right" or "full" or "cross" or "outer" or "straight_join")
            j++;
        if (j < nodes.Count && nodes[j] is FLeaf { Tok.Kind: FKind.Word } jw && jw.Tok.Lower is "join" or "apply")
        {
            len = j - i + 1;
            return i;
        }
        return -1;
    }

    private void RenderFrom(List<FNode> body, int ind)
    {
        // Segments: the first table list, then one segment per JOIN.
        var segs = new List<(int Start, int Kw)>();
        for (var i = 0; i < body.Count; i++)
        {
            if (body[i] is FLeaf { Tok.Kind: FKind.Word } && JoinStart(body, i, out var len) == i)
            {
                segs.Add((i, len));
                i += len - 1;
            }
        }

        var firstEnd = segs.Count > 0 ? segs[0].Start : body.Count;
        RenderTableSegment(body.GetRange(0, firstEnd), ind, null);
        for (var s = 0; s < segs.Count; s++)
        {
            var end = s + 1 < segs.Count ? segs[s + 1].Start : body.Count;
            var kwLen = segs[s].Kw;
            em.NewLine(ind);
            for (var k = 0; k < kwLen; k++) AppendTok(((FLeaf)body[segs[s].Start + k]).Tok);
            RenderTableSegment(body.GetRange(segs[s].Start + kwLen, end - segs[s].Start - kwLen), ind, ind + 1);
        }
    }

    /// <summary>A table expression and its ON / USING part. <paramref name="joinIndent"/> is null for the FROM list itself.</summary>
    private void RenderTableSegment(List<FNode> seg, int ind, int? joinIndent)
    {
        var on = seg.FindIndex(n => n is FLeaf { Tok.Kind: FKind.Word, Tok.Lower: "on" or "using" });
        var tables = on >= 0 ? seg.GetRange(0, on) : seg;
        if (joinIndent == null) RenderList(tables, ind, breakWhenMany: true);
        else RenderFlow(tables);
        if (on < 0) return;

        var kw = (FLeaf)seg[on];
        em.NewLine(ind + 1);
        AppendTok(kw.Tok);
        var cond = seg.GetRange(on + 1, seg.Count - on - 1);
        if (kw.IsWord("on")) RenderCond(cond, ind + 1);
        else RenderFlow(cond);
    }

    private void RenderWith(List<FNode> body, int ind)
    {
        var items = SplitItems(body);
        var leading = o.CommaPosition == CommaPosition.Leading;
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (i > 0)
            {
                em.NewLine(ind);
                if (leading && items[i - 1].Comma is { } pc)
                {
                    AppendTok(pc.Tok);
                    foreach (var t in items[i - 1].Trail) AppendTok(t.Tok);
                }
            }
            RenderFlow(it.Nodes);
            if (!leading && it.Comma is { } c)
            {
                AppendTok(c.Tok);
                foreach (var t in it.Trail) AppendTok(t.Tok);
            }
        }
    }

    // ------------------------------------------------------------------ expressions

    private void RenderFlow(List<FNode> nodes)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            switch (nodes[i])
            {
                case FGroup g:
                    RenderGroup(g);
                    break;
                case FLeaf l:
                    if (!em.Flat && l.IsWord("case"))
                    {
                        var end = FindEnd(nodes, i);
                        if (end > 0)
                        {
                            RenderCase(nodes, i, end);
                            i = end;
                            break;
                        }
                    }
                    AppendTok(l.Tok);
                    break;
            }
        }
    }

    private static int FindEnd(List<FNode> nodes, int caseIndex)
    {
        var depth = 0;
        for (var i = caseIndex; i < nodes.Count; i++)
        {
            if (nodes[i] is not FLeaf { Tok.Kind: FKind.Word } l) continue;
            if (l.Tok.Lower == "case") depth++;
            else if (l.Tok.Lower == "end" && --depth == 0) return i;
        }
        return -1;
    }

    private void RenderCase(List<FNode> nodes, int caseIndex, int endIndex)
    {
        var baseInd = em.LineIndent;
        AppendTok(((FLeaf)nodes[caseIndex]).Tok);

        // Split the inside at WHEN / ELSE of this CASE (nested CASEs stay whole).
        var parts = new List<List<FNode>> { new() };
        var depth = 0;
        for (var i = caseIndex + 1; i < endIndex; i++)
        {
            var n = nodes[i];
            if (n is FLeaf { Tok.Kind: FKind.Word } l)
            {
                if (l.Tok.Lower == "case") depth++;
                else if (l.Tok.Lower == "end") depth--;
                else if (depth == 0 && l.Tok.Lower is "when" or "else")
                {
                    parts.Add(new List<FNode>());
                }
            }
            parts[^1].Add(n);
        }

        RenderFlow(parts[0]);
        for (var p = 1; p < parts.Count; p++)
        {
            em.NewLine(baseInd + 1);
            RenderFlow(parts[p]);
        }
        em.NewLine(baseInd);
        AppendTok(((FLeaf)nodes[endIndex]).Tok);
    }

    private bool IsBlockGroup(FGroup g)
    {
        if (g.Open.Raw != "(") return false;
        var first = g.Kids.Find(n => n is not FLeaf { IsComment: true });
        return first is FLeaf { Tok.Kind: FKind.Word } l && l.Tok.Lower is "select" or "with" or "values" or "insert" or "update" or "delete" or "merge" or "table";
    }

    private void RenderGroup(FGroup g)
    {
        var flat = Flat(g);
        if (em.Flat)
        {
            if (flat == null) em.Failed = true;
            else WriteGroupText(g, flat);
            return;
        }

        if (flat != null && !g.ForceBreak && em.Column + (NeedSpace(g.Open) ? 1 : 0) + flat.Length + 2 <= o.MaxLineWidth)
        {
            WriteGroupText(g, flat);
            return;
        }

        if (g.Open.Raw == "(" && IsBlockGroup(g)) { RenderBlock(g); return; }
        var items = SplitItems(g.Kids);
        if (g.Open.Raw == "(" && (items.Count > 1 || g.ForceBreak) && g.Kids.Count > 0) { RenderListGroup(g, items); return; }
        if (g.Open.Raw == "(" && HasTopAndOr(g.Kids) && flat != null) { RenderCondGroup(g); return; }
        if (flat != null) { WriteGroupText(g, flat); return; }
        if (g.Open.Raw == "(") { RenderBlockFlow(g); return; }
        // A bracket run that is not flat (comment inside): keep tokens in a plain flow.
        AppendTok(g.Open);
        RenderFlow(g.Kids);
        AppendTok(g.Close);
    }

    private static bool HasTopAndOr(List<FNode> kids) => kids.Any(n => n is FLeaf { Tok.Kind: FKind.Word, Tok.Lower: "and" or "or" });

    private void WriteGroupText(FGroup g, string flat)
    {
        if (NeedSpace(g.Open) && em.LineHasContent) em.Write(" ");
        em.Write(flat);
        em.Last = g.Close;
        em.LastUnary = false;
    }

    private void RenderBlock(FGroup g)
    {
        var outer = em.LineIndent;
        AppendTok(g.Open);
        em.NewLine(outer + 1);
        FormatSeq(g.Kids, outer + 1);
        em.NewLine(outer);
        AppendTok(g.Close);
    }

    private void RenderBlockFlow(FGroup g)
    {
        var outer = em.LineIndent;
        AppendTok(g.Open);
        em.NewLine(outer + 1);
        RenderFlow(g.Kids);
        em.NewLine(outer);
        AppendTok(g.Close);
    }

    private void RenderCondGroup(FGroup g)
    {
        var outer = em.LineIndent;
        AppendTok(g.Open);
        em.NewLine(outer + 1);
        RenderCond(g.Kids, outer + 1);
        em.NewLine(outer);
        AppendTok(g.Close);
    }

    private void RenderListGroup(FGroup g, List<Item> items)
    {
        var outer = em.LineIndent;
        AppendTok(g.Open);
        var simple = !g.ForceBreak && items.All(it => it.Nodes.Count == 1 && it.Nodes[0] is FLeaf { IsComment: false } && it.Trail.Count == 0);
        if (simple)
        {
            em.NewLine(outer + 1);
            for (var i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var leaf = (FLeaf)it.Nodes[0];
                var width = leaf.Tok.Out.Length + (it.Comma != null ? 1 : 0);
                if (em.LineHasContent && em.Column + 1 + width > o.MaxLineWidth) em.NewLine(outer + 1);
                AppendTok(leaf.Tok);
                if (it.Comma != null) AppendTok(it.Comma.Tok);
            }
        }
        else
        {
            EmitBroken(items, outer + 1);
        }
        em.NewLine(outer);
        AppendTok(g.Close);
    }

    /// <summary>The group on one line, or null when it cannot be (comment needing a line, subquery).</summary>
    private string? Flat(FGroup g)
    {
        if (g.FlatDone) return g.FlatText;
        g.FlatDone = true;
        if (IsBlockGroup(g)) return g.FlatText = null;
        var saved = em;
        em = new SqlFormatEmitter(o, eol, true);
        try
        {
            AppendTok(g.Open);
            RenderFlow(g.Kids);
            AppendTok(g.Close);
            g.FlatText = em.Failed ? null : em.ToString();
        }
        finally
        {
            em = saved;
        }
        return g.FlatText;
    }

    /// <summary>The nodes on one line, or null.</summary>
    private string? FlatOf(List<FNode> nodes)
    {
        var saved = em;
        em = new SqlFormatEmitter(o, eol, true);
        try
        {
            RenderFlow(nodes);
            return em.Failed ? null : em.ToString();
        }
        finally
        {
            em = saved;
        }
    }

    // ------------------------------------------------------------------ tokens and spacing

    private static readonly HashSet<string> BlockWords = new() { "select", "union", "intersect", "except" };

    private void AppendTok(FTok t)
    {
        if (em.Flat)
        {
            if (t.Kind == FKind.LineComment || t.OwnLine && t.IsComment || t.Raw.Contains('\n') && t.IsComment) { em.Failed = true; return; }
            if (t.Kind == FKind.Word && BlockWords.Contains(t.Lower)) { em.Failed = true; return; }
        }
        else if (em.BreakPending) em.NewLine(em.LineIndent);
        else if (t.IsComment && t.OwnLine && em.LineHasContent) em.NewLine(em.LineIndent);

        var unary = t.Kind == FKind.Op && t.Raw is "-" or "+" or "~" && IsUnaryContext(em.Last);
        if (em.LineHasContent && em.Last != null && NeedSpace(t)) em.Write(" ");
        em.Write(t.Out);
        em.Last = t;
        em.LastUnary = unary;
        if (t.Kind == FKind.LineComment && !em.Flat) em.BreakPending = true;
    }

    private static bool IsUnaryContext(FTok? prev)
    {
        if (prev == null) return true;
        return prev.Kind switch
        {
            FKind.Open or FKind.Comma or FKind.Op or FKind.Cast => true,
            FKind.Word => prev.Keyword && !SqlFormatKeywords.IsValueKeyword(prev.Lower),
            _ => false,
        };
    }

    private static readonly HashSet<string> SpaceBeforeParen = new()
    {
        "in", "as", "on", "and", "or", "not", "exists", "any", "some", "all", "where", "values", "select", "from", "join", "using", "when",
        "then", "else", "case", "between", "like", "ilike", "is", "set", "union", "intersect", "except", "returning", "into", "distinct",
        "by", "having", "over", "limit", "offset", "key", "unique", "check", "default", "constraint", "with", "inner", "outer", "apply",
        "lateral", "filter", "table", "if", "references", "similar", "to", "output", "top", "update", "delete", "insert", "end", "null",
    };

    /// <summary>Whether a space goes between the last written token and <paramref name="t"/>.</summary>
    private bool NeedSpace(FTok t)
    {
        var prev = em.Last;
        if (prev == null || !em.LineHasContent) return false;
        if (prev.Kind == FKind.Op && t.Kind == FKind.Op) return true;
        if (em.LastUnary) return false;

        switch (t.Kind)
        {
            case FKind.Comma or FKind.Semicolon or FKind.Dot or FKind.Close or FKind.Cast:
                return false;
        }
        if (prev.Kind is FKind.Dot or FKind.Cast) return false;

        if (t.Kind == FKind.Open)
        {
            if (prev.Kind is FKind.Open or FKind.Dot or FKind.Cast) return false;
            if (t.Raw == "[") return prev.Kind is FKind.Comma or FKind.Op;
            return prev.Kind switch
            {
                FKind.Word => prev.Keyword && SpaceBeforeParen.Contains(prev.Lower) || t.SpaceBefore,
                FKind.Close => t.SpaceBefore,
                _ => true,
            };
        }

        if (prev.Kind == FKind.Open) return false;
        if (prev.Kind == FKind.Comma) return true;
        if (t.Kind == FKind.Quoted && prev.Kind == FKind.Word && !t.SpaceBefore && t.Raw[0] == '\'') return false;
        if (t.Kind == FKind.Word && t.Raw[0] == ':' && !t.SpaceBefore && prev.Kind is FKind.Word or FKind.Close) return false;
        if (t.Kind == FKind.Punct || prev.Kind == FKind.Punct) return t.SpaceBefore;
        return true;
    }
}
