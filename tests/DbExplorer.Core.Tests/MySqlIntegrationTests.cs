using System.Data.Common;
using DbExplorer.Core.Config;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Export;
using DbExplorer.Core.Model;
using DbExplorer.Core.Plans;
using DbExplorer.Core.Querying;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;
using MySqlConnector;

namespace DbExplorer.Core.Tests;

/// <summary>
/// Against a real MySQL or MariaDB server: skipped unless env var DBX_MYSQL holds a MySqlConnector connection string of a
/// user allowed to create and drop databases. Each test works in a throw-away database, dropped afterwards.
/// </summary>
public sealed class MySqlIntegrationTests : IAsyncLifetime
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plain) => plain;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private static string? ServerString => Environment.GetEnvironmentVariable("DBX_MYSQL");

    private static readonly IDbDriver Driver = DriverFactory.For(DbKind.MySql);

    private readonly string _database = "dbx_" + Guid.NewGuid().ToString("N")[..10];
    private Session _session = null!;
    private bool _created;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(ServerString)) return;
        await using (var admin = new MySqlConnection(ServerString))
        {
            await admin.OpenAsync();
            await Execute(admin, $"CREATE DATABASE `{_database}` CHARACTER SET utf8mb4");
            _created = true;
        }
        var b = new MySqlConnectionStringBuilder(ServerString);
        var config = new ConnectionConfig(Guid.NewGuid(), "it", DbKind.MySql, null, b.Server, (int)b.Port, _database, b.UserID,
            b.Password, false, false, false);
        _session = new Session(config, Driver, [], new PlainProtector());
        await using var c = await _session.OpenConnectionAsync(CancellationToken.None);
        foreach (var sql in new[]
        {
            "CREATE TABLE customers (id int AUTO_INCREMENT PRIMARY KEY, name varchar(100) NOT NULL, active tinyint(1) NOT NULL DEFAULT 1, "
                + "note text, born date, seen datetime(6), amount decimal(12,2), blobby blob, u int unsigned, UNIQUE KEY ux_name (name))",
            "CREATE TABLE lines_ (order_id int, line_no int, customer_id int, PRIMARY KEY (line_no, order_id), "
                + "CONSTRAINT fk_c FOREIGN KEY (customer_id) REFERENCES customers (id))",
            "CREATE VIEW v_customers AS SELECT id, name FROM customers",
            "CREATE PROCEDURE p_count() BEGIN SELECT COUNT(*) FROM customers; SELECT 2; END",
            "CREATE FUNCTION f_twice(x int) RETURNS int DETERMINISTIC RETURN x * 2",
            "CREATE FUNCTION f_write() RETURNS int MODIFIES SQL DATA BEGIN INSERT INTO customers (name) VALUES (UUID()); RETURN 1; END",
            "INSERT INTO customers (name, note, born, seen, amount) VALUES ('Ann', '100% _sure| a\\\\b', '2001-02-03', '2026-01-02 03:04:05.123456', 12.50), "
                + "('Bob', NULL, '0000-00-00', NULL, NULL)",
        })
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql.Contains("0000-00-00") ? sql.Replace("'0000-00-00'", "NULL") : sql;
            await cmd.ExecuteNonQueryAsync();
        }
        var manager = await Driver.LoadSchemaAsync(c, CancellationToken.None);
        _session = new Session(config, Driver, manager, new PlainProtector());
    }

    public async Task DisposeAsync()
    {
        if (!_created) return;
        _session.Dispose();
        await using var admin = new MySqlConnection(ServerString);
        await admin.OpenAsync();
        await Execute(admin, $"DROP DATABASE `{_database}`");
    }

    private static void RequireServer() =>
        Skip.If(string.IsNullOrWhiteSpace(ServerString), "Set DBX_MYSQL to a MySQL or MariaDB connection string to run this test.");

    private static async Task Execute(DbConnection c, string sql, DbTransaction? tx = null)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> Scalar(string sql)
    {
        await using var c = await _session.OpenConnectionAsync(CancellationToken.None);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private TableInfo Table(string name) => _session.Schema.Single(t => t.Ref.Name == name);

    private async Task<List<StatementResult>> Run(ScriptConnection script, string sql, PlanMode plan = PlanMode.None, int maxRows = 1000,
        CancellationToken ct = default)
    {
        var results = new List<StatementResult>();
        var options = new ScriptOptions(maxRows, _session.Config.ReadOnly, DbKind.MySql, Plan: plan, CaptureMessages: true);
        await foreach (var r in script.RunAsync(ScriptSplitter.SplitForRun(sql, DbKind.MySql), options, ct)) results.Add(r);
        return results;
    }

    private void MakeReadOnly(bool value = true) => _session.UpdateConfig(_session.Config with { ReadOnly = value });

    [SkippableFact]
    public void Schema_TablesColumnsKeysAndIndexes()
    {
        RequireServer();
        Assert.Equal(["customers", "lines_", "v_customers"], _session.Schema.Select(t => t.Ref.Name));
        Assert.All(_session.Schema, t => Assert.Null(t.Ref.Schema));
        var customers = Table("customers");
        Assert.Equal(["id"], customers.Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name));
        Assert.Equal(typeof(bool), customers.Columns.Single(c => c.Name == "active").ClrType);
        Assert.Equal(typeof(uint), customers.Columns.Single(c => c.Name == "u").ClrType);
        Assert.Contains(customers.Indexes, i => i is { Name: "ux_name", Unique: true });
        var lines = Table("lines_");
        Assert.Equal(2, lines.Columns.Count(c => c.IsPrimaryKey));
        var fk = Assert.Single(lines.ForeignKeys);
        Assert.Equal((new TableRef(null, "customers"), "customer_id", "id"), (fk.RefTable, fk.Columns[0], fk.RefColumns[0]));
        Assert.True(Table("v_customers").IsView);
    }

    [SkippableFact]
    public async Task RoutinesAndDefinitions()
    {
        RequireServer();
        var routines = await _session.LoadRoutinesAsync(CancellationToken.None);
        Assert.Equal(["f_twice", "f_write", "p_count"], routines.Select(r => r.Ref.Name));
        Assert.Contains("SELECT COUNT(*)", await _session.LoadRoutineDefinitionAsync(routines[2], CancellationToken.None), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("f_twice", await _session.LoadRoutineDefinitionAsync(routines[0], CancellationToken.None));
        Assert.Contains("customers", await _session.LoadViewDefinitionAsync(new TableRef(null, "v_customers"), CancellationToken.None));
        await Assert.ThrowsAsync<DefinitionUnavailableException>(() =>
            _session.LoadViewDefinitionAsync(new TableRef(null, "nope"), CancellationToken.None));
    }

    [SkippableFact]
    public async Task TablePage_FiltersCountSearchAndExport()
    {
        RequireServer();
        var table = Table("customers");
        foreach (var filter in new[] { "100%", "_sure|", "a\\b" })
        {
            var q = new TableQuery(table, [new ColumnFilter("note", filter)], null, [], 0, 100);
            var page = await TableDataLoader.LoadPageAsync(_session, q, 100, CancellationToken.None);
            Assert.True(page.Rows.Count == 1, filter);
            Assert.Equal(1, await TableDataLoader.CountAsync(_session, q, CancellationToken.None));
        }
        var typed = new TableQuery(table, [new ColumnFilter("born", "2001-02-03"), new ColumnFilter("seen", ">2026-01-01")], "id > 0", [new SortSpec("name", true)], 0, 10);
        var rows = await TableDataLoader.LoadPageAsync(_session, typed, 10, CancellationToken.None);
        var ann = Assert.Single(rows.Rows);

        var matches = new List<DataMatch>();
        var summary = await DataSearch.RunAsync(_session, new DataSearchOptions("a\\b"), (_, m) => matches.AddRange(m), null, CancellationToken.None);
        Assert.Empty(summary.Failures);
        Assert.Contains(matches, m => m.Column == "note");

        // The exported literals give the same row back, also with NO_BACKSLASH_ESCAPES.
        var values = string.Join(", ", rows.ColumnNames.Select((n, i) => SqlLiterals.Format(DbKind.MySql, n == "id" ? 100 : n == "name" ? "Copy's \\ é 🎉" : ann[i])));
        foreach (var mode in new[] { "", "NO_BACKSLASH_ESCAPES" })
        {
            await using var c = await _session.OpenConnectionAsync(CancellationToken.None);
            await Execute(c, $"SET SESSION sql_mode = '{mode}'");
            await Execute(c, $"INSERT INTO customers ({string.Join(", ", rows.ColumnNames.Select(Driver.Quote))}) VALUES ({values})");
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT CONCAT(name, '|', note, '|', seen) FROM customers WHERE id = 100";
            Assert.Equal("Copy's \\ é 🎉|100% _sure| a\\b|2026-01-02 03:04:05.123456", await cmd.ExecuteScalarAsync());
            await Execute(c, "DELETE FROM customers WHERE id = 100");
        }
    }

    [SkippableFact]
    public async Task Edits_UpdateInsertDelete()
    {
        RequireServer();
        var table = Table("customers");
        var edits = new RowEdit[]
        {
            new UpdateRow(new Dictionary<string, object?> { ["id"] = 1 }, new Dictionary<string, object?> { ["name"] = "Ann", ["seen"] = "2026-03-04 05:06:07.654321" }),
            new InsertRow(new Dictionary<string, object?> { ["name"] = "Cid", ["active"] = "false" }),
            new DeleteRow(new Dictionary<string, object?> { ["id"] = 2 }),
        };
        await using (var c = await _session.OpenConnectionAsync(CancellationToken.None))
            await EditApplier.ApplyAsync(c, EditPlanner.Plan(Driver, table, ["id"], edits), CancellationToken.None);
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM customers WHERE name = 'Cid' AND active = 0"));
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM customers WHERE seen = '2026-03-04 05:06:07.654321'"));
        // An update that changes nothing still matches its row.
        await using (var c = await _session.OpenConnectionAsync(CancellationToken.None))
            await EditApplier.ApplyAsync(c, EditPlanner.Plan(Driver, table, ["id"], [edits[0]]), CancellationToken.None);
        await using var stale = await _session.OpenConnectionAsync(CancellationToken.None);
        await Assert.ThrowsAsync<StaleRowException>(() => EditApplier.ApplyAsync(stale, EditPlanner.Plan(Driver, table, ["id"], [edits[2]]), CancellationToken.None));
    }

    // The server itself, with the guard out of the way.
    [SkippableTheory]
    [InlineData("INSERT INTO customers (name) VALUES ('x')")]
    [InlineData("DELETE FROM customers")]
    [InlineData("CREATE TABLE evil (a int)")]
    [InlineData("DROP TABLE lines_")]
    [InlineData("TRUNCATE TABLE lines_")]
    [InlineData("ALTER TABLE customers ADD evil int")]
    [InlineData("COMMIT; INSERT INTO customers (name) VALUES ('x')")]
    [InlineData("COMMIT; DROP TABLE lines_")]
    [InlineData("SELECT 1; COMMIT; DELETE FROM customers")]
    [InlineData("SELECT f_write()")]
    [InlineData("CREATE USER dbx_evil_user IDENTIFIED BY 'x'")]
    public async Task ReadOnlyContext_ServerRefusesWrites(string sql)
    {
        RequireServer();
        var before = await Scalar("SELECT COUNT(*) FROM customers");
        await using (var c = await _session.OpenConnectionAsync(CancellationToken.None))
        {
            var tx = await TableDataLoader.BeginReadOnlyContextAsync(c, DbKind.MySql, CancellationToken.None);
            await Assert.ThrowsAnyAsync<DbException>(async () =>
            {
                await using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                await using var reader = await cmd.ExecuteReaderAsync();
                do { while (await reader.ReadAsync()) { } } while (await reader.NextResultAsync());
            });
        }
        Assert.Equal(before, await Scalar("SELECT COUNT(*) FROM customers"));
        Assert.Equal(3, await Scalar("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE()"));
        // The pooled connection is reset: it writes again.
        await using var again = await _session.OpenConnectionAsync(CancellationToken.None);
        await Execute(again, "INSERT INTO customers (name) VALUES ('after')");
    }

    [SkippableFact]
    public async Task ReadOnlyContext_QuotingModesAreReset()
    {
        RequireServer();
        await using var c = await _session.OpenConnectionAsync(CancellationToken.None);
        await Execute(c, "SET SESSION sql_mode = 'ANSI,NO_BACKSLASH_ESCAPES,STRICT_ALL_TABLES'");
        await using var tx = await TableDataLoader.BeginReadOnlyContextAsync(c, DbKind.MySql, CancellationToken.None);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT @@SESSION.sql_mode";
        var mode = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.DoesNotContain("ANSI_QUOTES", mode);
        Assert.DoesNotContain("NO_BACKSLASH_ESCAPES", mode);
        Assert.Contains("STRICT_ALL_TABLES", mode);
    }

    [SkippableFact]
    public async Task ReadOnlyScript_RefusesAndStillReads()
    {
        RequireServer();
        MakeReadOnly();
        using var script = new ScriptConnection(_session);
        var read = await Run(script, "SELECT id FROM customers; SHOW TABLES; SHOW CREATE TABLE customers");
        Assert.All(read, r => Assert.Null(r.Error));
        Assert.Contains("read-only", (await Run(script, "SET GLOBAL max_connections = 10"))[0].Error);
        // A stored function the guard cannot know about: the server refuses it.
        Assert.NotNull((await Run(script, "SELECT f_write()"))[0].Error);
        Assert.Null((await Run(script, "SELECT 1"))[0].Error);
    }

    [SkippableFact]
    public async Task Script_KeepsSessionState_DelimiterCallAndRowCounts()
    {
        RequireServer();
        using var script = new ScriptConnection(_session);
        var first = await Run(script, "CREATE TEMPORARY TABLE tt (a int); SET @v := 5; INSERT INTO tt VALUES (@v), (6)");
        Assert.All(first, r => Assert.Null(r.Error));
        Assert.Equal([null, null, 2], first.Select(r => r.RowsAffected));
        var second = await Run(script, "SELECT SUM(a), @v FROM tt");
        Assert.Equal(11m, second[0].Sets[0].Rows[0][0]);

        var proc = await Run(script, "DELIMITER $$\nCREATE PROCEDURE p_two()\nBEGIN\n  SELECT 1;\n\n  SELECT 2;\nEND$$\nDELIMITER ;\nCALL p_two();");
        Assert.All(proc, r => Assert.Null(r.Error));
        Assert.Equal(2, proc[1].Sets.Count);

        await Run(script, "BEGIN");
        Assert.True(await script.HasOpenTransactionAsync());
        await Run(script, "DELETE FROM lines_; ROLLBACK");
        Assert.False(await script.HasOpenTransactionAsync());
    }

    [SkippableFact]
    public async Task Script_ReportsWarnings()
    {
        RequireServer();
        using var script = new ScriptConnection(_session);
        var result = await Run(script, "SELECT CAST('abc' AS SIGNED)");
        Assert.Null(result[0].Error);
        Assert.Contains(result[0].Messages!, m => m.Text.StartsWith("Warning", StringComparison.Ordinal));
        Assert.Empty((await Run(script, "SELECT 1"))[0].Messages!);
    }

    [SkippableFact]
    public async Task Script_RowLimitThenNextStatement_AndCancel()
    {
        RequireServer();
        using var script = new ScriptConnection(_session);
        var many = "SELECT a.ordinal_position FROM information_schema.columns a, information_schema.columns b";
        var results = await Run(script, $"SET @k = 1; {many}; SELECT @k", maxRows: 5);
        Assert.All(results, r => Assert.Null(r.Error));
        Assert.True(results[1].Sets[0].Truncated);
        Assert.Equal(1, Convert.ToInt32(results[2].Sets[0].Rows[0][0]));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cancelled = await Run(script, "SELECT SLEEP(30)", ct: cts.Token);
        Assert.Empty(cancelled);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        var after = await Run(script, "SELECT @k");
        Assert.Null(after[0].Error);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EstimatedPlan_RunsNothing(bool readOnly)
    {
        RequireServer();
        MakeReadOnly(readOnly);
        using var script = new ScriptConnection(_session);
        var before = await Scalar("SELECT COUNT(*) FROM customers");
        foreach (var sql in new[]
        {
            "SELECT c.name FROM customers c JOIN lines_ l ON l.customer_id = c.id WHERE c.note LIKE '%a%' ORDER BY c.born",
            "SELECT name FROM customers UNION SELECT name FROM customers GROUP BY name",
            "DELETE FROM customers",
            "UPDATE customers SET name = CONCAT(name, 'x')",
            "SELECT * FROM customers WHERE id = f_write()",
        })
        {
            var result = Assert.Single(await Run(script, sql, PlanMode.Estimated));
            if (sql.Contains("f_write") && result.Error is not null) continue;   // refused while evaluating the function: fine
            // A read-only session refuses to explain a change.
            if (readOnly && (sql.StartsWith("DELETE", StringComparison.Ordinal) || sql.StartsWith("UPDATE", StringComparison.Ordinal))
                && result.Error is not null) { Assert.Contains("READ ONLY", result.Error); continue; }
            Assert.True(result.Error is null, $"{sql}: {result.Error}");
            var (_, plan) = PlanCapture.Extract(result, DbKind.MySql, PlanMode.Estimated);
            Assert.NotNull(plan);
            Assert.NotEmpty(plan.Statements);
        }
        Assert.Equal(before, await Scalar("SELECT COUNT(*) FROM customers"));
        if (readOnly) return;
        Assert.False(await script.HasOpenTransactionAsync());
        Assert.Null((await Run(script, "INSERT INTO customers (name) VALUES ('later')"))[0].Error);
        Assert.Equal(before + 1, await Scalar("SELECT COUNT(*) FROM customers"));

        // Inside the script's own transaction, a plan neither commits nor undoes it.
        await Run(script, "BEGIN; INSERT INTO customers (name) VALUES ('mine')");
        Assert.Null(Assert.Single(await Run(script, "SELECT * FROM customers", PlanMode.Estimated)).Error);
        Assert.True(await script.HasOpenTransactionAsync());
        Assert.Equal(before + 1, await Scalar("SELECT COUNT(*) FROM customers"));
        Assert.Equal(before + 2, Convert.ToInt64((await Run(script, "SELECT COUNT(*) FROM customers"))[0].Sets[0].Rows[0][0]));
        await Run(script, "ROLLBACK");
        Assert.Equal(before + 1, await Scalar("SELECT COUNT(*) FROM customers"));
    }

    [SkippableFact]
    public async Task ActualPlan_HasRowsAndTimes()
    {
        RequireServer();
        using var script = new ScriptConnection(_session);
        var sql = "SELECT c.name FROM customers c LEFT JOIN lines_ l ON l.customer_id = c.id WHERE c.amount IS NULL OR c.amount > 0 ORDER BY c.born";
        var result = Assert.Single(await Run(script, sql, PlanMode.Actual));
        Assert.True(result.Error is null, result.Error);
        var (_, plan) = PlanCapture.Extract(result, DbKind.MySql, PlanMode.Actual);
        var statement = Assert.Single(plan!.Statements);
        Assert.True(statement.HasActuals, plan.Raw);
        Assert.True(statement.HasTimes, plan.Raw);
    }

    [SkippableFact]
    public async Task ErrorsSayWhichStageFailed()
    {
        RequireServer();
        var config = _session.Config with { ProtectedPassword = "wrong" };
        var manager = await Assert.ThrowsAsync<ConnectionStageException>(() =>
            SessionManager.TestConnectionAsync(config, "wrong", CancellationToken.None));
        Assert.Equal(ConnectionStage.DatabaseSignIn, manager.Stage);
        await SessionManager.TestConnectionAsync(config, new MySqlConnectionStringBuilder(ServerString).Password, CancellationToken.None);
        await SessionManager.TestConnectionAsync(config with { Tls = new TlsConfig(TlsMode.Require) },
            new MySqlConnectionStringBuilder(ServerString).Password, CancellationToken.None);
    }
}
