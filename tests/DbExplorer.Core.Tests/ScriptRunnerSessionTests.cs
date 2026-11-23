using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

/// <summary>The session-level runner: opens its own connection and, on read-only connections, a read-only context.</summary>
public class ScriptRunnerSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-runner-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private readonly Session _session;

    public ScriptRunnerSessionTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "r.db");
        using (var c = new SqliteConnection($"Data Source={_file};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO t VALUES (1, 'a')";
            cmd.ExecuteNonQuery();
        }

        var config = new ConnectionConfig(Guid.NewGuid(), "Lite", DbKind.Sqlite, null, null, null, _file, null, null, false, false, false);
        _session = new Session(config, new SqliteDriver(), [], null);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<List<StatementResult>> RunAsync(string script, ScriptOptions options)
    {
        var results = new List<StatementResult>();
        var stmts = ScriptSplitter.Split(script, DbKind.Sqlite);
        await foreach (var r in ScriptRunner.RunAsync(_session, stmts, options, CancellationToken.None)) results.Add(r);
        return results;
    }

    [Fact]
    public async Task RunOnSession_ReturnsRows()
    {
        var results = await RunAsync("SELECT id, name FROM t", new ScriptOptions(Kind: DbKind.Sqlite));

        var set = Assert.Single(Assert.Single(results).Sets);
        Assert.Equal(new object?[] { 1L, "a" }, set.Rows[0]);
    }

    [Fact]
    public async Task RunOnSession_Writable_PersistsWrites()
    {
        await RunAsync("INSERT INTO t VALUES (2, 'b')", new ScriptOptions(Kind: DbKind.Sqlite));

        var results = await RunAsync("SELECT count(*) FROM t", new ScriptOptions(Kind: DbKind.Sqlite));
        Assert.Equal(2L, results.Single().Sets.Single().Rows[0][0]);
    }

    [Fact]
    public async Task RunOnSession_ReadOnly_RefusesWrite()
    {
        var results = await RunAsync("DELETE FROM t", new ScriptOptions(ReadOnly: true, Kind: DbKind.Sqlite));

        Assert.Contains("read-only", Assert.Single(results).Error);
    }

    [Fact]
    public async Task RunOnSession_ReadOnly_RunsInReadOnlyContext_DefenceInDepth()
    {
        // The guard is lexical; the connection itself must also refuse writes. SQLite exposes the setting as a table function.
        const string probe = "SELECT query_only FROM pragma_query_only";

        var readOnly = await RunAsync(probe, new ScriptOptions(ReadOnly: true, Kind: DbKind.Sqlite));
        var writable = await RunAsync(probe, new ScriptOptions(ReadOnly: false, Kind: DbKind.Sqlite));

        Assert.Equal(1L, readOnly.Single().Sets.Single().Rows[0][0]);
        Assert.Equal(0L, writable.Single().Sets.Single().Rows[0][0]);
    }

    [Fact]
    public async Task RunOnSession_Cancel_InterruptsAStatementThatReturnsNoRowYet()
    {
        // No row is ever produced, so the reader loop never gets a chance to look at the token: the statement
        // itself must be interrupted.
        const string silent = "WITH RECURSIVE c(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM c) SELECT n FROM c WHERE n < 0";
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var results = new List<StatementResult>();
        var run = Task.Run(async () =>
        {
            await foreach (var r in ScriptRunner.RunAsync(
                               _session, ScriptSplitter.Split(silent, DbKind.Sqlite), new ScriptOptions(Kind: DbKind.Sqlite), cts.Token))
                results.Add(r);
        });

        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20))));
        await run;   // no exception
        Assert.Empty(results);
    }

    [Fact]
    public async Task RunOnSession_DisposedSession_Throws()
    {
        _session.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => RunAsync("SELECT 1", new ScriptOptions(Kind: DbKind.Sqlite)));
    }
}
