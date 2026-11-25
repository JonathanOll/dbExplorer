using System.Text;
using System.Text.RegularExpressions;
using DbExplorer.Core.Metadata;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Scripting;

public enum CompletionKind { Keyword, Table, View, Column, Schema, Alias, Function }

/// <param name="Text">What replaces the typed text: already quoted for the dialect when needed.</param>
/// <param name="Label">What the list shows.</param>
/// <param name="Detail">A column's type and key flags, a table's schema, an alias's table; null when there is nothing to add.</param>
/// <param name="ReplaceStart">Offset in the script where the replaced text starts (on the opening quote when one was typed).</param>
/// <param name="ReplaceLength">Length of the replaced text, up to the caret.</param>
/// <param name="FilterText">What the typed prefix is matched against when it is not <paramref name="Label"/> (a qualified column).</param>
public sealed record CompletionItem(
    string Text, string Label, CompletionKind Kind, string? Detail, int ReplaceStart, int ReplaceLength, string? FilterText = null)
{
    public string Filter => FilterText ?? Label;
}

/// <summary>The tables and views the completion may offer: what is already loaded, never fetched while typing.</summary>
public interface ICompletionSchema
{
    IReadOnlyList<TableInfo> Tables { get; }
}

public sealed class TableListSchema(IReadOnlyList<TableInfo> tables) : ICompletionSchema
{
    public IReadOnlyList<TableInfo> Tables { get; } = tables;
}

/// <summary>
/// SQL autocomplete as a pure function of the script text, the caret, the loaded schema and the dialect. Only the statement
/// under the caret is analysed (the splitting rules of Ctrl+Enter). Nothing is offered inside a string literal or a comment.
/// Limits: no CTE or sub-query column lists, no user functions, a sub-query sees the tables of the whole statement.
/// </summary>
public static class SqlCompletion
{
    public const int MaxItems = 200;

    private enum Clause { Object, Column, Other }

    private sealed record Use(string? Schema, string Name, string? Alias);

    private sealed record Cand(string Text, string Label, CompletionKind Kind, string? Detail, string Filter, int Group, int Tie);

    private static readonly HashSet<string> ObjectWords = new(StringComparer.OrdinalIgnoreCase)
        { "FROM", "JOIN", "UPDATE", "INTO", "TABLE", "TRUNCATE" };

    private static readonly HashSet<string> ColumnWords = new(StringComparer.OrdinalIgnoreCase)
        { "SELECT", "WHERE", "ON", "HAVING", "SET", "RETURNING", "OUTPUT", "BY" };

    private static readonly HashSet<string> OtherWords = new(StringComparer.OrdinalIgnoreCase)
        { "VALUES", "CREATE", "ALTER", "DROP", "EXPLAIN", "LIMIT", "OFFSET", "UNION", "INTERSECT", "EXCEPT", "INSERT", "DELETE", "WITH" };

    // Reserved words that are fine as names in practice: quoting them would only add noise.
    private static readonly HashSet<string> LooseWords = new(StringComparer.OrdinalIgnoreCase)
        { "KEY", "COLUMN", "ADD", "PARTITION", "WINDOW", "OUTPUT", "APPLY", "GO", "VIEW", "TOP", "LATERAL", "TRUNCATE", "RETURNING", "NATURAL", "FETCH" };

    private static readonly Regex BlankLine = new(@"\n[ \t\r]*\n", RegexOptions.Compiled);
    private static readonly Regex GoLine = new(@"(?im)^[ \t]*GO[ \t]*\r?$", RegexOptions.Compiled);

    public static IReadOnlyList<CompletionItem> Complete(
        string text, int caret, ICompletionSchema? schema, DbKind kind, int max = MaxItems)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var options = SqlLexOptions.For(kind);
        var (stmtStart, stmtEnd) = StatementRange(text, caret, kind);
        var before = text[stmtStart..caret];
        var caretInStmt = before.Length;

        // A sentinel after the caret tells where the caret is: it becomes its own token, unless a string, a quoted identifier
        // or a comment is still open at the caret (then it is swallowed).
        var tokens = SqlLexer.Tokenize(before + "\0", options);
        var open = !(tokens.Count > 0 && tokens[^1] is { Kind: SqlTokenKind.Other } sentinel && sentinel.Start == caretInStmt);
        int replaceStart;
        string prefix;
        var quoted = false;
        if (open)
        {
            // Only a quoted identifier being typed ("Ord) is completed, never a string literal or a comment.
            if (tokens.Count == 0 || tokens[^1] is not { Kind: SqlTokenKind.Quoted } q || q.End <= caretInStmt || before[q.Start] != '"')
                return [];
            replaceStart = q.Start;
            prefix = before[(q.Start + 1)..];
            quoted = true;
        }
        else
        {
            var ws = before.Length;
            while (ws > 0 && IsIdentChar(before[ws - 1])) ws--;
            prefix = before[ws..];
            replaceStart = ws;
            if (prefix.Length > 0 && char.IsDigit(prefix[0])) return [];
            if (ws > 0 && before[ws - 1] is '@' or '#') return [];
            if (kind == DbKind.SqlServer && before.LastIndexOf('[') is >= 0 and var bracket && before.IndexOf(']', bracket) < 0)
            {
                replaceStart = bracket;   // [Order D: the identifier is still open
                prefix = before[(bracket + 1)..];
                quoted = true;
            }
        }
        var ctx = tokens.Where(t => t.End <= replaceStart).ToList();
        string Word(SqlToken t) => before.Substring(t.Start, t.End - t.Start);
        bool IsChar(SqlToken t, char c) => t.Kind == SqlTokenKind.Other && before[t.Start] == c;

        // The whole statement (aliases are often declared after the caret), without the word being typed.
        var all = text[stmtStart..Math.Max(stmtEnd, caret)];
        if (quoted) all = all[..replaceStart] + " " + all[(replaceStart + 1)..];
        var stmtTokens = SqlLexer.Tokenize(all, options);
        // A dollar-quoted body is only recognised with its closing delimiter, which lies after the caret.
        if (kind == DbKind.Postgres && stmtTokens.Any(t => t.Kind == SqlTokenKind.Quoted && all[t.Start] == '$' && t.Start < caretInStmt && caretInStmt < t.End))
            return [];
        stmtTokens = stmtTokens
            .Where(t => !(t.Kind is SqlTokenKind.Word or SqlTokenKind.Quoted && t.Start < caretInStmt && t.End >= caretInStmt)).ToList();

        // ---- where in the statement the caret is ----
        var clause = Clause.Other;
        var stack = new Stack<Clause>();
        string? objectWord = null;
        for (var i = 0; i < ctx.Count; i++)
        {
            var t = ctx[i];
            if (t.Kind == SqlTokenKind.Word)
            {
                var w = Word(t);
                if (ObjectWords.Contains(w)) { clause = Clause.Object; objectWord = w; }
                else if (ColumnWords.Contains(w)) clause = Clause.Column;
                else if (OtherWords.Contains(w)) clause = Clause.Other;
            }
            else if (IsChar(t, '('))
            {
                var introducesColumns = clause == Clause.Object && string.Equals(objectWord, "INTO", StringComparison.OrdinalIgnoreCase)
                    && i > 0 && ctx[i - 1].Kind is SqlTokenKind.Word or SqlTokenKind.Quoted;
                stack.Push(clause);
                clause = introducesColumns ? Clause.Column : Clause.Other;
            }
            else if (IsChar(t, ')') && stack.Count > 0) clause = stack.Pop();
        }

        var tables = schema?.Tables ?? [];
        var length = caret - replaceStart;
        var cands = new List<Cand>();

        if (ctx.Count > 0 && IsChar(ctx[^1], '.'))
        {
            var qualifiers = Qualifiers(ctx, before);
            if (qualifiers.Count == 0) return [];
            Qualified(cands, qualifiers, clause == Clause.Object, tables, TableUses(stmtTokens, all), kind, quoted);
            return Rank(cands, prefix, replaceStart, length, max, objectsFirst: false);
        }

        if (ctx.Count == 0)
        {
            foreach (var k in SqlCompletionVocabulary.StatementKeywords(kind)) cands.Add(Keyword(k, 0));
            return Rank(cands, prefix, replaceStart, length, max, objectsFirst: false);
        }

        var previous = ctx[^1];
        var expectsName = (previous.Kind == SqlTokenKind.Word && ObjectWords.Contains(Word(previous)))
                          || (IsChar(previous, ',') && clause == Clause.Object);
        if (clause == Clause.Object && expectsName)
        {
            AddObjects(cands, tables, kind, quoted);
            foreach (var k in SqlCompletionVocabulary.Keywords(kind)) cands.Add(Keyword(k, 2));
            return Rank(cands, prefix, replaceStart, length, max, objectsFirst: true);
        }

        if (clause == Clause.Column)
        {
            AddColumns(cands, tables, TableUses(stmtTokens, all), kind, quoted);
            foreach (var f in SqlCompletionVocabulary.Functions(kind)) cands.Add(Function(f, 2));
        }
        else if (clause == Clause.Other)
        {
            foreach (var f in SqlCompletionVocabulary.Functions(kind)) cands.Add(Function(f, 1));
        }
        foreach (var k in SqlCompletionVocabulary.Keywords(kind)) cands.Add(Keyword(k, 3));
        return Rank(cands, prefix, replaceStart, length, max, objectsFirst: false);
    }

    // ---- the statement under the caret ----

    private static (int Start, int End) StatementRange(string text, int caret, DbKind kind)
    {
        ScriptStatement? current = null;
        foreach (var s in ScriptSplitter.SplitAtBlankLines(text, kind))
        {
            if (s.Start > caret) break;
            current = s;
        }
        if (current is null) return (caret, caret);
        if (caret <= current.End) return (current.Start, current.End);

        // Past the end of the statement: still inside it (a new line of the same statement), unless a separator lies between.
        var gap = text.Substring(current.End, caret - current.End);
        var separated = SqlLexer.Tokenize(gap, SqlLexOptions.For(kind)).Any(t => t.Kind == SqlTokenKind.Semicolon)
            || BlankLine.IsMatch(gap)
            || (kind == DbKind.SqlServer && GoLine.IsMatch(gap));
        return separated ? (caret, caret) : (current.Start, caret);
    }

    // ---- tables used by the statement ----

    private static List<Use> TableUses(List<SqlToken> tokens, string s)
    {
        var uses = new List<Use>();
        string Raw(SqlToken t) => s.Substring(t.Start, t.End - t.Start);
        string? Ident(int i) => i < tokens.Count ? Unquote(tokens[i], s) : null;
        bool IsKeyword(int i, string w) => i < tokens.Count && tokens[i].Kind == SqlTokenKind.Word && string.Equals(Raw(tokens[i]), w, StringComparison.OrdinalIgnoreCase);
        bool IsChar(int i, char c) => i < tokens.Count && tokens[i].Kind == SqlTokenKind.Other && s[tokens[i].Start] == c;
        bool IsReservedWord(int i) => tokens[i].Kind == SqlTokenKind.Word && SqlCompletionVocabulary.Reserved.Contains(Raw(tokens[i]));

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != SqlTokenKind.Word || !ObjectWords.Contains(Raw(tokens[i]))) continue;
            var allowAlias = !IsKeyword(i, "TABLE");
            var list = IsKeyword(i, "FROM");

            var j = i + 1;
            while (Ident(j) is { } first && !IsReservedWord(j))
            {
                var parts = new List<string> { first };
                while (IsChar(j + 1, '.') && Ident(j + 2) is { } next) { parts.Add(next); j += 2; }
                string? alias = null;
                if (allowAlias)
                {
                    if (IsKeyword(j + 1, "AS") && Ident(j + 2) is { } named) { alias = named; j += 2; }
                    else if (Ident(j + 1) is { } bare && !IsReservedWord(j + 1)) { alias = bare; j++; }
                }
                uses.Add(new Use(parts.Count > 1 ? parts[^2] : null, parts[^1], alias));
                if (!list || !IsChar(j + 1, ',')) break;
                j += 2;
            }
        }
        return uses;
    }

    /// <summary>The name a Word or a quoted identifier stands for; null for a string literal and anything else.</summary>
    private static string? Unquote(SqlToken t, string s)
    {
        var raw = s.Substring(t.Start, t.End - t.Start);
        if (t.Kind == SqlTokenKind.Word) return raw;
        if (t.Kind != SqlTokenKind.Quoted || raw.Length < 2) return null;
        return raw[0] switch
        {
            '"' when raw[^1] == '"' => raw[1..^1].Replace("\"\"", "\""),
            '[' when raw[^1] == ']' => raw[1..^1].Replace("]]", "]"),
            _ => null,
        };
    }

    // The qualifier chain in front of the caret's dot, last part first: "o." gives [o], "sales.orders." gives [orders, sales].
    private static List<string> Qualifiers(List<SqlToken> ctx, string s)
    {
        var parts = new List<string>();
        var i = ctx.Count - 2;
        while (i >= 0 && parts.Count < 2 && Unquote(ctx[i], s) is { } name)
        {
            parts.Add(name);
            if (i >= 2 && ctx[i - 1].Kind == SqlTokenKind.Other && s[ctx[i - 1].Start] == '.') i -= 2;
            else break;
        }
        return parts;
    }

    // ---- candidates ----

    private static Cand Keyword(string k, int group) => new(k, k, CompletionKind.Keyword, null, k, group, 0);

    private static Cand Function(string f, int group) => new(f + "(", f, CompletionKind.Function, "function", f, group, 0);

    private static string DefaultSchema(DbKind kind) => kind switch { DbKind.Postgres => "public", DbKind.SqlServer => "dbo", _ => "" };

    private static TableInfo? FindTable(IReadOnlyList<TableInfo> tables, string? schema, string name, DbKind kind)
    {
        TableInfo? fallback = null;
        foreach (var t in tables)
        {
            if (!string.Equals(t.Ref.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (schema is not null)
            {
                if (string.Equals(t.Ref.Schema, schema, StringComparison.OrdinalIgnoreCase)) return t;
                continue;
            }
            if (t.Ref.Schema is null || string.Equals(t.Ref.Schema, DefaultSchema(kind), StringComparison.OrdinalIgnoreCase)) return t;
            fallback ??= t;
        }
        return fallback;
    }

    private static void AddObjects(List<Cand> cands, IReadOnlyList<TableInfo> tables, DbKind kind, bool force)
    {
        var schemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables)
        {
            var schema = t.Ref.Schema;
            var plain = schema is null || string.Equals(schema, DefaultSchema(kind), StringComparison.OrdinalIgnoreCase);
            var text = plain ? Fmt(t.Ref.Name, kind, force) : Fmt(schema!, kind, force) + "." + Fmt(t.Ref.Name, kind, force);
            cands.Add(new Cand(text, t.Ref.Name, t.IsView ? CompletionKind.View : CompletionKind.Table, schema, t.Ref.Name, 0, 0));
            if (schema is not null && schemas.Add(schema))
                cands.Add(new Cand(Fmt(schema, kind, force), schema, CompletionKind.Schema, "schema", schema, 1, 0));
        }
    }

    private static void Qualified(
        List<Cand> cands, List<string> q, bool objectContext, IReadOnlyList<TableInfo> tables, List<Use> uses, DbKind kind, bool force)
    {
        void TablesOf(string schema)
        {
            foreach (var t in tables)
                if (string.Equals(t.Ref.Schema, schema, StringComparison.OrdinalIgnoreCase))
                    cands.Add(new Cand(Fmt(t.Ref.Name, kind, force), t.Ref.Name, t.IsView ? CompletionKind.View : CompletionKind.Table, t.Ref.Schema, t.Ref.Name, 0, 0));
        }

        var name = q[0];
        if (objectContext)
        {
            TablesOf(name);
            return;
        }

        TableInfo? table;
        if (q.Count > 1) table = FindTable(tables, q[1], name, kind);
        else
        {
            var use = uses.FirstOrDefault(u => u.Alias is not null && string.Equals(u.Alias, name, StringComparison.OrdinalIgnoreCase))
                      ?? uses.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
            table = use is null ? FindTable(tables, null, name, kind) : FindTable(tables, use.Schema, use.Name, kind);
        }
        if (table is not null) AddColumnsOf(cands, table, null, kind, force, 0);
        else if (q.Count == 1) TablesOf(name);
    }

    private static void AddColumns(List<Cand> cands, IReadOnlyList<TableInfo> tables, List<Use> uses, DbKind kind, bool force)
    {
        var resolved = new List<(TableInfo Info, string Name)>();
        foreach (var use in uses)
        {
            var table = FindTable(tables, use.Schema, use.Name, kind);
            if (table is null) continue;
            var name = use.Alias ?? table.Ref.Name;
            if (!resolved.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))) resolved.Add((table, name));
        }

        // A column name several tables of the statement share is offered once per table, qualified.
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (info, _) in resolved)
            foreach (var c in info.Columns) counts[c.Name] = counts.GetValueOrDefault(c.Name) + 1;

        for (var i = 0; i < resolved.Count; i++)
        {
            var (info, name) = resolved[i];
            AddColumnsOf(cands, info, c => counts[c.Name] > 1 ? Fmt(name, kind, false) + "." : null, kind, force, i);
            cands.Add(new Cand(Fmt(name, kind, force), name, CompletionKind.Alias, info.Ref.ToString(), name, 1, i));
        }
    }

    private static void AddColumnsOf(
        List<Cand> cands, TableInfo table, Func<ColumnInfo, string?>? qualifier, DbKind kind, bool force, int tableIndex)
    {
        var foreign = new HashSet<string>(table.ForeignKeys.SelectMany(f => f.Columns), StringComparer.OrdinalIgnoreCase);
        foreach (var c in table.Columns)
        {
            var detail = new StringBuilder(c.DataType);
            if (c.IsPrimaryKey) detail.Append(" · PK");
            else if (foreign.Contains(c.Name)) detail.Append(" · FK");
            var qualified = qualifier?.Invoke(c);
            cands.Add(new Cand(
                (qualified ?? "") + Fmt(c.Name, kind, force), qualified is null ? c.Name : qualified + c.Name,
                CompletionKind.Column, detail.ToString(), c.Name, 0, tableIndex * 10_000 + c.Ordinal));
        }
    }

    // ---- ranking ----

    private static IReadOnlyList<CompletionItem> Rank(
        List<Cand> cands, string prefix, int replaceStart, int replaceLength, int max, bool objectsFirst)
    {
        var scored = new List<(Cand C, int Tier, int Score)>(cands.Count);
        foreach (var c in cands)
        {
            if (prefix.Length == 0 || c.Filter.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) scored.Add((c, 0, 0));
            else if (FuzzyMatcher.Score(prefix, c.Filter) is { } score) scored.Add((c, 1, score));
        }

        // Where the context expects objects, the kind of item leads (tables, then schemas, then keywords); elsewhere the match does:
        // exact prefix first, then fuzzy.
        var ordered = objectsFirst
            ? scored.OrderBy(x => x.C.Group).ThenBy(x => x.Tier)
            : scored.OrderBy(x => x.Tier).ThenBy(x => x.C.Group);
        var items = new List<CompletionItem>();
        var seen = new HashSet<(string, CompletionKind)>();
        foreach (var (c, _, _) in ordered.ThenByDescending(x => x.Score).ThenBy(x => x.C.Tie).ThenBy(x => x.C.Filter, StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Add((c.Text, c.Kind))) continue;
            items.Add(new CompletionItem(c.Text, c.Label, c.Kind, c.Detail, replaceStart, replaceLength, c.Filter == c.Label ? null : c.Filter));
            if (items.Count >= max) break;
        }
        return items;
    }

    // ---- quoting ----

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>The identifier as the dialect wants it written: quoted only when it has to be (or when the user already opened a quote).</summary>
    private static string Fmt(string name, DbKind kind, bool force)
    {
        if (!force && !NeedsQuotes(name, kind)) return name;
        return kind == DbKind.SqlServer ? "[" + name.Replace("]", "]]") + "]" : "\"" + name.Replace("\"", "\"\"") + "\"";
    }

    internal static bool NeedsQuotes(string name, DbKind kind)
    {
        if (name.Length == 0) return true;
        if (!(char.IsLetter(name[0]) || name[0] == '_')) return true;
        foreach (var c in name)
        {
            if (!IsIdentChar(c)) return true;
            if (kind == DbKind.Postgres && char.IsUpper(c)) return true;   // PostgreSQL folds unquoted names to lower case
        }
        return SqlCompletionVocabulary.Reserved.Contains(name) && !LooseWords.Contains(name);
    }
}
