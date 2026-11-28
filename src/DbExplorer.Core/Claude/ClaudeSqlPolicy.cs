using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Claude;

/// <summary>
/// What SQL an MCP call may run: one SELECT. Far stricter than <see cref="ReadOnlyGuard"/> (which it includes), because
/// nobody reads the text before it runs. A column that shares a refused word works when quoted. The engine's own
/// read-only mode stays behind it; like the guard it cannot see what a user-defined function does.
/// </summary>
public static class ClaudeSqlPolicy
{
    public const int MaxLength = 20_000;

    private static readonly HashSet<string> FirstWords = new(StringComparer.OrdinalIgnoreCase) { "SELECT", "WITH", "VALUES" };

    // Words refused anywhere: row locks (every engine), and on SQL Server what needs no ';' to follow a SELECT.
    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase) { "UPDATE", "LOCK" };

    private static readonly HashSet<string> SqlServerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD", "BEGIN", "BULK", "CHECKPOINT", "CLOSE", "COMMIT", "CONVERSATION", "DEALLOCATE", "DECLARE", "DISABLE", "ENABLE",
        "GOTO", "HOLDLOCK", "IF", "LOAD", "MOVE", "OPEN", "PRINT", "RAISERROR", "READTEXT", "RECEIVE", "RECONFIGURE", "RETURN",
        "REVERT", "ROLLBACK", "SAVE", "SEND", "SET", "SETUSER", "TABLOCK", "TABLOCKX", "THROW", "TRAN", "TRANSACTION",
        "UPDATETEXT", "UPDLOCK", "USE", "WAITFOR", "WHILE", "WRITETEXT", "XLOCK",
    };

    // Functions that read the server's files, run SQL held in a string, hold locks, signal others or only burn time.
    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        // PostgreSQL
        "connectby", "crosstab", "crosstab2", "crosstab3", "crosstab4", "cursor_to_xml", "cursor_to_xmlschema", "lo_export",
        "lo_import", "nextval", "pg_backup_start", "pg_backup_stop", "pg_current_logfile", "pg_log_backend_memory_contexts",
        "pg_notify", "pg_read_binary_file", "pg_read_file", "pg_sleep", "pg_sleep_for", "pg_sleep_until", "pg_start_backup",
        "pg_stat_file", "pg_stop_backup", "pg_switch_xlog", "query_to_xml", "query_to_xml_and_xmlschema", "query_to_xmlschema",
        "set_config", "setval", "ts_rewrite", "ts_stat", "xpath_table",
        // MySQL / MariaDB
        "benchmark", "load_file", "master_pos_wait", "master_gtid_wait", "sleep", "source_pos_wait", "sys_eval", "sys_exec",
        "wait_for_executed_gtid_set", "wait_until_sql_thread_after_gtids",
        // SQL Server: server files and the transaction log
        "dm_os_enumerate_filesystem", "dm_os_file_exists", "fn_dblog", "fn_dump_dblog", "fn_get_audit_file", "fn_trace_gettable",
        "fn_xe_file_target_read_file",
        // SQLite
        "edit", "fts3_tokenizer", "load_extension", "readfile", "writefile",
    };

    private static readonly string[] FunctionPrefixes = ["dblink", "pg_ls_", "pg_advisory", "pg_try_advisory", "postgres_fdw_"];

    private const string OnlyOne = "Only one SELECT statement is allowed.";

    /// <summary>Null when the text may run, else why not.</summary>
    public static string? Refuse(string? sql, DbKind kind)
    {
        if (string.IsNullOrWhiteSpace(sql)) return "The SQL is empty.";
        if (sql.Length > MaxLength) return $"The SQL is longer than {MaxLength} characters.";
        if (sql.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n'))) return "The SQL holds control characters.";

        foreach (var reading in SqlLexOptions.Readings(kind))
            if (RefuseReading(sql, kind, SqlLexer.Tokenize(sql, reading)) is { } why)
                return why;

        if (ReadOnlyGuard.IsWrite(sql, kind) || ProductionWrites.MayWrite(sql, kind))
            return "Refused: only reading is allowed, and this statement may write.";
        return ScriptSplitter.Split(sql, kind).Count == 1 ? null : OnlyOne;
    }

    /// <summary>
    /// On SQL Server and MySQL a login may read other databases of its server by name. Refuses a name qualified by one of
    /// <paramref name="closed"/> (the databases of connections that may not give data, production ones included) and SQL
    /// Server's four-part (linked server) names. Lexical: a view or synonym of the allowed database that points elsewhere
    /// is not seen.
    /// </summary>
    public static string? RefuseOtherDatabase(string sql, DbKind kind, IReadOnlyCollection<string> closed)
    {
        if (kind is not (DbKind.SqlServer or DbKind.MySql)) return null;
        foreach (var reading in SqlLexOptions.Readings(kind))
        {
            var tokens = SqlLexer.Tokenize(sql, reading);
            var dots = 0;   // dots of the qualified name being read
            for (var i = 0; i < tokens.Count; i++)
            {
                if (IsDot(sql, tokens, i))
                {
                    if (++dots >= 3 && kind == DbKind.SqlServer) return "Refused: four-part (linked server) names are not allowed.";
                    continue;
                }
                if (!IsDot(sql, tokens, i - 1) && !IsDot(sql, tokens, i + 1)) { dots = 0; continue; }
                if (!IsDot(sql, tokens, i - 1)) dots = 0;
                if (IsDot(sql, tokens, i + 1) && closed.Contains(Name(sql, tokens[i]), StringComparer.OrdinalIgnoreCase))
                    return $"Refused: {Name(sql, tokens[i])} is a database that Claude Code may not read.";
            }
        }
        return null;
    }

    private static bool IsDot(string sql, List<SqlToken> tokens, int i) =>
        i >= 0 && i < tokens.Count && tokens[i].Kind == SqlTokenKind.Other && sql[tokens[i].Start] == '.';

    private static string Name(string sql, SqlToken t) =>
        t.Kind == SqlTokenKind.Quoted && t.End - t.Start > 2 ? sql.Substring(t.Start + 1, t.End - t.Start - 2) : Text(sql, t);

    private static string? RefuseReading(string sql, DbKind kind, List<SqlToken> tokens)
    {
        var first = 0;
        while (first < tokens.Count && tokens[first].Kind == SqlTokenKind.Other && sql[tokens[first].Start] == '(') first++;
        if (first >= tokens.Count || tokens[first].Kind != SqlTokenKind.Word || !FirstWords.Contains(Text(sql, tokens[first])))
            return "Only a SELECT statement (SELECT, WITH or VALUES) is allowed.";

        var ended = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind is SqlTokenKind.Executable or SqlTokenKind.Directive) return "Executable comments and client directives are not allowed.";
            if (t.Kind == SqlTokenKind.Semicolon) { ended = true; continue; }
            if (ended) return OnlyOne;

            var called = i + 1 < tokens.Count && tokens[i + 1].Kind == SqlTokenKind.Other && sql[tokens[i + 1].Start] == '(';
            if (t.Kind == SqlTokenKind.Word)
            {
                var word = Text(sql, t);
                if (Words.Contains(word) || kind == DbKind.SqlServer && SqlServerWords.Contains(word))
                    return $"Refused: '{word.ToUpperInvariant()}' is not allowed (quote it if it is a column or table name).";
                if (called && IsRefusedFunction(word)) return RefusedFunction(word);
            }
            else if (t.Kind == SqlTokenKind.Quoted && called && t.End - t.Start > 2 && sql[t.Start] is '"' or '[' or '`')
            {
                var name = sql.Substring(t.Start + 1, t.End - t.Start - 2);
                if (IsRefusedFunction(name)) return RefusedFunction(name);
            }
        }
        return null;
    }

    private static bool IsRefusedFunction(string name) =>
        Functions.Contains(name) || FunctionPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static string RefusedFunction(string name) => $"Refused: the function {name} is not allowed.";

    private static string Text(string sql, SqlToken t) => sql[t.Start..t.End];
}
