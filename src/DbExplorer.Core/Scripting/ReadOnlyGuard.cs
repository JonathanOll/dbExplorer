using DbExplorer.Core.Model;

namespace DbExplorer.Core.Scripting;

/// <summary>
/// Best-effort check used by read-only connections. It is deliberately conservative: anything that is not
/// recognised as a plain read is reported as a write. It is lexical: it only knows a fixed deny-list of functions
/// with side effects (server control, statistics reset, session advisory locks, dblink / linked-server access,
/// server-side file and replication-slot functions, NEXT VALUE FOR), matched by exact name when followed by "(", also
/// when schema-qualified or double-quoted. Any other function with side effects, and obfuscated forms, are not detected.
/// The guard also gates ordinary read-only connections and the raw WHERE box, which is intended.
/// </summary>
public static class ReadOnlyGuard
{
    // Words that make a statement more than a read. They are looked for anywhere in a statement that starts with a
    // read keyword, because T-SQL does not require separators ("SELECT 1 DELETE FROM t" is two statements) and
    // because of data-modifying CTEs, EXPLAIN ANALYZE of DML, SELECT INTO...
    private static readonly HashSet<string> WriteVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "DROP", "CREATE", "ALTER", "RENAME",
        "GRANT", "REVOKE", "DENY", "EXEC", "EXECUTE", "CALL", "INTO",
        "DBCC", "SHUTDOWN", "KILL", "BACKUP", "RESTORE", "VACUUM", "REINDEX", "ATTACH", "DETACH",
    };

    // Functions callable from a plain SELECT that still have side effects (or reach outside the connection). Exact names,
    // matched when followed by "(" (a column or table with the same name is fine). Deliberately absent: nextval/setval,
    // set_config, lo_create/lo_unlink (PostgreSQL refuses or rolls those back in a read-only transaction) and the
    // transaction-scoped advisory locks (pg_advisory_xact_lock...), which release at the end of the transaction.
    private static readonly HashSet<string> SideEffectFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        // PostgreSQL: server control
        "pg_terminate_backend", "pg_cancel_backend", "pg_reload_conf", "pg_rotate_logfile", "pg_switch_wal",
        "pg_create_restore_point", "pg_promote", "pg_wal_replay_pause", "pg_wal_replay_resume",
        // PostgreSQL: statistics reset
        "pg_stat_reset", "pg_stat_reset_shared", "pg_stat_reset_single_table_counters",
        "pg_stat_reset_single_function_counters", "pg_stat_reset_slru", "pg_stat_reset_replication_slot",
        // PostgreSQL: session-level advisory locks
        "pg_advisory_lock", "pg_advisory_lock_shared", "pg_try_advisory_lock", "pg_try_advisory_lock_shared",
        // PostgreSQL: dblink (runs statements on another database)
        "dblink", "dblink_exec", "dblink_send_query", "dblink_connect", "dblink_connect_u",
        // PostgreSQL: server-side files, replication
        "lo_export", "pg_file_write", "pg_file_rename", "pg_file_unlink",
        "pg_drop_replication_slot", "pg_create_physical_replication_slot", "pg_create_logical_replication_slot",
        "pg_replication_slot_advance", "pg_logical_emit_message", "pg_copy_physical_replication_slot",
        "pg_copy_logical_replication_slot",
        // PostgreSQL: consuming reads of a logical slot (they advance it); the _peek_ variants do not consume and stay allowed
        "pg_logical_slot_get_changes", "pg_logical_slot_get_binary_changes",
        // PostgreSQL 15+/17+: more statistics resets
        "pg_stat_reset_subscription_stats", "pg_stat_reset_backend_stats",
        // SQL Server: linked servers / ad-hoc remote sources
        "OPENQUERY", "OPENROWSET", "OPENDATASOURCE",
    };

    // SQLite pragmas that only read, whatever their arguments.
    private static readonly HashSet<string> ReadPragmas = new(StringComparer.OrdinalIgnoreCase)
    {
        "table_info", "table_xinfo", "table_list", "index_list", "index_info", "index_xinfo",
        "foreign_key_list", "foreign_key_check", "database_list", "collation_list", "compile_options",
        "function_list", "module_list", "pragma_list", "integrity_check", "quick_check",
    };

    // SQLite pragmas that read when used bare and write when given a value: PRAGMA x, but not PRAGMA x(v) or x = v.
    private static readonly HashSet<string> BareReadPragmas = new(StringComparer.OrdinalIgnoreCase)
    {
        "user_version", "application_id", "schema_version", "data_version", "journal_mode", "foreign_keys",
        "encoding", "page_size", "page_count", "freelist_count", "cache_size", "synchronous", "temp_store",
        "locking_mode", "auto_vacuum", "busy_timeout", "read_uncommitted", "recursive_triggers", "secure_delete",
    };

    private static readonly DbKind[] AllKinds = [DbKind.Postgres, DbKind.SqlServer, DbKind.Sqlite];

    /// <summary>
    /// Kind-agnostic check: a write if ANY database dialect's lexing of the text finds one. The dialects disagree on
    /// what is a string or a comment (E'\'' , nested comments, $$ quotes, [brackets]); taking the union means the
    /// guard can never treat a span as opaque when the real database would not.
    /// </summary>
    public static bool IsWrite(string sql) => AllKinds.Any(k => IsWrite(sql, k));

    /// <summary>
    /// True unless the first keyword (after comments) is SELECT, WITH, EXPLAIN, SHOW or VALUES with no write verb
    /// anywhere in the statement, or a read form of PRAGMA. Every ';'-separated statement is checked, lexed with the
    /// rules of <paramref name="kind"/>. Text with no statement at all counts as a write.
    /// </summary>
    public static bool IsWrite(string sql, DbKind kind)
    {
        var tokens = SqlLexer.Tokenize(sql, SqlLexOptions.For(kind));

        var any = false;
        var start = 0;
        for (var i = 0; i <= tokens.Count; i++)
        {
            if (i < tokens.Count && tokens[i].Kind != SqlTokenKind.Semicolon) continue;
            if (i > start)
            {
                any = true;
                if (IsWriteStatement(sql, tokens, start, i)) return true;
            }
            start = i + 1;
        }
        return !any;
    }

    private static bool IsWriteStatement(string sql, List<SqlToken> tokens, int from, int to)
    {
        var i = from;
        while (i < to && tokens[i].Kind == SqlTokenKind.Other && sql[tokens[i].Start] == '(') i++;
        if (i >= to || tokens[i].Kind != SqlTokenKind.Word) return true;

        switch (Word(sql, tokens[i]).ToUpperInvariant())
        {
            case "SELECT":
            case "WITH":
            case "EXPLAIN":
            case "SHOW":
            case "VALUES":
                return HasWriteVerb(sql, tokens, i + 1, to);
            case "PRAGMA":
                return IsWritePragma(sql, tokens, i + 1, to);
            default:
                return true;
        }
    }

    /// <summary>PRAGMA [schema.]name [= value | (value)]: only whitelisted read forms are allowed.</summary>
    private static bool IsWritePragma(string sql, List<SqlToken> tokens, int from, int to)
    {
        var i = from;
        if (i >= to || tokens[i].Kind != SqlTokenKind.Word) return true;
        var name = Word(sql, tokens[i]);
        i++;
        if (i + 1 < to && IsChar(sql, tokens[i], '.') && tokens[i + 1].Kind == SqlTokenKind.Word)
        {
            name = Word(sql, tokens[i + 1]);   // PRAGMA main.table_info(t)
            i += 2;
        }

        if (i < to)
        {
            if (!IsChar(sql, tokens[i], '(')) return true;   // "= value" or anything unexpected
            return !ReadPragmas.Contains(name);
        }
        return !ReadPragmas.Contains(name) && !BareReadPragmas.Contains(name);
    }

    private static bool IsChar(string sql, SqlToken t, char c) => t.Kind == SqlTokenKind.Other && sql[t.Start] == c;

    private static string Word(string sql, SqlToken t) => sql.Substring(t.Start, t.End - t.Start);

    private static bool HasWriteVerb(string sql, List<SqlToken> tokens, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            if (IsSideEffectCall(sql, tokens, i, to)) return true;
            if (tokens[i].Kind != SqlTokenKind.Word) continue;
            var word = Word(sql, tokens[i]);
            if (!WriteVerbs.Contains(word)) continue;

            // "SELECT ... FOR UPDATE" / "FOR NO KEY UPDATE" only lock rows; they are not writes.
            if (word.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) && i > from
                && tokens[i - 1].Kind == SqlTokenKind.Word
                && Word(sql, tokens[i - 1]) is var prev
                && (prev.Equals("FOR", StringComparison.OrdinalIgnoreCase) || prev.Equals("KEY", StringComparison.OrdinalIgnoreCase)))
                continue;

            return true;
        }
        return false;
    }

    /// <summary>A deny-listed function name followed by "(" (plain or double-quoted / bracketed / backticked), or NEXT VALUE FOR.</summary>
    private static bool IsSideEffectCall(string sql, List<SqlToken> tokens, int i, int to)
    {
        var t = tokens[i];
        if (t.Kind == SqlTokenKind.Word)
        {
            if (i + 2 < to && Word(sql, t).Equals("NEXT", StringComparison.OrdinalIgnoreCase)
                && tokens[i + 1].Kind == SqlTokenKind.Word && Word(sql, tokens[i + 1]).Equals("VALUE", StringComparison.OrdinalIgnoreCase)
                && tokens[i + 2].Kind == SqlTokenKind.Word && Word(sql, tokens[i + 2]).Equals("FOR", StringComparison.OrdinalIgnoreCase))
                return true;   // OFFSET n ROWS FETCH NEXT n ROWS ONLY has a number after NEXT, so it never matches
            return i + 1 < to && IsChar(sql, tokens[i + 1], '(') && SideEffectFunctions.Contains(Word(sql, t));
        }

        // A quoted identifier ("pg_terminate_backend"(1), [OPENQUERY](...)); never a string literal or a $$ body.
        if (t.Kind == SqlTokenKind.Quoted && t.End - t.Start > 2 && i + 1 < to && IsChar(sql, tokens[i + 1], '(')
            && sql[t.Start] is '"' or '[' or '`')
            return SideEffectFunctions.Contains(sql.Substring(t.Start + 1, t.End - t.Start - 2));
        return false;
    }
}
