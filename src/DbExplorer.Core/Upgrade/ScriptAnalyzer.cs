using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Upgrade;

public enum ScriptWarningKind { Index, LongRunning }

/// <param name="Line">1-based line of the keyword that triggered the warning.</param>
public sealed record ScriptWarning(ScriptWarningKind Kind, string Reason, int Line);

/// <summary>
/// Heuristic T-SQL scan that warns about statements likely to be slow or to take locks (index creation or rebuild,
/// unbounded UPDATE/DELETE, loops, WAITFOR, ALTER COLUMN, key constraints, full-text indexes, unbounded SELECT INTO and
/// INSERT ... SELECT, very large scripts).
/// It looks at lexer tokens only, so comments, strings and [bracketed] identifiers are ignored; the text of <c>EXEC('...')</c> and
/// <c>sp_executesql N'...'</c> is scanned with the same rules. It warns and never blocks; <see cref="FindUse"/> finds the one
/// statement an upgrade must refuse (<c>USE</c>).
/// </summary>
public static class ScriptAnalyzer
{
    /// <summary>A script of at least this many characters is reported as a large script.</summary>
    public const int LargeScriptChars = 100_000;

    private static readonly HashSet<string> IndexModifiers = new(StringComparer.OrdinalIgnoreCase)
        { "UNIQUE", "CLUSTERED", "NONCLUSTERED", "COLUMNSTORE", "XML", "SPATIAL", "PRIMARY", "SELECTIVE" };

    // A DML keyword right after one of these is a trigger event, a referential action, a permission or a MERGE action.
    private static readonly HashSet<string> NotDmlAfter = new(StringComparer.OrdinalIgnoreCase)
        { "ON", "OF", "FOR", "AFTER", "INSTEAD", "GRANT", "DENY", "REVOKE", "THEN" };

    // T-SQL has no mandatory terminator, so a statement also ends where one of these starts (GO ends the batch).
    private static readonly HashSet<string> StatementWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "SELECT", "CREATE", "ALTER", "DROP", "TRUNCATE", "EXEC", "EXECUTE",
        "DECLARE", "IF", "ELSE", "WHILE", "BEGIN", "PRINT", "RETURN", "WAITFOR", "USE", "RAISERROR", "THROW", "GO",
    };

    // Dynamic SQL inside dynamic SQL is followed this deep.
    private const int MaxDynamicDepth = 3;

    public static IReadOnlyList<ScriptWarning> Analyze(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return [];

        var found = new List<ScriptWarning>();
        if (sql.Length >= LargeScriptChars)
            found.Add(new ScriptWarning(ScriptWarningKind.LongRunning, $"Large script ({Math.Round(sql.Length / 1024.0)} KB)", 1));

        Inspect(sql, found, out _, 0, 0);

        // OrderBy is stable: the large-script warning (line 1, added first) stays ahead of the others on line 1.
        return found.OrderBy(w => w.Line).ToList();
    }

    /// <summary>
    /// The 1-based line of the first <c>USE &lt;db&gt;</c> statement (also inside <c>EXEC('...')</c> / <c>sp_executesql</c>), or null.
    /// The upgrade runs on databases the user confirmed, and a USE would move the rest of the script to another one, so a script
    /// with a USE is refused (it is a load error of the script, not a warning). Only the statement form counts: USE as a column,
    /// alias or identifier, in a string or in a comment does not.
    /// </summary>
    public static int? FindUse(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return null;
        Inspect(sql, [], out var useLine, 0, 0);
        return useLine;
    }

    /// <summary>The load error of a script whose <see cref="FindUse"/> is <paramref name="line"/>.</summary>
    public static string UseMessage(int line) =>
        $"USE <db> would redirect the run to a database that was not confirmed (line {line}).";

    /// <param name="lineBase">Line of the script that holds <paramref name="sql"/> as a string, minus 1 (0 for the script itself).</param>
    private static void Inspect(string sql, List<ScriptWarning> found, out int? useLine, int lineBase, int depth)
    {
        var inner = new List<ScriptWarning>();
        var scan = new Scan(sql, SqlLexer.Tokenize(sql, SqlLexOptions.For(DbKind.SqlServer)), inner);
        int? use = null;
        for (var i = 0; i < scan.Tokens.Count; i++)
        {
            if (scan.IsWord(i, "CREATE")) scan.CreateIndex(i);
            else if (scan.IsWord(i, "ALTER"))
            {
                if (scan.IsWord(i + 1, "INDEX")) scan.RebuildIndex(i);
                else if (scan.IsWord(i + 1, "TABLE")) scan.AlterTable(i);
                else scan.FullTextIndex(i);
            }
            else if (scan.IsWord(i, "DROP")) scan.FullTextIndex(i);
            else if (scan.IsWord(i, "UPDATE") || scan.IsWord(i, "DELETE")) scan.UnboundedDml(i);
            else if (scan.IsWord(i, "INSERT")) scan.InsertSelect(i);
            else if (scan.IsWord(i, "SELECT")) scan.SelectInto(i);
            else if (scan.IsWord(i, "WHILE")) scan.Add(ScriptWarningKind.LongRunning, "WHILE loop", i);
            else if (scan.IsWord(i, "WAITFOR")) scan.Add(ScriptWarningKind.LongRunning, "WAITFOR delays the script", i);
            else if (scan.IsWord(i, "USE"))
            {
                if (use is null && scan.IsUseStatement(i)) use = scan.LineAt(i);
            }
            else if (depth < MaxDynamicDepth && scan.DynamicSql(i) is var (text, offset))
            {
                Inspect(text, inner, out var innerUse, scan.LineOfOffset(offset) - 1, depth + 1);
                if (use is null && innerUse is { } u) use = u;
            }
        }
        foreach (var w in inner) found.Add(lineBase == 0 ? w : w with { Line = w.Line + lineBase });
        useLine = use is { } line ? line + lineBase : null;
    }

    private sealed class Scan
    {
        private readonly string _sql;
        private readonly List<ScriptWarning> _found;
        private readonly List<int> _lineStarts = [0];

        public Scan(string sql, List<SqlToken> tokens, List<ScriptWarning> found)
        {
            _sql = sql;
            Tokens = tokens;
            _found = found;
            for (var i = 0; i < sql.Length; i++)
                if (sql[i] == '\n') _lineStarts.Add(i + 1);
        }

        public List<SqlToken> Tokens { get; }

        public void Add(ScriptWarningKind kind, string reason, int tokenIndex) =>
            _found.Add(new ScriptWarning(kind, reason, LineOf(Tokens[tokenIndex].Start)));

        public int LineAt(int tokenIndex) => LineOf(Tokens[tokenIndex].Start);

        public int LineOfOffset(int offset) => LineOf(offset);

        private int LineOf(int offset)
        {
            var idx = _lineStarts.BinarySearch(offset);
            return (idx >= 0 ? idx : ~idx - 1) + 1;
        }

        public bool IsWord(int i, string word) =>
            i >= 0 && i < Tokens.Count && Tokens[i].Kind == SqlTokenKind.Word
            && _sql.AsSpan(Tokens[i].Start, Tokens[i].End - Tokens[i].Start).Equals(word, StringComparison.OrdinalIgnoreCase);

        private bool IsWordIn(int i, HashSet<string> words) =>
            i >= 0 && i < Tokens.Count && Tokens[i].Kind == SqlTokenKind.Word
            && words.GetAlternateLookup<ReadOnlySpan<char>>().Contains(_sql.AsSpan(Tokens[i].Start, Tokens[i].End - Tokens[i].Start));

        private bool IsChar(int i, char c) =>
            i >= 0 && i < Tokens.Count && Tokens[i].Kind == SqlTokenKind.Other && _sql[Tokens[i].Start] == c;

        private bool IsSemicolon(int i) => i >= 0 && i < Tokens.Count && Tokens[i].Kind == SqlTokenKind.Semicolon;

        /// <summary>Name of the identifier token at <paramref name="i"/> with brackets or quotes removed, or null.</summary>
        private string? NameAt(int i)
        {
            if (i < 0 || i >= Tokens.Count) return null;
            var t = Tokens[i];
            var text = _sql.Substring(t.Start, t.End - t.Start);
            switch (t.Kind)
            {
                case SqlTokenKind.Word:
                    return text;
                case SqlTokenKind.Quoted when text.Length >= 2 && text[0] is '[' or '"' or '`':
                    return text[1..^1];
                default:
                    return null;
            }
        }

        /// <summary>CREATE / ALTER / DROP FULLTEXT INDEX: building or repopulating a full-text index is heavy.</summary>
        public void FullTextIndex(int at)
        {
            if (!IsWord(at + 1, "FULLTEXT") || !IsWord(at + 2, "INDEX")) return;
            var reason = IsWord(at, "CREATE") ? "Creates a full-text index"
                : IsWord(at, "ALTER") ? "Alters a full-text index" : "Drops a full-text index";
            Add(ScriptWarningKind.Index, reason, at);
        }

        public void CreateIndex(int create)
        {
            if (IsWord(create + 1, "FULLTEXT")) { FullTextIndex(create); return; }
            var j = create + 1;
            for (var mods = 0; mods < 4 && IsWordIn(j, IndexModifiers); mods++) j++;
            if (!IsWord(j, "INDEX")) return;

            var name = IsWord(j + 1, "ON") ? null : NameAt(j + 1);
            Add(ScriptWarningKind.Index, name is null ? "Creates an index" : $"Creates index {name}", create);
        }

        public void RebuildIndex(int alter)
        {
            // ALTER INDEX <name|ALL> ON t REBUILD ...: REBUILD within 12 tokens, in the same statement.
            for (var k = alter + 2; k < Tokens.Count && k <= alter + 12; k++)
            {
                if (IsSemicolon(k) || IsWord(k, "ALTER")) return;
                if (!IsWord(k, "REBUILD")) continue;
                Add(ScriptWarningKind.Index, $"Rebuilds index {NameAt(alter + 2) ?? "ALL"}", alter);
                return;
            }
        }

        /// <summary>
        /// ALTER TABLE: scans the whole statement (a name can have 1 to 4 parts), for ALTER COLUMN (can rewrite the table) and for
        /// PRIMARY KEY / UNIQUE (the constraint builds an index).
        /// </summary>
        public void AlterTable(int alter)
        {
            var depth = 0;
            var column = false;
            string? key = null;
            for (var k = alter + 2; k < Tokens.Count; k++)
            {
                if (IsChar(k, '(')) { depth++; continue; }
                if (IsChar(k, ')')) { if (--depth < 0) break; continue; }
                if (depth > 0) continue;

                if (IsSemicolon(k)) break;
                if (IsWord(k, "ALTER"))
                {
                    if (!IsWord(k + 1, "COLUMN")) break;   // the next ALTER statement
                    column = true;
                    continue;
                }
                if (IsWord(k, "PRIMARY") && IsWord(k + 1, "KEY")) { key ??= "PRIMARY KEY"; continue; }
                if (IsWord(k, "UNIQUE")) { key ??= "UNIQUE"; continue; }

                if (IsWord(k, "DROP") && (IsWord(k + 1, "COLUMN") || IsWord(k + 1, "CONSTRAINT") || IsWord(k + 1, "PERIOD"))) continue;
                if ((IsWord(k, "DELETE") || IsWord(k, "UPDATE")) && IsWord(k - 1, "ON")) continue;   // ON DELETE CASCADE
                if (IsWordIn(k, StatementWords)) break;
            }

            if (column) Add(ScriptWarningKind.LongRunning, "ALTER COLUMN can rewrite the whole table", alter);
            if (key is not null) Add(ScriptWarningKind.Index, $"ADD {key} builds an index", alter);
        }

        private bool IsTempTarget(int i)
        {
            if (IsChar(i, '#') || IsChar(i, '@')) return true;
            return i >= 0 && i < Tokens.Count && Tokens[i].Kind == SqlTokenKind.Quoted
                   && Tokens[i].End - Tokens[i].Start > 1 && _sql[Tokens[i].Start] == '[' && _sql[Tokens[i].Start + 1] is '#' or '@';
        }

        /// <summary>INSERT [INTO] target ... SELECT: copies rows, can be long. Not for #temp tables and @table variables.</summary>
        public void InsertSelect(int at)
        {
            if (IsWordIn(at - 1, NotDmlAfter) || IsChar(at - 1, ',')) return;

            var target = at + 1;
            if (IsWord(target, "INTO")) target++;
            if (IsTempTarget(target)) return;

            var depth = 0;
            for (var k = at + 1; k < Tokens.Count; k++)
            {
                if (IsChar(k, '(')) { depth++; continue; }
                if (IsChar(k, ')')) { if (--depth < 0) return; continue; }
                if (depth > 0) continue;

                if (IsSemicolon(k) || IsWord(k, "VALUES")) return;
                if (IsWord(k, "SELECT"))
                {
                    if (!IsBounded(k + 1, select: true))
                        Add(ScriptWarningKind.LongRunning, "INSERT ... SELECT without WHERE can write many rows", at);
                    return;
                }
                if (IsWordIn(k, StatementWords)) return;
            }
        }

        /// <summary>SELECT ... INTO target: creates and fills a table. Not for #temp tables and @table variables.</summary>
        public void SelectInto(int select)
        {
            var depth = 0;
            for (var k = select + 1; k < Tokens.Count; k++)
            {
                if (IsChar(k, '(')) { depth++; continue; }
                if (IsChar(k, ')')) { if (--depth < 0) return; continue; }
                if (depth > 0) continue;

                if (IsSemicolon(k) || IsWord(k, "FROM") || IsWordIn(k, StatementWords)) return;
                if (!IsWord(k, "INTO")) continue;

                if (!IsTempTarget(k + 1) && !IsBounded(select + 1, select: true))
                    Add(ScriptWarningKind.LongRunning, "SELECT INTO without WHERE creates and fills a table", select);
                return;
            }
        }

        // A USE that is a statement: the first token, after a ';', or first on its line after something that can end a statement,
        // and followed by a database name. A column called use is never followed by a name nor first on its line after a comma.
        private static readonly HashSet<string> NotADatabaseName = new(StringComparer.OrdinalIgnoreCase)
        {
            "FROM", "AS", "WHERE", "JOIN", "ON", "INTO", "SET", "BY", "AND", "OR", "IN", "IS", "NOT", "LIKE", "ORDER", "GROUP",
            "HAVING", "UNION", "EXCEPT", "INTERSECT", "WHEN", "THEN", "ELSE", "END", "INNER", "LEFT", "RIGHT", "FULL", "CROSS",
            "OUTER", "APPLY", "WITH", "OVER", "DESC", "ASC", "VALUES", "SELECT", "BETWEEN", "EXISTS", "TOP",
        };

        private static readonly HashSet<string> ContinuesAStatement = new(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "AS", "BY", "ON", "AND", "OR", "NOT", "IN", "IS", "WHEN", "THEN", "FROM", "JOIN", "WHERE", "SET", "TOP",
            "DISTINCT", "ALL", "INTO", "TABLE", "UPDATE", "DELETE", "INSERT", "EXISTS", "LIKE", "BETWEEN", "CASE", "HAVING",
            "OVER", "PARTITION", "ORDER", "GROUP", "UNION", "EXCEPT", "INTERSECT", "APPLY", "OUTPUT", "VALUES", "REFERENCES",
            "WITH", "RETURNS", "DECLARE", "EXEC", "EXECUTE", "FOR", "OF", "TO",
        };

        public bool IsUseStatement(int at)
        {
            var next = at + 1;
            if (next >= Tokens.Count) return false;
            var nextTok = Tokens[next];
            var isName = nextTok.Kind == SqlTokenKind.Word
                ? !IsWordIn(next, NotADatabaseName)
                : nextTok.Kind == SqlTokenKind.Quoted && _sql[nextTok.Start] is '[' or '"';
            if (!isName) return false;

            if (at == 0 || IsSemicolon(at - 1)) return true;

            var prev = Tokens[at - 1];
            if (LineOf(prev.End - 1) == LineOf(Tokens[at].Start)) return false;   // not first on its line
            if (prev.Kind == SqlTokenKind.Word) return !IsWordIn(at - 1, ContinuesAStatement);
            if (prev.Kind == SqlTokenKind.Other) return !",(.=+-*/<>!&|^~%:@#".Contains(_sql[prev.Start]);
            return true;
        }

        /// <summary>
        /// The SQL text of <c>EXEC('...')</c> / <c>EXECUTE('...')</c> (every string of the parentheses, joined by a space) or of
        /// <c>sp_executesql N'...'</c> (its first string), unescaped, with the offset of the first string; null for anything else.
        /// </summary>
        public (string Text, int Offset)? DynamicSql(int at)
        {
            if ((IsWord(at, "EXEC") || IsWord(at, "EXECUTE")) && IsChar(at + 1, '('))
            {
                var depth = 0;
                var parts = new List<string>();
                var offset = -1;
                for (var k = at + 1; k < Tokens.Count; k++)
                {
                    if (IsChar(k, '(')) depth++;
                    else if (IsChar(k, ')')) { if (--depth == 0) break; }
                    else if (StringLiteral(k) is { } text)
                    {
                        if (offset < 0) offset = Tokens[k].Start;
                        parts.Add(text);
                    }
                }
                return parts.Count == 0 ? null : (string.Join(' ', parts), offset);
            }

            if (IsWord(at, "sp_executesql"))
                for (var k = at + 1; k < Tokens.Count && k <= at + 6; k++)
                {
                    if (IsSemicolon(k)) return null;
                    if (StringLiteral(k) is { } text) return (text, Tokens[k].Start);
                }
            return null;
        }

        /// <summary>The unescaped content of a '...' string token, or null for any other token.</summary>
        private string? StringLiteral(int i)
        {
            if (i < 0 || i >= Tokens.Count || Tokens[i].Kind != SqlTokenKind.Quoted || _sql[Tokens[i].Start] != '\'') return null;
            var raw = _sql.AsSpan(Tokens[i].Start + 1, Tokens[i].End - Tokens[i].Start - 1);
            if (raw.Length > 0 && raw[^1] == '\'') raw = raw[..^1];
            return raw.ToString().Replace("''", "'");
        }

        private static readonly HashSet<string> FromOrJoin = new(StringComparer.OrdinalIgnoreCase) { "FROM", "JOIN" };
        private static readonly HashSet<string> OuterOrCross = new(StringComparer.OrdinalIgnoreCase) { "LEFT", "RIGHT", "FULL", "OUTER", "CROSS" };
        private static readonly HashSet<string> SetOperators = new(StringComparer.OrdinalIgnoreCase) { "UNION", "EXCEPT", "INTERSECT" };
        private static readonly HashSet<string> SetOperatorOrAll = new(StringComparer.OrdinalIgnoreCase) { "UNION", "EXCEPT", "INTERSECT", "ALL" };

        // Per token: true from a CREATE / ALTER [OR ALTER] TRIGGER up to the next GO (a trigger is alone in its batch).
        private bool[]? _inTrigger;

        /// <summary>
        /// True inside a <c>CREATE TRIGGER</c>, from the CREATE to the end of its batch. A trigger body does not run during the
        /// upgrade, only later when its table changes, and its UPDATE / DELETE are meant to be bounded by <c>inserted</c> / <c>deleted</c>.
        /// </summary>
        private bool InTriggerBody(int i)
        {
            if (_inTrigger is null)
            {
                var map = new bool[Tokens.Count];
                var inside = false;
                for (var k = 0; k < Tokens.Count; k++)
                {
                    if (IsWord(k, "GO")) inside = false;
                    else if (IsWord(k, "CREATE") || IsWord(k, "ALTER"))
                    {
                        var j = k + 1;
                        if (IsWord(j, "OR") && IsWord(j + 1, "ALTER")) j += 2;
                        if (IsWord(j, "TRIGGER")) inside = true;
                    }
                    map[k] = inside;
                }
                _inTrigger = map;
            }
            return i >= 0 && i < _inTrigger.Length && _inTrigger[i];
        }

        /// <summary>
        /// <c>inserted</c> / <c>deleted</c> used as a table (after FROM, JOIN or a comma; not <c>inserted.col</c> of an OUTPUT clause):
        /// the statement only touches the rows of the change that fired the trigger.
        /// </summary>
        private bool IsPseudoTable(int k) =>
            (IsWord(k, "inserted") || IsWord(k, "deleted")) && !IsChar(k + 1, '.') && !IsChar(k + 1, '(')
            && (IsWordIn(k - 1, FromOrJoin) || IsChar(k - 1, ','));

        /// <summary>
        /// Walks the rest of a statement from <paramref name="start"/> (the token after UPDATE / DELETE, or after SELECT when
        /// <paramref name="select"/>) and says whether it is bounded: a WHERE or TOP of the statement itself (not of a sub-query),
        /// <c>inserted</c> / <c>deleted</c> used as a table, and, for UPDATE / DELETE only, a TOP inside a derived table of the FROM
        /// clause or a #temp table / @table variable in a FROM or non-outer JOIN (the working set of keys to touch). A SELECT without FROM reads no table, so it is bounded; with UNION / EXCEPT / INTERSECT every branch must be.
        /// </summary>
        private bool IsBounded(int start, bool select)
        {
            var depth = 0;
            var cases = 0;
            var derived = false;
            var bounded = false;
            var from = false;
            var earlierBranches = true;
            for (var k = start; k < Tokens.Count; k++)
            {
                if (IsChar(k, '('))
                {
                    if (depth == 0) derived = !select && (IsWordIn(k - 1, FromOrJoin) || (from && IsChar(k - 1, ',')));
                    depth++;
                    continue;
                }
                if (IsChar(k, ')')) { if (--depth < 0) break; if (depth == 0) derived = false; continue; }
                if (IsPseudoTable(k)) { bounded = true; continue; }
                if (depth > 0)
                {
                    if (derived && IsWord(k, "TOP")) bounded = true;
                    continue;
                }

                if (IsSemicolon(k)) break;
                if (IsWord(k, "CASE")) { cases++; continue; }
                if (IsWord(k, "END")) { if (cases == 0) break; cases--; continue; }
                if (cases > 0) continue;
                if (IsWord(k, "WHERE") || IsWord(k, "TOP")) { bounded = true; continue; }
                if (!select && IsWordIn(k, FromOrJoin) && IsTempTarget(k + 1) && !IsWordIn(k - 1, OuterOrCross)) bounded = true;
                if (IsWord(k, "FROM")) { from = true; continue; }
                if (select && IsWordIn(k, SetOperators))
                {
                    earlierBranches &= bounded || !from;
                    bounded = false;
                    from = false;
                    continue;
                }
                if (select && IsWord(k, "SELECT") && IsWordIn(k - 1, SetOperatorOrAll)) continue;
                if (IsWordIn(k, StatementWords)) break;
            }
            return select ? earlierBranches && (bounded || !from) : bounded;
        }

        public void UnboundedDml(int at)
        {
            var isUpdate = IsWord(at, "UPDATE");
            if (IsWordIn(at - 1, NotDmlAfter) || IsChar(at - 1, ',')) return;
            if (IsWord(at + 1, "STATISTICS") || IsChar(at + 1, '(')) return;

            var target = at + 1;
            if (!isUpdate && IsWord(target, "FROM")) target++;
            if (IsChar(target, '#') || IsChar(target, '@')) return;

            if (InTriggerBody(at) || IsBounded(at + 1, select: false)) return;

            Add(ScriptWarningKind.LongRunning, isUpdate
                ? "UPDATE without WHERE: touches every row"
                : "DELETE without WHERE: touches every row", at);
        }
    }
}
