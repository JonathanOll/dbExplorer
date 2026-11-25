using DbExplorer.Core.Model;

namespace DbExplorer.Core.Scripting;

/// <summary>
/// Decides which words are keywords (their case may change) and applies the keyword case. Deliberately conservative: a word that
/// is often a column name (name, key, value, date, text...) is only a keyword in a position where it cannot be an identifier,
/// because a case change of an identifier can break a case-sensitive collation. Function names are never changed (SQLite names an
/// unaliased column after its text).
/// </summary>
internal static class SqlFormatKeywords
{
    private static readonly HashSet<string> Hard = new(StringComparer.Ordinal)
    {
        "select", "from", "where", "group", "by", "having", "order", "limit", "offset", "fetch", "union", "intersect", "except",
        "all", "distinct", "as", "with", "recursive", "insert", "into", "values", "update", "set", "delete", "merge", "using",
        "when", "then", "else", "end", "case", "and", "or", "not", "in", "is", "null", "like", "ilike", "similar", "between",
        "exists", "any", "some", "join", "inner", "left", "right", "full", "outer", "cross", "natural", "on", "create", "table",
        "view", "index", "unique", "primary", "foreign", "references", "default", "constraint", "check", "alter", "add", "drop",
        "column", "truncate", "asc", "desc", "over", "partition", "true", "false", "returning", "if", "cascade", "restrict",
        "temporary", "collate", "escape", "begin", "commit", "rollback", "transaction", "declare", "exec", "execute", "go",
        "print", "return", "while", "lateral", "apply", "explain", "nulls", "current_date",
        "current_time", "current_timestamp",
    };

    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "int", "integer", "bigint", "smallint", "tinyint", "mediumint", "serial", "bigserial", "smallserial", "bit", "boolean", "bool",
        "decimal", "numeric", "real", "float", "double", "money", "smallmoney", "char", "character", "nchar", "varchar", "nvarchar",
        "varchar2", "text", "ntext", "mediumtext", "longtext", "clob", "blob", "bytea", "binary", "varbinary", "date", "time",
        "timestamp", "timestamptz", "datetime", "datetime2", "smalldatetime", "datetimeoffset", "interval", "uuid",
        "uniqueidentifier", "json", "jsonb", "xml", "image", "geometry", "geography", "sql_variant", "citext", "int2", "int4", "int8",
        "float4", "float8",
    };

    private static readonly HashSet<string> TypeContinuation = new(StringComparer.Ordinal) { "precision", "varying", "zone", "time", "unsigned", "signed" };

    private static readonly HashSet<string> ColumnDefStarters = new(StringComparer.Ordinal)
    {
        "constraint", "primary", "foreign", "unique", "check", "key", "index", "exclude", "like", "fulltext",
    };

    /// <summary>Keywords that start a value, not an operator: a "-" after them is a subtraction.</summary>
    public static bool IsValueKeyword(string lower) => lower is "null" or "true" or "false" or "end" or "current_date" or "current_time" or "current_timestamp";

    /// <summary>Marks keywords and types, then sets <see cref="FTok.Out"/> for every word of the statement.</summary>
    public static void Apply(List<FTok> toks, DbKind kind, KeywordCase mode)
    {
        var sig = new List<FTok>(toks.Count);
        foreach (var t in toks) if (!t.IsComment) sig.Add(t);

        var stack = new Stack<string>();
        var createTable = IsCreateTable(sig);
        var colListDepth = -1;
        var itemIndex = 0;
        var itemFirstOk = false;
        FTok? prevTypeTok = null;

        for (var k = 0; k < sig.Count; k++)
        {
            var t = sig[k];
            var prev = k > 0 ? sig[k - 1] : null;
            var next = k + 1 < sig.Count ? sig[k + 1] : null;

            if (t.Kind == FKind.Open)
            {
                if (createTable && colListDepth < 0 && stack.Count == 0 && t.Raw == "(" && prev is { Kind: FKind.Word or FKind.Quoted or FKind.Dot })
                {
                    colListDepth = stack.Count + 1;
                    itemIndex = 0;
                }
                stack.Push(prev is { Kind: FKind.Word } ? prev.Lower : "");
                continue;
            }
            if (t.Kind == FKind.Close)
            {
                if (stack.Count == colListDepth) colListDepth = -2;
                if (stack.Count > 0) stack.Pop();
                continue;
            }

            var inColList = colListDepth > 0 && stack.Count == colListDepth;
            if (inColList && t.Kind == FKind.Comma) { itemIndex = 0; prevTypeTok = null; continue; }

            if (t.Kind != FKind.Word)
            {
                if (inColList) { if (itemIndex == 0) itemFirstOk = t.Kind == FKind.Quoted; itemIndex++; }
                continue;
            }

            var lower = t.Lower;
            var keyword = false;
            var isType = false;
            if (!char.IsLetter(t.Raw[0]) && t.Raw[0] != '_') { }
            else if (prev is { Kind: FKind.Dot } || next is { Kind: FKind.Dot }) { }
            else
            {
                keyword = IsKeywordWord(lower, prev, k > 1 ? sig[k - 2] : null, next, kind);
                if (!keyword)
                {
                    if (Types.Contains(lower))
                    {
                        if (prev is { Kind: FKind.Cast }) isType = true;
                        else if (prev is { Kind: FKind.Word } && prev.Lower == "as" && stack.Count > 0 && stack.Peek() is "cast" or "try_cast" or "safe_cast") isType = true;
                        else if (inColList && itemIndex == 1 && itemFirstOk) isType = true;
                    }
                    if (!isType && prevTypeTok != null && ReferenceEquals(prev, prevTypeTok) && TypeContinuation.Contains(lower)) isType = true;
                    if (!isType && lower is "time" or "zone" && prev is { Kind: FKind.Word } && prev.Lower is "with" or "without" or "time") isType = true;
                }
            }

            t.Keyword = keyword || isType;
            if (isType) prevTypeTok = t;
            if (inColList)
            {
                if (itemIndex == 0) itemFirstOk = !ColumnDefStarters.Contains(lower);
                itemIndex++;
            }
        }

        foreach (var t in toks)
        {
            if (t.Kind != FKind.Word) continue;
            t.Out = !t.Keyword || mode == KeywordCase.Keep ? t.Raw : mode == KeywordCase.Upper ? t.Raw.ToUpperInvariant() : t.Lower;
        }
    }

    private static bool IsKeywordWord(string lower, FTok? prev, FTok? prev2, FTok? next, DbKind kind)
    {
        switch (lower)
        {
            case "top": return kind == DbKind.SqlServer && !(next is { Kind: FKind.Comma or FKind.Dot });
            case "output": return kind == DbKind.SqlServer;
            case "returning": return kind != DbKind.SqlServer;
            case "left" or "right": return next is not { Kind: FKind.Open };
            case "key": return prev is { Kind: FKind.Word } && prev.Lower is "primary" or "foreign";
            case "first" or "last": return prev is { Kind: FKind.Word } && prev.Lower is "nulls" or "fetch";
            case "next": return prev is { Kind: FKind.Word } && prev.Lower == "fetch";
            case "row" or "rows": return prev is { Kind: FKind.Number } or { Kind: FKind.Word, Lower: "first" or "next" };
            case "only": return prev is { Kind: FKind.Word } && prev.Lower is "row" or "rows";
            case "conflict": return prev is { Kind: FKind.Word } && prev.Lower == "on";
            case "nothing": return prev is { Kind: FKind.Word } && prev.Lower == "do";
            case "do": return next is { Kind: FKind.Word } && next.Lower is "update" or "nothing";
            case "matched": return prev is { Kind: FKind.Word } && prev.Lower is "when" or "not";
            case "analyze" or "verbose": return prev is { Kind: FKind.Word } && prev.Lower is "explain" or "analyze";
            case "replace": return prev is { Kind: FKind.Word } && prev.Lower == "or";
            case "for": return next is { Kind: FKind.Word } && next.Lower is "update" or "share" or "no";
            case "go": return kind == DbKind.SqlServer;
            case "ties": return prev is { Kind: FKind.Word } && prev.Lower == "with" && prev2 is { Kind: FKind.Number or FKind.Close };
            case "percent": return prev is { Kind: FKind.Number or FKind.Close };
            case "target" or "source": return prev is { Kind: FKind.Word } && prev.Lower == "by" && prev2 is { Kind: FKind.Word } && prev2.Lower == "matched";
            case "glob": return prev is { Kind: FKind.Word or FKind.Quoted or FKind.Close };
        }
        return Hard.Contains(lower);
    }

    private static bool IsCreateTable(List<FTok> sig)
    {
        if (sig.Count < 2 || !sig[0].Is("create")) return false;
        for (var k = 1; k < Math.Min(sig.Count, 8); k++)
        {
            if (sig[k].Is("table")) return true;
            if (sig[k].Kind != FKind.Word) return false;
        }
        return false;
    }
}
