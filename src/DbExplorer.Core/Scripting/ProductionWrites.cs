using DbExplorer.Core.Model;
using DbExplorer.Core.Plans;

namespace DbExplorer.Core.Scripting;

/// <summary>
/// Which statements of a run must be confirmed on a production connection (<see cref="ConnectionConfig.IsProduction"/>):
/// everything that is not provably a read. It is <see cref="ReadOnlyGuard"/> (unknown = write) made stricter, because on a
/// writable connection no read-only transaction backs it up. Lexical: a user function with side effects is not detected.
/// </summary>
public static class ProductionWrites
{
    // Functions the read-only guard lets through because a read-only transaction refuses or undoes them.
    private static readonly HashSet<string> WritingFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "nextval", "setval", "set_config", "lo_create", "lo_creat", "lo_unlink", "lo_import", "lo_put", "lo_from_bytea",
        "lowrite", "lo_truncate",
    };

    // T-SQL needs no separator, so these may follow a SELECT in the same batch.
    private static readonly HashSet<string> SqlServerVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "DISABLE", "ENABLE", "RECONFIGURE", "WRITETEXT", "UPDATETEXT", "CHECKPOINT", "SETUSER",
    };

    /// <summary>
    /// The statements of the run that may write. None for an estimated plan, which runs nothing, unless a SQL Server script
    /// touches SHOWPLAN itself (it could switch the plan mode off and run what follows).
    /// </summary>
    public static IReadOnlyList<string> In(IEnumerable<string> statements, DbKind kind, PlanMode plan = PlanMode.None)
    {
        var all = statements.ToList();
        if (plan == PlanMode.Estimated
            && !(kind == DbKind.SqlServer && all.Any(s => s.Contains("SHOWPLAN", StringComparison.OrdinalIgnoreCase))))
            return [];
        return all.Where(s => MayWrite(s, kind)).ToList();
    }

    /// <summary>True unless the statement is provably a read.</summary>
    public static bool MayWrite(string sql, DbKind kind)
    {
        if (ReadOnlyGuard.IsWrite(sql, kind)) return true;
        var tokens = SqlLexer.Tokenize(sql, SqlLexOptions.For(kind));
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == SqlTokenKind.Word)
            {
                var word = sql.Substring(t.Start, t.End - t.Start);
                if (kind == DbKind.SqlServer && SqlServerVerbs.Contains(word)) return true;
                if (WritingFunctions.Contains(word) && IsCall(sql, tokens, i)) return true;
            }
            else if (t.Kind == SqlTokenKind.Quoted && t.End - t.Start > 2 && sql[t.Start] == '"'
                     && WritingFunctions.Contains(sql.Substring(t.Start + 1, t.End - t.Start - 2)) && IsCall(sql, tokens, i))
                return true;
        }
        return false;
    }

    private static bool IsCall(string sql, List<SqlToken> tokens, int i) =>
        i + 1 < tokens.Count && tokens[i + 1].Kind == SqlTokenKind.Other && sql[tokens[i + 1].Start] == '(';
}
