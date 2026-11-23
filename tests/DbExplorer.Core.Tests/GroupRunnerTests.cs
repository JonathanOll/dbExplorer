using System.Collections.Concurrent;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

/// <summary>Collects progress synchronously (Progress&lt;T&gt; would post to another thread).</summary>
internal sealed class SyncProgress : IProgress<MemberProgress>
{
    public ConcurrentQueue<MemberProgress> Events { get; } = new();
    public void Report(MemberProgress value) => Events.Enqueue(value);
    public List<MemberState> StatesOf(Guid id) => Events.Where(e => e.ConnectionId == id).Select(e => e.State).ToList();
}

/// <summary>The group run is read-only by construction: a write statement must never reach any member.</summary>
public class GroupRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-group-" + Guid.NewGuid().ToString("N"));
    private readonly List<ConnectionConfig> _members = [];
    private static readonly GroupRunOptions Defaults = new();

    public GroupRunnerTests()
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < 3; i++) _members.Add(MakeSqliteMember(i, 10 + i));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A SQLite file with the table t(id, name): <paramref name="rows"/> rows named "m{index}".</summary>
    private ConnectionConfig MakeSqliteMember(int index, int rows)
    {
        var file = Path.Combine(_dir, $"m{index}-{Guid.NewGuid():N}.db");
        using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT);";
            cmd.ExecuteNonQuery();
            for (var r = 1; r <= rows; r++)
            {
                cmd.CommandText = $"INSERT INTO t VALUES ({r}, 'm{index}')";
                cmd.ExecuteNonQuery();
            }
        }
        return new ConnectionConfig(Guid.NewGuid(), "Lite" + index, DbKind.Sqlite, null, null, null, file, null, null, false, false, false);
    }

    private static Task<ScriptConnection> Sqlite(ConnectionConfig cfg, CancellationToken ct) =>
        Task.FromResult(new ScriptConnection(new Session(cfg, new SqliteDriver(), [], null)));

    private static IReadOnlyList<ScriptStatement> Select(string sql = "SELECT name FROM t ORDER BY id") =>
        [new ScriptStatement(sql, 0, sql.Length)];

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Run_ReturnsOneResultPerMember_InMemberOrder()
    {
        var runner = new GroupRunner(Sqlite);

        var results = await runner.RunAsync(_members, Select(), Defaults, null, CancellationToken.None);

        Assert.Equal(3, results.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(_members[i].Id, results[i].Connection.Id);
            Assert.Equal(MemberState.Done, results[i].State);
            Assert.Null(results[i].Error);
            var set = Assert.Single(Assert.Single(results[i].Statements).Sets);
            Assert.Equal(10 + i, set.Rows.Count);
            Assert.Equal("m" + i, set.Rows[0][0]);
        }
    }

    [Fact]
    public async Task Run_ExecutesInParallel_UpToMaxParallel()
    {
        var members = Enumerable.Range(0, 5).Select(i => MakeSqliteMember(i, 1)).ToList();
        int current = 0, peak = 0;
        var runner = new GroupRunner(async (cfg, ct) =>
        {
            var now = Interlocked.Increment(ref current);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            await Task.Delay(120, ct);
            Interlocked.Decrement(ref current);
            return await Sqlite(cfg, ct);
        });

        var results = await runner.RunAsync(members, Select(), new GroupRunOptions(MaxParallel: 2), null, CancellationToken.None);

        Assert.All(results, r => Assert.Equal(MemberState.Done, r.State));
        Assert.Equal(2, peak);
    }

    [Theory]
    [InlineData(DbKind.Sqlite, "DELETE FROM t")]
    [InlineData(DbKind.Sqlite, "select 1; update t set name='x'")]
    [InlineData(DbKind.Sqlite, "/* c */ INSERT INTO t VALUES (99, 'z')")]
    [InlineData(DbKind.Sqlite, "-- note\nDROP TABLE t")]
    [InlineData(DbKind.Sqlite, "PRAGMA user_version = 5")]
    [InlineData(DbKind.SqlServer, "SELECT 1 INTO x FROM t")]
    [InlineData(DbKind.SqlServer, "SELECT 1 DELETE FROM t")]
    [InlineData(DbKind.SqlServer, "EXEC sp_who")]
    [InlineData(DbKind.Postgres, "WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d")]
    [InlineData(DbKind.Postgres, "SELECT 1; TRUNCATE t")]
    public async Task Run_RefusesAnyWriteStatement_BeforeRunningAnything(DbKind kind, string sql)
    {
        var members = Enumerable.Range(0, 3)
            .Select(i => new ConnectionConfig(Guid.NewGuid(), "c" + i, kind, null, "h", null, "d", null, null, false, false, false)).ToList();
        var factoryCalls = 0;
        var runner = new GroupRunner((cfg, ct) => { Interlocked.Increment(ref factoryCalls); throw new InvalidOperationException("must not connect"); });
        var progress = new SyncProgress();

        await Assert.ThrowsAsync<GroupRunRefusedException>(() =>
            runner.RunAsync(members, [new ScriptStatement(sql, 0, sql.Length)], Defaults, progress, CancellationToken.None));

        Assert.Equal(0, factoryCalls);
        Assert.Empty(progress.Events);
    }

    [Fact]
    public async Task Run_RefusesWhenOnlyTheLastOfSeveralStatementsIsAWrite()
    {
        var factoryCalls = 0;
        var runner = new GroupRunner((cfg, ct) => { Interlocked.Increment(ref factoryCalls); return Sqlite(cfg, ct); });
        var stmts = ScriptSplitter.Split("SELECT 1; SELECT 2; UPDATE t SET name = 'x'", DbKind.Sqlite);
        Assert.Equal(3, stmts.Count);

        await Assert.ThrowsAsync<GroupRunRefusedException>(() => runner.RunAsync(_members, stmts, Defaults, null, CancellationToken.None));

        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task Run_RefusesAnEmptyStatementList()
    {
        var runner = new GroupRunner(Sqlite);

        await Assert.ThrowsAsync<GroupRunRefusedException>(() => runner.RunAsync(_members, [], Defaults, null, CancellationToken.None));
    }

    [Fact]
    public async Task Run_RejectsMixedKinds()
    {
        var factoryCalls = 0;
        var pg = new ConnectionConfig(Guid.NewGuid(), "pg", DbKind.Postgres, null, "h", null, "d", null, null, false, false, false);
        var runner = new GroupRunner((cfg, ct) => { Interlocked.Increment(ref factoryCalls); return Sqlite(cfg, ct); });

        await Assert.ThrowsAsync<GroupRunRefusedException>(() =>
            runner.RunAsync([_members[0], pg], Select(), Defaults, null, CancellationToken.None));

        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task Run_ReadOnlyIsForcedEvenWhenMemberFlagIsFalse()
    {
        // PostgreSQL: the read-only context is a READ ONLY transaction, visible in the commands the fake connection received.
        var fake = new FakeDbConnection(affected: 0);
        var cfg = new ConnectionConfig(Guid.NewGuid(), "pg", DbKind.Postgres, null, "h", null, "d", null, null, false, false, ReadOnly: false);
        var runner = new GroupRunner((c, ct) => Task.FromResult(new ScriptConnection(new Session(c, new StubDriver(DbKind.Postgres, fake), [], null))));

        await runner.RunAsync([cfg], Select("SELECT 1"), Defaults, null, CancellationToken.None);

        Assert.Contains(fake.Commands, c => c.CommandText == "SET TRANSACTION READ ONLY");
    }

    [Fact]
    public async Task Run_RefusedWriteLeavesEveryMemberDataUntouched()
    {
        // The data of every member is untouched after a run (and a rejected write never created anything).
        var runner = new GroupRunner(Sqlite);
        await runner.RunAsync(_members, Select(), Defaults, null, CancellationToken.None);
        await Assert.ThrowsAsync<GroupRunRefusedException>(() =>
            runner.RunAsync(_members, Select("DELETE FROM t"), Defaults, null, CancellationToken.None));

        for (var i = 0; i < 3; i++)
        {
            using var c = new SqliteConnection($"Data Source={_members[i].Database};Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM t";
            Assert.Equal(10L + i, cmd.ExecuteScalar());
        }
    }

    [Fact]
    public async Task Run_AConnectionOfAnotherKindIsNeverUsed()
    {
        var fake = new FakeDbConnection(affected: 0);
        var runner = new GroupRunner((cfg, ct) =>
            Task.FromResult(new ScriptConnection(new Session(cfg, new StubDriver(DbKind.Postgres, fake), [], null))));

        var results = await runner.RunAsync([_members[0]], Select(), Defaults, null, CancellationToken.None);

        Assert.Equal(MemberState.Failed, Assert.Single(results).State);
        Assert.NotNull(results[0].Error);
        Assert.Empty(fake.Commands);
    }

    [Fact]
    public async Task Run_AFailingMember_DoesNotStopTheOthers()
    {
        var runner = new GroupRunner((cfg, ct) =>
            cfg.Id == _members[1].Id ? throw new InvalidOperationException("login failed for member") : Sqlite(cfg, ct));

        var results = await runner.RunAsync(_members, Select(), Defaults, null, CancellationToken.None);

        Assert.Equal(MemberState.Done, results[0].State);
        Assert.Equal(MemberState.Failed, results[1].State);
        Assert.Contains("login failed for member", results[1].Error);
        Assert.Equal(MemberState.Done, results[2].State);
    }

    [Fact]
    public async Task Run_AMemberWhoseStatementFails_IsFailedAndKeepsTheStatementResults()
    {
        var other = MakeSqliteMember(7, 1);
        using (var c = new SqliteConnection($"Data Source={other.Database};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TABLE t";
            cmd.ExecuteNonQuery();
        }
        var runner = new GroupRunner(Sqlite);

        var results = await runner.RunAsync([_members[0], other], Select(), Defaults, null, CancellationToken.None);

        Assert.Equal(MemberState.Done, results[0].State);
        Assert.Equal(MemberState.Failed, results[1].State);
        Assert.Contains("no such table", results[1].Error, StringComparison.OrdinalIgnoreCase);
        Assert.Single(results[1].Statements);
    }

    [Fact]
    public async Task Run_AnUnreachableMember_TimesOutOnItsOwn()
    {
        var hung = _members[1];
        var runner = new GroupRunner(async (cfg, ct) =>
        {
            if (cfg.Id == hung.Id) await Task.Delay(Timeout.Infinite, ct);
            return await Sqlite(cfg, ct);
        });
        var progress = new SyncProgress();

        var run = runner.RunAsync(_members, Select(), Defaults, progress, CancellationToken.None);
        await WaitUntil(() => progress.StatesOf(_members[0].Id).Contains(MemberState.Done)
                              && progress.StatesOf(_members[2].Id).Contains(MemberState.Done));
        Assert.False(run.IsCompleted);   // the hung member does not hold the others back, but the run waits for it

        runner.CancelMember(hung.Id);
        var results = await run;

        Assert.Equal(MemberState.Done, results[0].State);
        Assert.Equal(MemberState.Cancelled, results[1].State);
        Assert.Equal(MemberState.Done, results[2].State);
    }

    [Fact]
    public async Task Run_CancelAll_MarksRunningAndPendingMembersCancelled_WithoutThrowing()
    {
        var runner = new GroupRunner(async (cfg, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return await Sqlite(cfg, ct);
        });
        var progress = new SyncProgress();
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(_members, Select(), new GroupRunOptions(MaxParallel: 1), progress, cts.Token);
        await WaitUntil(() => progress.StatesOf(_members[0].Id).Contains(MemberState.Running));
        cts.Cancel();
        var results = await run;

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(MemberState.Cancelled, r.State));
        Assert.All(_members, m => Assert.Equal(MemberState.Cancelled, progress.StatesOf(m.Id).Last()));
    }

    [Fact]
    public async Task Run_CancelMember_OnAPendingMember_CancelsItWithoutRunningIt()
    {
        var started = new ConcurrentBag<Guid>();
        var gate = new TaskCompletionSource();
        var runner = new GroupRunner(async (cfg, ct) =>
        {
            started.Add(cfg.Id);
            await gate.Task.WaitAsync(ct);
            return await Sqlite(cfg, ct);
        });
        var progress = new SyncProgress();

        var run = runner.RunAsync(_members, Select(), new GroupRunOptions(MaxParallel: 1), progress, CancellationToken.None);
        await WaitUntil(() => progress.StatesOf(_members[0].Id).Contains(MemberState.Running));
        runner.CancelMember(_members[2].Id);
        gate.SetResult();
        var results = await run;

        Assert.Equal(MemberState.Done, results[0].State);
        Assert.Equal(MemberState.Done, results[1].State);
        Assert.Equal(MemberState.Cancelled, results[2].State);
        Assert.DoesNotContain(_members[2].Id, started);
    }

    [Fact]
    public async Task Run_ReportsProgressPerMember()
    {
        var runner = new GroupRunner(Sqlite);
        var progress = new SyncProgress();

        await runner.RunAsync(_members, Select(), Defaults, progress, CancellationToken.None);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal([MemberState.Pending, MemberState.Running, MemberState.Done], progress.StatesOf(_members[i].Id));
            var done = progress.Events.Single(e => e.ConnectionId == _members[i].Id && e.State == MemberState.Done);
            Assert.Equal(10 + i, done.Rows);
            Assert.NotNull(done.Duration);
            Assert.Null(done.Error);
        }
    }

    [Fact]
    public async Task Run_ReportsAFailedMemberWithItsError()
    {
        var runner = new GroupRunner((cfg, ct) => throw new InvalidOperationException("boom"));
        var progress = new SyncProgress();

        await runner.RunAsync([_members[0]], Select(), Defaults, progress, CancellationToken.None);

        var failed = progress.Events.Single(e => e.State == MemberState.Failed);
        Assert.Contains("boom", failed.Error);
    }

    [Fact]
    public async Task Run_PassesTheRowLimitToEachMember()
    {
        var runner = new GroupRunner(Sqlite);

        var results = await runner.RunAsync(_members, Select(), new GroupRunOptions(MaxRows: 3), null, CancellationToken.None);

        Assert.All(results, r =>
        {
            var set = Assert.Single(Assert.Single(r.Statements).Sets);
            Assert.Equal(3, set.Rows.Count);
            Assert.True(set.Truncated);
        });
    }

    [Fact]
    public async Task Run_DisposesEveryMemberConnection()
    {
        var created = new ConcurrentBag<ScriptConnection>();
        var runner = new GroupRunner(async (cfg, ct) =>
        {
            var c = await Sqlite(cfg, ct);
            created.Add(c);
            return c;
        });

        await runner.RunAsync(_members, Select(), Defaults, null, CancellationToken.None);

        Assert.Equal(3, created.Count);
        Assert.All(created, c => Assert.Throws<ObjectDisposedException>(() =>
            c.RunAsync(Select(), new ScriptOptions(), CancellationToken.None).GetAsyncEnumerator()
                .MoveNextAsync().AsTask().GetAwaiter().GetResult()));
    }
}
