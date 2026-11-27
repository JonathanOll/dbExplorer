using System.Text;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Scripting;

/// <summary>
/// Formats SQL text (a selection or a whole script). The formatter is safe by construction: every statement is laid out from
/// its tokens, then re-tokenised and compared with the original tokens (keywords case-insensitively). Any difference, and any
/// character the lexer cannot classify, leaves the statement as it was (or only lightly normalised). String literals, quoted
/// identifiers, comments, dollar-quoted bodies and GO lines are never rewritten.
/// </summary>
public static class SqlFormatter
{
    private sealed class Unit
    {
        public List<FTok> Tokens { get; } = new();
        public FTok? Terminator { get; set; }
        public List<FTok> Trail { get; } = new();
        public bool IsGo { get; set; }
        public int GapNewlines { get; set; }
    }

    public static string Format(string sql, DbKind kind, SqlFormatOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(sql)) return sql;
        var o = (options ?? SqlFormatOptions.Default).Normalized();
        var lex = SqlLexOptions.For(kind);
        var eol = sql.Contains("\r\n") ? "\r\n" : "\n";
        // A script that changes the delimiter holds stored programs, whose ';' do not end statements: left alone.
        if (lex.MySqlComments
            && SqlLexer.Tokenize(sql, lex with { Delimiters = true }).Any(t => t.Kind is SqlTokenKind.Directive or SqlTokenKind.Executable))
            return sql;
        var toks = FTokenizer.Tokenize(sql, lex);
        if (toks.Count == 0) return sql;

        var units = Split(toks, kind);
        var sb = new StringBuilder();
        var first = true;
        var prevMultiline = false;
        var prevGo = false;
        foreach (var u in units)
        {
            var text = FormatUnit(sql, u, kind, o, lex, eol);
            if (!first)
            {
                var blank = u.GapNewlines >= 2 || (!u.IsGo && !prevGo && (prevMultiline || text.Contains('\n')));
                sb.Append(eol);
                if (blank) sb.Append(eol);
            }
            sb.Append(text);
            first = false;
            prevMultiline = text.Contains('\n');
            prevGo = u.IsGo;
        }
        if (sql.EndsWith('\n')) sb.Append(eol);
        return sb.ToString();
    }

    /// <summary>
    /// Where <paramref name="offset"/> of <paramref name="before"/> lands in <paramref name="after"/> (a formatted copy): the same
    /// number of non-blank characters precede it, so the caret stays on the same token whatever whitespace moved.
    /// </summary>
    public static int MapOffset(string before, string after, int offset)
    {
        offset = Math.Clamp(offset, 0, before.Length);
        var count = 0;
        for (var i = 0; i < offset; i++) if (!char.IsWhiteSpace(before[i])) count++;
        if (count == 0) return 0;
        for (var i = 0; i < after.Length; i++)
        {
            if (char.IsWhiteSpace(after[i])) continue;
            if (--count == 0) return i + 1;
        }
        return after.Length;
    }

    // ------------------------------------------------------------------ splitting

    private static List<Unit> Split(List<FTok> toks, DbKind kind)
    {
        var units = new List<Unit>();
        var cur = new Unit();
        for (var i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (cur.Tokens.Count == 0) cur.GapNewlines = t.NewlinesBefore;
            if (t.Kind == FKind.Semicolon)
            {
                cur.Terminator = t;
                while (i + 1 < toks.Count && toks[i + 1].IsComment && toks[i + 1].NewlinesBefore == 0)
                {
                    cur.Trail.Add(toks[++i]);
                    if (toks[i].Kind == FKind.LineComment) break;
                }
                units.Add(cur);
                cur = new Unit();
                continue;
            }
            if (kind == DbKind.SqlServer && IsGoLine(toks, i, out var end))
            {
                if (cur.Tokens.Count > 0) { units.Add(cur); cur = new Unit { GapNewlines = t.NewlinesBefore }; }
                var go = new Unit { IsGo = true, GapNewlines = t.NewlinesBefore };
                for (var k = i; k < end; k++) go.Tokens.Add(toks[k]);
                units.Add(go);
                i = end - 1;
                cur = new Unit();
                continue;
            }
            cur.Tokens.Add(t);
        }
        if (cur.Tokens.Count > 0) units.Add(cur);
        return units;
    }

    /// <summary>A GO alone on its line (an optional count and trailing comments may follow). <paramref name="end"/> is exclusive.</summary>
    private static bool IsGoLine(List<FTok> toks, int i, out int end)
    {
        end = i;
        var t = toks[i];
        if (!t.Is("go") || i > 0 && t.NewlinesBefore == 0) return false;
        var j = i + 1;
        if (j < toks.Count && toks[j].Kind == FKind.Number && toks[j].NewlinesBefore == 0) j++;
        while (j < toks.Count && toks[j].IsComment && toks[j].NewlinesBefore == 0) j++;
        if (j < toks.Count && toks[j].NewlinesBefore == 0) return false;
        end = j;
        return true;
    }

    // ------------------------------------------------------------------ one statement

    private static string FormatUnit(string sql, Unit u, DbKind kind, SqlFormatOptions o, SqlLexOptions lex, string eol)
    {
        var toks = u.Tokens;
        if (toks.Count == 0) return Terminate("", false, u, o, kind);

        if (toks.Any(t => t.Kind == FKind.Unknown))
            return Terminate(sql.Substring(toks[0].Start, toks[^1].End - toks[0].Start), toks[^1].Kind == FKind.LineComment, u, o, kind);

        SqlFormatKeywords.Apply(toks, kind, o.KeywordCase);
        if (u.IsGo) return string.Join(" ", toks.Select(t => t.Out));

        string? body = null;
        var endsWithLine = false;
        var tree = FTokenizer.BuildTree(toks);
        if (tree != null && SqlFormatLayout.IsFormattable(tree))
        {
            try
            {
                var layout = new SqlFormatLayout(o, kind, eol);
                var text = layout.Run(tree, out endsWithLine);
                if (SameTokens(toks, FTokenizer.Tokenize(text, lex))) body = text;
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or IndexOutOfRangeException or InvalidCastException or NullReferenceException)
            {
                body = null;
            }
        }
        if (body == null)
        {
            body = Light(toks, eol);
            endsWithLine = toks[^1].Kind == FKind.LineComment;
            if (!SameTokens(toks, FTokenizer.Tokenize(body, lex)))
            {
                body = sql.Substring(toks[0].Start, toks[^1].End - toks[0].Start);
            }
        }
        return Terminate(body, endsWithLine, u, o, kind, eol);
    }

    private static string Terminate(string body, bool endsWithLineComment, Unit u, SqlFormatOptions o, DbKind kind, string eol = "\n")
    {
        if (u.Terminator == null) return body;
        var sb = new StringBuilder(body);
        if (endsWithLineComment) sb.Append(eol);
        sb.Append(';');
        foreach (var t in u.Trail) sb.Append(' ').Append(t.Raw);
        return sb.ToString();
    }

    private static bool SameTokens(List<FTok> a, List<FTok> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Kind != b[i].Kind) return false;
            var cmp = a[i].Kind == FKind.Word ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(a[i].Raw, b[i].Raw, cmp)) return false;
        }
        return true;
    }

    /// <summary>
    /// Fallback for statements the layout does not know: runs of spaces collapse to one, line breaks and the indentation of each
    /// line stay, keywords take the keyword case. Nothing else moves.
    /// </summary>
    private static string Light(List<FTok> toks, string eol)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (i > 0)
            {
                if (t.NewlinesBefore > 0)
                {
                    for (var n = 0; n < t.NewlinesBefore; n++) sb.Append(eol);
                    var lastBreak = Math.Max(t.Gap.LastIndexOf('\n'), t.Gap.LastIndexOf('\r'));
                    sb.Append(t.Gap, lastBreak + 1, t.Gap.Length - lastBreak - 1);
                }
                else if (t.Gap.Length > 0) sb.Append(' ');
            }
            sb.Append(t.Out);
        }
        return sb.ToString();
    }
}
