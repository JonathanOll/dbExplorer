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

    // MySQL / MariaDB functions with effects a read-only transaction neither refuses nor undoes. Looked for only there:
    // PostgreSQL's nextval is refused by its read-only transaction.
    private static readonly HashSet<string> MySqlSideEffectFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        // named locks held by the session
        "get_lock", "release_lock", "release_all_locks",
        // MariaDB sequences, and the Spider engine running SQL on other servers
        "nextval", "setval", "spider_direct_sql", "spider_bg_direct_sql", "spider_copy_tables", "spider_flush_table_mon_cache",
        // user-defined functions commonly installed to run commands on the server
        "sys_exec", "sys_eval", "sys_set",
        // server components: replication topology, keyring, locking service, audit log, version tokens, firewall
        "group_replication_set_as_primary", "group_replication_switch_to_single_primary_mode",
        "group_replication_switch_to_multi_primary_mode", "group_replication_set_write_concurrency",
        "group_replication_set_communication_protocol", "asynchronous_connection_failover_add_source",
        "asynchronous_connection_failover_delete_source", "asynchronous_connection_failover_add_managed",
        "asynchronous_connection_failover_delete_managed", "keyring_key_store", "keyring_key_remove", "keyring_key_generate",
        "service_get_read_locks", "service_get_write_locks", "service_release_locks", "audit_log_rotate",
        "audit_log_filter_set_filter", "audit_log_filter_remove_filter", "audit_log_filter_set_user", "audit_log_filter_remove_user",
        "audit_log_encryption_password_set", "version_tokens_set", "version_tokens_edit", "version_tokens_delete",
        "version_tokens_lock_shared", "version_tokens_lock_exclusive", "version_tokens_unlock",
        "mysql_firewall_flush_status", "set_firewall_mode", "set_firewall_group_mode",
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

    private static readonly DbKind[] AllKinds = [DbKind.Postgres, DbKind.SqlServer, DbKind.Sqlite, DbKind.MySql];

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
        // A NUL may end a comment or the text for the server: what follows cannot be judged.
        if (kind == DbKind.MySql && sql.Contains('\0')) return true;
        // MySQL reads quotes as its sql_mode says: a write under any reading is a write.
        return SqlLexOptions.Readings(kind).Any(reading => IsWrite(sql, kind, reading));
    }

    private static bool IsWrite(string sql, DbKind kind, SqlLexOptions reading)
    {
        var tokens = SqlLexer.Tokenize(sql, reading);
        // An executable comment is code, run or not according to the server's version: never a provable read.
        if (tokens.Any(t => t.Kind == SqlTokenKind.Executable)) return true;

        var any = false;
        var start = 0;
        for (var i = 0; i <= tokens.Count; i++)
        {
            if (i < tokens.Count && tokens[i].Kind != SqlTokenKind.Semicolon) continue;
            if (i > start)
            {
                any = true;
                if (IsWriteStatement(sql, tokens, start, i, kind)) return true;
            }
            start = i + 1;
        }
        return !any;
    }

    private static bool IsWriteStatement(string sql, List<SqlToken> tokens, int from, int to, DbKind kind)
    {
        var i = from;
        while (i < to && tokens[i].Kind == SqlTokenKind.Other && sql[tokens[i].Start] == '(') i++;
        if (i >= to || tokens[i].Kind != SqlTokenKind.Word) return true;

        var first = Word(sql, tokens[i]).ToUpperInvariant();
        if (kind == DbKind.MySql)
        {
            switch (first)
            {
                case "SHOW":
                    // SHOW CREATE TABLE names a verb without running it.
                    var next = i + 1 < to && tokens[i + 1].Kind == SqlTokenKind.Word
                        && Word(sql, tokens[i + 1]).Equals("CREATE", StringComparison.OrdinalIgnoreCase) ? i + 2 : i + 1;
                    return HasWriteVerb(sql, tokens, next, to, kind);
                case "DESCRIBE":
                case "DESC":
                case "TABLE":
                    return HasWriteVerb(sql, tokens, i + 1, to, kind);
            }
        }

        switch (first)
        {
            case "SELECT":
            case "WITH":
            case "EXPLAIN":
            case "SHOW":
            case "VALUES":
                return HasWriteVerb(sql, tokens, i + 1, to, kind);
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

    private static bool HasWriteVerb(string sql, List<SqlToken> tokens, int from, int to, DbKind kind)
    {
        for (var i = from; i < to; i++)
        {
            if (IsSideEffectCall(sql, tokens, i, to, kind)) return true;
            if (tokens[i].Kind != SqlTokenKind.Word) continue;
            var word = Word(sql, tokens[i]);
            // INTO OUTFILE writes a file on the server; "INTO" may be glued to a number ("1e1into"), these never are.
            if (kind == DbKind.MySql && (word.Equals("OUTFILE", StringComparison.OrdinalIgnoreCase)
                || word.Equals("DUMPFILE", StringComparison.OrdinalIgnoreCase))) return true;
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
    private static bool IsSideEffectCall(string sql, List<SqlToken> tokens, int i, int to, DbKind kind)
    {
        var t = tokens[i];
        if (t.Kind == SqlTokenKind.Word)
        {
            if (i + 2 < to && Word(sql, t).Equals("NEXT", StringComparison.OrdinalIgnoreCase)
                && tokens[i + 1].Kind == SqlTokenKind.Word && Word(sql, tokens[i + 1]).Equals("VALUE", StringComparison.OrdinalIgnoreCase)
                && tokens[i + 2].Kind == SqlTokenKind.Word && Word(sql, tokens[i + 2]).Equals("FOR", StringComparison.OrdinalIgnoreCase))
                return true;   // OFFSET n ROWS FETCH NEXT n ROWS ONLY has a number after NEXT, so it never matches
            return i + 1 < to && IsChar(sql, tokens[i + 1], '(') && HasSideEffects(Word(sql, t), kind);
        }

        // A quoted identifier ("pg_terminate_backend"(1), [OPENQUERY](...)); never a string literal or a $$ body.
        if (t.Kind == SqlTokenKind.Quoted && t.End - t.Start > 2 && i + 1 < to && IsChar(sql, tokens[i + 1], '(')
            && sql[t.Start] is '"' or '[' or '`')
            return HasSideEffects(sql.Substring(t.Start + 1, t.End - t.Start - 2), kind);
        return false;
    }

    private static bool HasSideEffects(string function, DbKind kind) =>
        SideEffectFunctions.Contains(function) || kind == DbKind.MySql && MySqlSideEffectFunctions.Contains(function);
}
