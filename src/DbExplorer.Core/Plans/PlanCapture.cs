using System.Data;
using System.Data.Common;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Plans;

/// <summary>How a run asks each engine for a plan, and how the plan is picked out of what comes back.</summary>
public static class PlanCapture
{
    /// <summary>The mode the engine can really give (SQLite has no actual plan: it falls back to the estimated one).</summary>
    public static PlanMode Effective(DbKind kind, PlanMode mode) =>
        mode == PlanMode.Actual && kind == DbKind.Sqlite ? PlanMode.Estimated : mode;

    // ---- session options (SQL Server) ----

    internal static string? OnSql(DbKind kind, PlanMode mode) => kind == DbKind.SqlServer
        ? mode switch { PlanMode.Estimated => "SET SHOWPLAN_XML ON", PlanMode.Actual => "SET STATISTICS XML ON", _ => null }
        : null;

    internal static string? OffSql(DbKind kind, PlanMode mode) => kind == DbKind.SqlServer
        ? mode switch { PlanMode.Estimated => "SET SHOWPLAN_XML OFF", PlanMode.Actual => "SET STATISTICS XML OFF", _ => null }
        : null;

    /// <summary>Switches the plan option on for the connection. SHOWPLAN_XML must be alone in its batch: it is its own command.</summary>
    internal static async Task EnableAsync(DbConnection connection, DbKind kind, PlanMode mode, CancellationToken ct)
    {
        if (OnSql(kind, mode) is not { } sql) return;
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Switches it off; false when that failed (the connection is then suspect). While SHOWPLAN_XML is on nothing executes, so this must come first.</summary>
    internal static async Task<bool> DisableAsync(DbConnection connection, DbKind kind, PlanMode mode)
    {
        if (OffSql(kind, mode) is not { } sql) return true;
        if (connection.State != ConnectionState.Open) return true;
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 10;
            await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- MySQL / MariaDB: explain inside something that is rolled back ----

    /// <summary>
    /// The optimizer may evaluate constant parts, stored functions included, while it explains. This opens a transaction
    /// (a savepoint when the script already has one open, which must not be committed) and returns the statement that
    /// undoes whatever they changed. A read-only context is no use: the server then refuses to explain a change.
    /// </summary>
    internal static async Task<string> BeginRolledBackExplainAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = 30;
        async Task Run(string sql)
        {
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // A savepoint only lasts inside a transaction: releasing it fails in autocommit.
        await Run("SAVEPOINT dbx_probe").ConfigureAwait(false);
        try { await Run("RELEASE SAVEPOINT dbx_probe").ConfigureAwait(false); }
        catch (DbException)
        {
            await Run("START TRANSACTION").ConfigureAwait(false);
            return "ROLLBACK";
        }
        await Run("SAVEPOINT dbx_plan").ConfigureAwait(false);
        return "ROLLBACK TO SAVEPOINT dbx_plan";
    }

    /// <summary>Runs the statement <see cref="BeginRolledBackExplainAsync"/> returned; false when that failed.</summary>
    internal static async Task<bool> RollBackExplainAsync(DbConnection connection, string undo)
    {
        if (connection.State != ConnectionState.Open) return true;
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = undo;
            cmd.CommandTimeout = 10;
            await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- statement text (PostgreSQL, SQLite, MySQL / MariaDB) ----

    /// <summary>The text actually sent for a statement under <paramref name="mode"/>.</summary>
    internal static string Wrap(string text, DbKind kind, PlanMode mode, bool mariaDb = false) => (kind, mode) switch
    {
        (_, PlanMode.None) => text,
        (DbKind.MySql, PlanMode.Estimated) => "EXPLAIN FORMAT=JSON " + text,
        // MariaDB: ANALYZE runs the statement and reports in JSON. MySQL only measures in its tree format.
        (DbKind.MySql, PlanMode.Actual) => (mariaDb ? "ANALYZE FORMAT=JSON " : "EXPLAIN ANALYZE FORMAT=TREE ") + text,
        (DbKind.Postgres, PlanMode.Estimated) => "EXPLAIN (FORMAT JSON) " + text,
        (DbKind.Postgres, PlanMode.Actual) => "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + text,
        (DbKind.Sqlite, _) => "EXPLAIN QUERY PLAN " + text,
        _ => text,
    };

    private static readonly string[] PostgresExplainable =
        ["SELECT", "WITH", "VALUES", "TABLE", "INSERT", "UPDATE", "DELETE", "MERGE", "EXECUTE", "DECLARE", "CREATE"];

    /// <summary>Null when the engine can give a plan for the statement, else why not.</summary>
    internal static string? CannotPlan(string text, DbKind kind, PlanMode mode = PlanMode.Estimated, bool mariaDb = false)
    {
        if (kind == DbKind.MySql) return CannotPlanMySql(text, mode, mariaDb);
        if (kind != DbKind.Postgres) return null;
        var word = FirstWord(text);
        if (PostgresExplainable.Contains(word, StringComparer.OrdinalIgnoreCase)) return null;
        var name = word.Length == 0 ? "an empty statement" : word.ToUpperInvariant();
        return $"No execution plan: PostgreSQL only explains SELECT, WITH, VALUES, INSERT, UPDATE, DELETE, MERGE, EXECUTE, DECLARE and CREATE TABLE AS ({name} is none of them).";
    }

    private static readonly string[] MySqlExplainable = ["SELECT", "WITH", "TABLE", "INSERT", "UPDATE", "DELETE", "REPLACE"];

    // MySQL only measures what reads: EXPLAIN ANALYZE of a single-table change runs nothing and gives no figures.
    private static readonly string[] MySqlMeasurable = ["SELECT", "WITH", "TABLE"];

    private static readonly string[] MariaDbMeasurable = ["SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "REPLACE"];

    // The server accepts several statements in one command: "EXPLAIN ... SELECT 1; DELETE ..." would run the DELETE. So the
    // text must be one statement under every way the server may read it, with no executable comment (it could add ANALYZE).
    private static string? CannotPlanMySql(string text, PlanMode mode, bool mariaDb)
    {
        var allowed = mode != PlanMode.Actual ? MySqlExplainable : mariaDb ? MariaDbMeasurable : MySqlMeasurable;
        var word = "";
        foreach (var reading in SqlLexOptions.Readings(DbKind.MySql))
        {
            var tokens = SqlLexer.Tokenize(text, reading);
            if (text.Contains('\0') || tokens.Any(t => t.Kind == SqlTokenKind.Executable))
                return "No execution plan: the statement holds an executable comment (/*! ... */), which could change what runs.";
            var end = tokens.FindIndex(t => t.Kind == SqlTokenKind.Semicolon);
            if (end >= 0 && tokens.Skip(end).Any(t => t.Kind != SqlTokenKind.Semicolon))
                return "No execution plan: the text holds more than one statement.";
            var first = tokens.FirstOrDefault(t => !(t.Kind == SqlTokenKind.Other && text[t.Start] == '('));
            word = first.Kind == SqlTokenKind.Word ? text[first.Start..first.End] : "";
            if (!allowed.Contains(word, StringComparer.OrdinalIgnoreCase)) break;
        }
        if (allowed.Contains(word, StringComparer.OrdinalIgnoreCase)) return null;
        var engine = mode == PlanMode.Actual ? (mariaDb ? "MariaDB only measures" : "MySQL only measures") : "MySQL and MariaDB only explain";
        var name = word.Length == 0 ? "this one" : word.ToUpperInvariant();
        return $"No execution plan: {engine} {string.Join(", ", allowed)} ({name} is none of them).";
    }

    private static string FirstWord(string sql)
    {
        var i = 0;
        while (i < sql.Length)
        {
            while (i < sql.Length && (char.IsWhiteSpace(sql[i]) || sql[i] == '(')) i++;
            if (string.CompareOrdinal(sql, i, "--", 0, 2) == 0)
            {
                var eol = sql.IndexOf('\n', i);
                if (eol < 0) return "";
                i = eol + 1;
            }
            else if (string.CompareOrdinal(sql, i, "/*", 0, 2) == 0)
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) return "";
                i = close + 2;
            }
            else break;
        }
        var start = i;
        while (i < sql.Length && char.IsLetter(sql[i])) i++;
        return sql[start..i];
    }

    // ---- reading the result ----

    /// <summary>
    /// Splits a statement's result into what the user sees and the plan. The plan's sets (showplan XML, the EXPLAIN text) are
    /// removed from the result. A statement that failed, or a server that sent no plan, gives a null plan.
    /// </summary>
    public static (StatementResult Display, QueryPlan? Plan) Extract(StatementResult result, DbKind kind, PlanMode mode)
    {
        if (mode == PlanMode.None || result.Error is not null) return (result, null);
        try
        {
            switch (kind)
            {
                case DbKind.SqlServer:
                    return ExtractSqlServer(result, mode);
                case DbKind.Postgres:
                {
                    var set = result.Sets.FirstOrDefault(s => s.Rows.Count > 0 && s.Rows[0].Length > 0);
                    var json = set?.Rows[0][0]?.ToString();
                    if (!PostgresPlanParser.LooksLikePlan(json)) return (result, null);
                    return (result with { Sets = [] }, PostgresPlanParser.Parse(json!, mode, result.Statement.Text));
                }
                case DbKind.MySql:
                {
                    var set = result.Sets.FirstOrDefault(s => s.Rows.Count > 0 && s.Rows[0].Length > 0);
                    var raw = set?.Rows[0][0] is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : set?.Rows[0][0]?.ToString();
                    if (!MySqlPlanParser.LooksLikePlan(raw)) return (result, null);
                    return (result with { Sets = [] }, MySqlPlanParser.Parse(raw!, mode, result.Statement.Text));
                }
                default:
                {
                    var set = result.Sets.FirstOrDefault(s => s.Columns.Count >= 4);
                    if (set is null) return (result, null);
                    var rows = set.Rows.Select(r => new SqliteExplainRow(Convert.ToInt64(r[0]), Convert.ToInt64(r[1]), r[3]?.ToString() ?? "")).ToList();
                    return (result with { Sets = [] }, SqlitePlanParser.Parse(rows, result.Statement.Text));
                }
            }
        }
        catch (Exception e) when (e is System.Xml.XmlException or System.Text.Json.JsonException or InvalidCastException or FormatException)
        {
            // A plan the parser cannot read is not a reason to lose the statement's own result.
            return (result, null);
        }
    }

    private static (StatementResult, QueryPlan?) ExtractSqlServer(StatementResult result, PlanMode mode)
    {
        var plans = new List<ResultSet>();
        var rest = new List<ResultSet>();
        foreach (var set in result.Sets)
        {
            if (set.Columns.Count == 1 && set.Rows.Count > 0 && set.Rows[0][0] is string s && ShowplanParser.LooksLikeShowplan(s)) plans.Add(set);
            else rest.Add(set);
        }
        if (plans.Count == 0) return (result, null);

        // One batch can send several plan documents (a statement and its trigger): their statements join the first.
        var first = plans[0].Rows[0][0]!.ToString()!;
        var plan = ShowplanParser.Parse(first, mode);
        for (var i = 1; i < plans.Count; i++)
            plan.Statements.AddRange(ShowplanParser.Parse(plans[i].Rows[0][0]!.ToString()!, mode).Statements);
        return (result with { Sets = rest }, plan);
    }
}
