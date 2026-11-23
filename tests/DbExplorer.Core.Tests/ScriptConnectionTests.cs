using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

/// <summary>A driver of the given kind that hands out one prepared fake connection (no server).</summary>
internal sealed class StubDriver(DbKind kind, FakeDbConnection connection) : IDbDriver
{
    public DbKind Kind => kind;
    public string ParamPrefix => "@";
    public string Quote(string identifier) => identifier;
    public string QuoteTable(TableRef t) => t.Name;
    public string CastToText(string quotedColumn) => quotedColumn;
    public string EscapeLike(string text) => text;
    public string PageSql(string selectSql, string orderBy, int offset, int limit) => selectSql;
    public System.Data.Common.DbConnection CreateConnection(ConnectionConfig cfg, string? plainPassword) => connection;
    public Task<IReadOnlyList<TableInfo>> LoadSchemaAsync(System.Data.Common.DbConnection c, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TableInfo>>([]);
}

/// <summary>Releasing the connection of a tab must undo an open transaction on pooled engines, not leave it to the pool's lazy reset.</summary>
public class ScriptConnectionReleaseTests
{
    [Theory]
    [InlineData(DbKind.SqlServer, "@@TRANCOUNT")]
    [InlineData(DbKind.Postgres, "ROLLBACK")]
    public async Task Dispose_SendsAnExplicitRollbackBeforeReleasingTheConnection(DbKind kind, string expected)
    {
        var fake = new FakeDbConnection(affected: 1);
        var config = new ConnectionConfig(Guid.NewGuid(), "c", kind, null, "h", null, "d", null, null, false, false, false);
        var session = new Session(config, new StubDriver(kind, fake), [], null);
        var script = new ScriptConnection(session);
        await foreach (var _ in script.RunAsync(ScriptSplitter.Split("SELECT 1", kind), new ScriptOptions(Kind: kind), CancellationToken.None)) { }

        script.Dispose();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (fake.State == System.Data.ConnectionState.Open && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(System.Data.ConnectionState.Closed, fake.State);
        Assert.Contains(fake.Commands, c => c.CommandText.Contains(expected));
        Assert.True(fake.Commands.FindIndex(c => c.CommandText.Contains(expected)) > 0, "the rollback follows the script's own statement");
    }

    [Fact]
    public async Task Dispose_OnSqlite_JustClosesTheConnection()
    {
        var fake = new FakeDbConnection(affected: 1);
        var config = new ConnectionConfig(Guid.NewGuid(), "c", DbKind.Sqlite, null, null, null, "x.db", null, null, false, false, false);
        var script = new ScriptConnection(new Session(config, new StubDriver(DbKind.Sqlite, fake), [], null));
        await foreach (var _ in script.RunAsync(ScriptSplitter.Split("SELECT 1", DbKind.Sqlite), new ScriptOptions(Kind: DbKind.Sqlite), CancellationToken.None)) { }

        script.Dispose();

        Assert.Equal(System.Data.ConnectionState.Closed, fake.State);
        Assert.DoesNotContain(fake.Commands, c => c.CommandText.Contains("ROLLBACK"));
    }
}

/// <summary>One dedicated connection per script tab: transactions, temp tables and session state survive between runs.</summary>
public class ScriptConnectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-scriptconn-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private readonly Session _session;
    private readonly ConnectionConfig _config;
    private static readonly ScriptOptions Writable = new(Kind: DbKind.Sqlite);

    public ScriptConnectionTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "s.db");
        using (var c = new SqliteConnection($"Data Source={_file};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO t VALUES (1, 'a')";
            cmd.ExecuteNonQuery();
        }

        _config = new ConnectionConfig(Guid.NewGuid(), "Lite", DbKind.Sqlite, null, null, null, _file, null, null, false, false, false);
        _session = new Session(_config, new SqliteDriver(), [], null);
    }

    public void Dispose()
    {
        _session.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static async Task<List<StatementResult>> RunAsync(
        ScriptConnection script, string sql, ScriptOptions? options = null, CancellationToken ct = default)
    {
        var results = new List<StatementResult>();
        await foreach (var r in script.RunAsync(ScriptSplitter.Split(sql, DbKind.Sqlite), options ?? Writable, ct)) results.Add(r);
        return results;
    }

    private static object? Scalar(IEnumerable<StatementResult> results) => results.Last().Sets.Single().Rows[0][0];

    private long Count(string where = "1 = 1")
    {
        using var c = new SqliteConnection($"Data Source={_file};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM t WHERE {where}";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task NothingIsOpenedBeforeTheFirstRun()
    {
        using var script = new ScriptConnection(_session);

        Assert.Null(script.CurrentConnection);
        await RunAsync(script, "SELECT 1");
        Assert.NotNull(script.CurrentConnection);
    }

    [Fact]
    public async Task ManualTransaction_SurvivesBetweenRuns_AndRollsBack()
    {
        using var script = new ScriptConnection(_session);

        await RunAsync(script, "BEGIN");
        await RunAsync(script, "INSERT INTO t VALUES (2, 'x')");
        Assert.Equal(2L, Scalar(await RunAsync(script, "SELECT COUNT(*) FROM t")));   // visible inside the transaction
        await RunAsync(script, "ROLLBACK");

        Assert.Equal(1L, Count());
        Assert.Equal(1L, Scalar(await RunAsync(script, "SELECT COUNT(*) FROM t")));
    }

    [Fact]
    public async Task ManualTransaction_Commit_Persists()
    {
        using var script = new ScriptConnection(_session);

        await RunAsync(script, "BEGIN");
        await RunAsync(script, "INSERT INTO t VALUES (2, 'x')");
        await RunAsync(script, "COMMIT");

        Assert.Equal(2L, Count());
    }

    [Fact]
    public async Task TempTable_SurvivesBetweenRuns()
    {
        using var script = new ScriptConnection(_session);

        await RunAsync(script, "CREATE TEMP TABLE scratch (v INTEGER)");
        await RunAsync(script, "INSERT INTO scratch VALUES (7)");

        Assert.Equal(7L, Scalar(await RunAsync(script, "SELECT v FROM scratch")));
    }

    [Fact]
    public async Task TheSameConnectionIsReused_AndTwoTabsHaveTheirOwn()
    {
        using var first = new ScriptConnection(_session);
        using var second = new ScriptConnection(_session);

        await RunAsync(first, "SELECT 1");
        var held = first.CurrentConnection;
        await RunAsync(first, "SELECT 2");
        await RunAsync(second, "SELECT 1");

        Assert.Same(held, first.CurrentConnection);
        Assert.NotSame(first.CurrentConnection, second.CurrentConnection);
        Assert.False(first.Reopened);
    }

    [Fact]
    public async Task Dispose_RollsBackAnOpenTransaction_AndReleasesTheFile()
    {
        var script = new ScriptConnection(_session);
        await RunAsync(script, "BEGIN");
        await RunAsync(script, "INSERT INTO t VALUES (2, 'x')");

        script.Dispose();

        Assert.Equal(1L, Count());
        using var other = new SqliteConnection($"Data Source={_file};Pooling=False");
        other.Open();
        using var cmd = other.CreateCommand();
        cmd.CommandText = "INSERT INTO t VALUES (3, 'y')";   // the write lock is free again
        cmd.ExecuteNonQuery();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => RunAsync(script, "SELECT 1"));
    }

    [Fact]
    public async Task SessionDispose_DisposesTheDedicatedConnections()
    {
        var script = new ScriptConnection(_session);
        await RunAsync(script, "BEGIN");
        await RunAsync(script, "INSERT INTO t VALUES (2, 'x')");

        _session.Dispose();   // Disconnect

        Assert.Null(script.CurrentConnection);
        Assert.Equal(1L, Count());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => RunAsync(script, "SELECT 1"));
    }

    [Fact]
    public async Task BrokenConnection_IsReopenedByTheNextRun_AndSaysSo()
    {
        using var script = new ScriptConnection(_session);
        await RunAsync(script, "CREATE TEMP TABLE scratch (v INTEGER)");
        script.CurrentConnection!.Close();   // what a dropped network connection looks like to the next run

        var results = await RunAsync(script, "SELECT 1");

        Assert.Null(results.Single().Error);
        Assert.True(script.Reopened);
        var gone = await RunAsync(script, "SELECT * FROM scratch");
        Assert.NotNull(gone.Single().Error);   // session state was reset
        Assert.False(script.Reopened);
    }

    [Fact]
    public async Task ReadOnlyChange_InvalidatesTheConnection()
    {
        using var script = new ScriptConnection(_session);
        await RunAsync(script, "CREATE TEMP TABLE scratch (v INTEGER)");
        var before = script.CurrentConnection;

        _session.UpdateConfig(_config with { ReadOnly = true });
        await RunAsync(script, "SELECT 1", new ScriptOptions(ReadOnly: true, Kind: DbKind.Sqlite));

        Assert.NotSame(before, script.CurrentConnection);
        Assert.True(script.Reopened);
    }

    [Fact]
    public async Task CosmeticConfigChange_KeepsTheConnection()
    {
        using var script = new ScriptConnection(_session);
        await RunAsync(script, "SELECT 1");
        var before = script.CurrentConnection;

        _session.UpdateConfig(_config with { Name = "renamed" });
        await RunAsync(script, "SELECT 1");

        Assert.Same(before, script.CurrentConnection);
        Assert.False(script.Reopened);
    }

    [Fact]
    public async Task ReadOnlyRun_DoesNotLeakItsContextIntoLaterWritableRuns()
    {
        using var script = new ScriptConnection(_session);
        const string probe = "SELECT query_only FROM pragma_query_only";

        var readOnly = await RunAsync(script, probe, new ScriptOptions(ReadOnly: true, Kind: DbKind.Sqlite));
        var writable = await RunAsync(script, probe, Writable);

        Assert.Equal(1L, Scalar(readOnly));
        Assert.Equal(0L, Scalar(writable));
    }

    [Fact]
    public async Task ReadOnlyRun_StillRefusesWrites()
    {
        using var script = new ScriptConnection(_session);

        var results = await RunAsync(script, "DELETE FROM t", new ScriptOptions(ReadOnly: true, Kind: DbKind.Sqlite));

        Assert.Contains("read-only", Assert.Single(results).Error);
        Assert.Equal(1L, Count());
    }

    [Fact]
    public async Task AfterACancel_TheConnectionIsStillUsable()
    {
        using var script = new ScriptConnection(_session);
        await RunAsync(script, "CREATE TEMP TABLE scratch (v INTEGER)");
        const string silent = "WITH RECURSIVE c(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM c) SELECT n FROM c WHERE n < 0";
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var run = Task.Run(() => RunAsync(script, silent, ct: cts.Token));
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20))));
        Assert.Empty(await run);

        var results = await RunAsync(script, "SELECT COUNT(*) FROM scratch");
        Assert.Equal(0L, Scalar(results));        // same session: the temp table is still there (the probe passed)
        Assert.False(script.Reopened);
    }

    [Fact]
    public async Task ASecondRunWhileOneIsActive_IsRefused()
    {
        using var script = new ScriptConnection(_session);
        const string silent = "WITH RECURSIVE c(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM c) SELECT n FROM c WHERE n < 0";
        using var cts = new CancellationTokenSource();
        var first = Task.Run(() => RunAsync(script, silent, ct: cts.Token));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!script.IsRunning)   // the first run has begun (no fixed sleep: a slow machine must not make this flaky)
        {
            Assert.True(DateTime.UtcNow < deadline, "the first run never started");
            await Task.Delay(5);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(script, "SELECT 1"));

        cts.Cancel();
        await first;
    }
}
