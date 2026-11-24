using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using DbExplorer.Core.Upgrade;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests.Upgrade;

/// <summary>Collects upgrade progress synchronously (Progress&lt;T&gt; would post to another thread).</summary>
internal sealed class UpgradeSyncProgress : IProgress<UpgradeProgress>
{
    public ConcurrentQueue<UpgradeProgress> Events { get; } = new();
    public Action<UpgradeProgress>? OnEvent { get; set; }

    public void Report(UpgradeProgress value)
    {
        Events.Enqueue(value);
        OnEvent?.Invoke(value);
    }

    public List<UpgradeProgress> Of(Guid id) => Events.Where(e => e.ConnectionId == id).ToList();
}

public class UpgradeRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-upgrade-" + Guid.NewGuid().ToString("N"));
    private static readonly UpgradeRunOptions Defaults = new();

    public UpgradeRunnerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A SQLite file with the table u(a INTEGER), and the table marker(a INTEGER) unless <paramref name="withMarker"/> is false.</summary>
    private ConnectionConfig Target(int index, bool withMarker = true, bool readOnly = false)
    {
        var file = Path.Combine(_dir, $"t{index}-{Guid.NewGuid():N}.db");
        using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE u (a INTEGER);" + (withMarker ? "CREATE TABLE marker (a INTEGER);" : "");
            cmd.ExecuteNonQuery();
        }
        return new ConnectionConfig(Guid.NewGuid(), "Lite" + index, DbKind.Sqlite, null, null, null, file, null, null, false, false, readOnly);
    }

    private List<ConnectionConfig> Targets(int count) => Enumerable.Range(0, count).Select(i => Target(i)).ToList();

    /// <summary>The unit tests play on SQLite files, so they use the internal seam that lifts the SQL Server requirement.</summary>
    private static UpgradeRunner Runner(Func<ConnectionConfig, CancellationToken, Task<DbConnection>> open) => new(open, requireSqlServer: false);

    private static Task<DbConnection> Sqlite(ConnectionConfig cfg, CancellationToken ct) =>
        new Session(cfg, new SqliteDriver(), [], null).OpenConnectionAsync(ct);

    private static List<long> Rows(ConnectionConfig cfg, string table = "u")
    {
        using var c = new SqliteConnection($"Data Source={cfg.Database};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT a FROM {table} ORDER BY rowid";
        using var r = cmd.ExecuteReader();
        var rows = new List<long>();
        while (r.Read()) rows.Add(r.GetInt64(0));
        return rows;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(5);
        }
    }

    private static UpgradeScript S(string name, string text) => new(name, text);

    [Fact]
    public async Task Run_PlaysScriptsInOrder_AllBatches_OnEveryTarget()
    {
        var targets = Targets(3);
        var scripts = new[] { S("one.sql", "INSERT INTO u VALUES (1)\nGO\nINSERT INTO u VALUES (2)"), S("two.sql", "INSERT INTO u VALUES (3)") };
        var progress = new UpgradeSyncProgress();

        var results = await Runner(Sqlite).RunAsync(targets, scripts, Defaults, progress, CancellationToken.None);

        Assert.Equal(3, results.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(targets[i].Id, results[i].Connection.Id);
            Assert.Equal(MemberState.Done, results[i].State);
            Assert.Null(results[i].Error);
            Assert.Equal([1L, 2L, 3L], Rows(targets[i]));
            Assert.Equal([(ScriptOutcomeState.Done, 2), (ScriptOutcomeState.Done, 1)], results[i].Scripts.Select(s => (s.State, s.BatchesRun)));
            Assert.Equal(["one.sql", "two.sql"], results[i].Scripts.Select(s => s.Name));

            Assert.Equal(
                [(MemberState.Pending, 0), (MemberState.Running, 1), (MemberState.Running, 2), (MemberState.Done, 2)],
                progress.Of(targets[i].Id).Select(e => (e.State, e.ScriptIndex)));
            Assert.All(progress.Of(targets[i].Id), e => Assert.Equal(2, e.ScriptCount));
        }
    }

    [Fact]
    public async Task Run_FirstError_StopsThatDatabaseOnly()
    {
        var targets = new List<ConnectionConfig> { Target(0), Target(1, withMarker: false), Target(2) };
        var scripts = new[]
        {
            S("one.sql", "INSERT INTO u VALUES (1)"),
            S("two.sql", "INSERT INTO marker VALUES (1)"),
            S("three.sql", "INSERT INTO u VALUES (3)"),
        };
        var progress = new UpgradeSyncProgress();

        var results = await Runner(Sqlite).RunAsync(targets, scripts, Defaults, progress, CancellationToken.None);

        Assert.Equal(MemberState.Done, results[0].State);
        Assert.Equal(MemberState.Done, results[2].State);
        Assert.Equal([1L, 3L], Rows(targets[0]));
        Assert.Equal([1L, 3L], Rows(targets[2]));

        var failed = results[1];
        Assert.Equal(MemberState.Failed, failed.State);
        Assert.Contains("two.sql", failed.Error);
        Assert.Contains("batch 1 of 1", failed.Error);
        Assert.Contains("marker", failed.Error);
        Assert.Equal([ScriptOutcomeState.Done, ScriptOutcomeState.Failed, ScriptOutcomeState.Skipped], failed.Scripts.Select(s => s.State));
        Assert.StartsWith("batch 1 of 1: ", failed.Scripts[1].Error);
        Assert.Equal([1L], Rows(targets[1]));

        var last = progress.Of(targets[1].Id).Last();
        Assert.Equal(MemberState.Failed, last.State);
        Assert.Equal(2, last.ScriptIndex);
        Assert.Equal("two.sql", last.ScriptName);
        Assert.Equal(failed.Error, last.Error);
    }

    [Fact]
    public async Task Run_NoTransactionWrapper_TheBatchesBeforeTheFailureStayApplied()
    {
        var target = Target(0);
        var script = S("s.sql", "INSERT INTO u VALUES (1)\nGO\nINSERT INTO nosuchtable VALUES (2)\nGO\nINSERT INTO u VALUES (3)");

        var results = await Runner(Sqlite).RunAsync([target], [script], Defaults, null, CancellationToken.None);

        var r = Assert.Single(results);
        Assert.Equal(MemberState.Failed, r.State);
        Assert.Contains("batch 2 of 3", r.Error);
        Assert.Equal(1, r.Scripts[0].BatchesRun);
        Assert.Equal([1L], Rows(target));
    }

    [Fact]
    public async Task Run_ScriptsWithSemicolonsInsideABlock_RunAsOneBatch()
    {
        var target = Target(0);
        var script = S("s.sql", "INSERT INTO u VALUES (1); INSERT INTO u VALUES (2);");

        var results = await Runner(Sqlite).RunAsync([target], [script], Defaults, null, CancellationToken.None);

        var outcome = Assert.Single(Assert.Single(results).Scripts);
        Assert.Equal(1, outcome.BatchesRun);
        Assert.Equal([1L, 2L], Rows(target));
    }

    [Fact]
    public async Task Run_LargeResultSet_IsNeitherTruncatedNorCancelled()
    {
        var target = Target(0);
        var script = S("s.sql",
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < 5000) SELECT i FROM n\nGO\nINSERT INTO u VALUES (9)");

        var results = await Runner(Sqlite).RunAsync([target], [script], Defaults, null, CancellationToken.None);

        var r = Assert.Single(results);
        Assert.Equal(MemberState.Done, r.State);
        Assert.Equal(2, r.Scripts[0].BatchesRun);
        Assert.Equal([9L], Rows(target));
    }

    [Fact]
    public async Task Run_EmptyAndCommentOnlyScripts_AreDoneWithNoBatch()
    {
        var target = Target(0);
        var scripts = new[] { S("empty.sql", ""), S("comment.sql", "-- nothing\nGO\n/* here */"), S("real.sql", "INSERT INTO u VALUES (1)") };

        var results = await Runner(Sqlite).RunAsync([target], scripts, Defaults, null, CancellationToken.None);

        var r = Assert.Single(results);
        Assert.Equal(MemberState.Done, r.State);
        Assert.Equal([(ScriptOutcomeState.Done, 0), (ScriptOutcomeState.Done, 0), (ScriptOutcomeState.Done, 1)], r.Scripts.Select(s => (s.State, s.BatchesRun)));
    }

    [Theory]
    [InlineData(null, 4)]
    [InlineData(2, 2)]
    public async Task Run_RunsAtMostMaxParallelDatabases(int? maxParallel, int expectedPeak)
    {
        var targets = Targets(6);
        int current = 0, peak = 0;
        var runner = Runner(async (cfg, ct) =>
        {
            var now = Interlocked.Increment(ref current);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            await Task.Delay(120, ct);
            Interlocked.Decrement(ref current);
            return await Sqlite(cfg, ct);
        });
        var options = maxParallel is { } n ? new UpgradeRunOptions(MaxParallel: n) : Defaults;

        var results = await runner.RunAsync(targets, [S("s.sql", "INSERT INTO u VALUES (1)")], options, null, CancellationToken.None);

        Assert.All(results, r => Assert.Equal(MemberState.Done, r.State));
        Assert.Equal(expectedPeak, peak);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(7, 7)]
    public async Task Run_CommandTimeout_IsAppliedToEveryCommand(int? seconds, int expected)
    {
        var fake = new FakeDbConnection(affected: 0);
        var runner = Runner((cfg, ct) => Task.FromResult<DbConnection>(fake));
        var options = seconds is { } s ? new UpgradeRunOptions(CommandTimeoutSeconds: s) : Defaults;
        var cfg = Fake("pg-less");

        await runner.RunAsync([cfg], [S("s.sql", "SELECT 1\nGO\nSELECT 2\nGO\nSELECT 3")], options, null, CancellationToken.None);

        Assert.Equal(3, fake.Commands.Count);
        Assert.All(fake.Commands, c => Assert.Equal(expected, c.CommandTimeout));
        Assert.Equal(["SELECT 1", "SELECT 2", "SELECT 3"], fake.Commands.Select(c => c.CommandText));
    }

    private static ConnectionConfig Fake(string name, bool readOnly = false) =>
        new(Guid.NewGuid(), name, DbKind.SqlServer, null, "h", null, "d", null, null, false, false, readOnly);

    [Fact]
    public async Task Run_CancelOneMember_WhileConnecting_OthersGo()
    {
        var targets = Targets(3);
        var hung = targets[1];
        var runner = Runner(async (cfg, ct) =>
        {
            if (cfg.Id == hung.Id) await Task.Delay(Timeout.Infinite, ct);
            return await Sqlite(cfg, ct);
        });
        var progress = new UpgradeSyncProgress();
        var scripts = new[] { S("s.sql", "INSERT INTO u VALUES (1)") };

        var run = runner.RunAsync(targets, scripts, Defaults, progress, CancellationToken.None);
        await WaitUntil(() => progress.Of(targets[0].Id).Any(e => e.State == MemberState.Done)
                              && progress.Of(targets[2].Id).Any(e => e.State == MemberState.Done));
        Assert.False(run.IsCompleted);   // the run waits for the hung one

        runner.CancelMember(hung.Id);
        var results = await run;

        Assert.Equal(MemberState.Done, results[0].State);
        Assert.Equal(MemberState.Cancelled, results[1].State);
        Assert.Equal(MemberState.Done, results[2].State);
        Assert.All(results[1].Scripts, s => Assert.Equal(ScriptOutcomeState.Skipped, s.State));
        Assert.Empty(Rows(hung));
        Assert.Equal([1L], Rows(targets[0]));
    }

    [Fact]
    public async Task Run_CancelAll_MarksConnectingAndPendingMembersCancelled_WithoutThrowing()
    {
        var targets = Targets(3);
        var connecting = 0;
        var runner = Runner(async (cfg, ct) =>
        {
            Interlocked.Increment(ref connecting);
            await Task.Delay(Timeout.Infinite, ct);
            return await Sqlite(cfg, ct);
        });
        var progress = new UpgradeSyncProgress();
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(targets, [S("s.sql", "INSERT INTO u VALUES (1)")], new UpgradeRunOptions(MaxParallel: 1), progress, cts.Token);
        await WaitUntil(() => Volatile.Read(ref connecting) == 1);   // one connecting, two still pending
        cts.Cancel();
        var results = await run;

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(MemberState.Cancelled, r.State));
        Assert.All(targets, t => Assert.Equal(MemberState.Cancelled, progress.Of(t.Id).Last().State));
        Assert.All(targets, t => Assert.Empty(Rows(t)));
    }

    [Fact]
    public async Task Run_CancelBetweenScripts_KeepsTheDataOfTheFinishedScripts()
    {
        var target = Target(0);
        var scripts = new[] { S("one.sql", "INSERT INTO u VALUES (1)"), S("two.sql", "INSERT INTO u VALUES (2)"), S("three.sql", "INSERT INTO u VALUES (3)") };
        using var cts = new CancellationTokenSource();
        var progress = new UpgradeSyncProgress { OnEvent = e => { if (e.State == MemberState.Running && e.ScriptIndex == 2) cts.Cancel(); } };

        var results = await Runner(Sqlite).RunAsync([target], scripts, Defaults, progress, cts.Token);

        var r = Assert.Single(results);
        Assert.Equal(MemberState.Cancelled, r.State);
        Assert.Equal([ScriptOutcomeState.Done, ScriptOutcomeState.Cancelled, ScriptOutcomeState.Skipped], r.Scripts.Select(s => s.State));
        Assert.Equal([1L], Rows(target));
        var last = progress.Of(target.Id).Last();
        Assert.Equal(MemberState.Cancelled, last.State);
        Assert.Equal(2, last.ScriptIndex);
    }

    [Fact]
    public async Task Run_CancelWhileABatchRuns_PassesTheTokenToTheCommand_AndCancelsThatScript()
    {
        var hanging = new HangingDbConnection();
        var runner = Runner((_, _) => Task.FromResult<DbConnection>(hanging));
        var cfg = Fake("slow");
        var progress = new UpgradeSyncProgress();

        var run = runner.RunAsync([cfg], [S("long.sql", "SELECT 1\nGO\nSELECT 2"), S("after.sql", "SELECT 3")], Defaults, progress, CancellationToken.None);
        await WaitUntil(() => hanging.Started.Task.IsCompleted);
        runner.CancelMember(cfg.Id);
        var results = await run.WaitAsync(TimeSpan.FromSeconds(10));

        var r = Assert.Single(results);
        Assert.Equal(MemberState.Cancelled, r.State);
        Assert.Equal([ScriptOutcomeState.Cancelled, ScriptOutcomeState.Skipped], r.Scripts.Select(s => s.State));
        Assert.Equal(0, r.Scripts[0].BatchesRun);
        Assert.Equal(ConnectionState.Closed, hanging.State);
    }

    [Fact]
    public async Task Run_Refuses_ReadOnlyTarget_BeforeOpeningAnything()
    {
        var targets = new List<ConnectionConfig> { Target(0), Target(1, readOnly: true) };
        var calls = 0;
        var runner = Runner((cfg, ct) => { Interlocked.Increment(ref calls); return Sqlite(cfg, ct); });
        var progress = new UpgradeSyncProgress();

        var ex = await Assert.ThrowsAsync<UpgradeRefusedException>(() =>
            runner.RunAsync(targets, [S("s.sql", "INSERT INTO u VALUES (1)")], Defaults, progress, CancellationToken.None));

        Assert.Contains("Lite1", ex.Message);
        Assert.DoesNotContain("Lite0", ex.Message);
        Assert.Equal(0, calls);
        Assert.Empty(progress.Events);
        Assert.Empty(Rows(targets[0]));
    }

    [Fact]
    public async Task Run_Refuses_MixedEngines_AndNoTargets()
    {
        var calls = 0;
        var runner = Runner((cfg, ct) => { Interlocked.Increment(ref calls); return Sqlite(cfg, ct); });
        var pg = new ConnectionConfig(Guid.NewGuid(), "pg", DbKind.Postgres, null, "h", null, "d", null, null, false, false, false);
        var scripts = new[] { S("s.sql", "SELECT 1") };

        await Assert.ThrowsAsync<UpgradeRefusedException>(() => runner.RunAsync([Target(0), pg], scripts, Defaults, null, CancellationToken.None));
        await Assert.ThrowsAsync<UpgradeRefusedException>(() => runner.RunAsync([], scripts, Defaults, null, CancellationToken.None));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Run_Refuses_NoScripts()
    {
        var calls = 0;
        var runner = Runner((cfg, ct) => { Interlocked.Increment(ref calls); return Sqlite(cfg, ct); });

        await Assert.ThrowsAsync<UpgradeRefusedException>(() => runner.RunAsync(Targets(2), [], Defaults, null, CancellationToken.None));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Run_Refuses_GoRepeat_NamingTheScriptAndLine_WithoutConnecting()
    {
        var calls = 0;
        var runner = Runner((cfg, ct) => { Interlocked.Increment(ref calls); return Sqlite(cfg, ct); });
        var scripts = new[] { S("fine.sql", "SELECT 1"), S("v0860/bad.sql", "SELECT 1\nGO\nSELECT 2\nGO 3\n") };

        var ex = await Assert.ThrowsAsync<UpgradeRefusedException>(() =>
            runner.RunAsync(Targets(2), scripts, Defaults, null, CancellationToken.None));

        Assert.Contains("v0860/bad.sql", ex.Message);
        Assert.DoesNotContain("fine.sql", ex.Message);
        Assert.Contains("line 4", ex.Message);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("SELECT 1\nGO;\nSELECT 2")]
    [InlineData("SELECT 1\nGO SELECT 2")]
    [InlineData("SELECT 1\nUSE other\nGO")]
    public async Task Run_Refuses_GoLikeLinesAndUse_WithoutConnecting(string text)
    {
        var calls = 0;
        var runner = Runner((cfg, ct) => { Interlocked.Increment(ref calls); return Sqlite(cfg, ct); });

        var ex = await Assert.ThrowsAsync<UpgradeRefusedException>(() =>
            runner.RunAsync(Targets(2), [S("v0860/bad.sql", text)], Defaults, null, CancellationToken.None));

        Assert.Contains("v0860/bad.sql", ex.Message);
        Assert.Contains("line 2", ex.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Run_FactoryThrows_MarksThatDatabaseFailed_AndTheOthersGo()
    {
        var targets = Targets(3);
        var runner = Runner((cfg, ct) =>
            cfg.Id == targets[1].Id
                ? throw new InvalidOperationException("The connection became read-only: nothing was run.")
                : Sqlite(cfg, ct));
        var scripts = new[] { S("s.sql", "INSERT INTO u VALUES (1)") };

        var results = await runner.RunAsync(targets, scripts, Defaults, null, CancellationToken.None);

        Assert.Equal(MemberState.Done, results[0].State);
        Assert.Equal(MemberState.Failed, results[1].State);
        Assert.Equal("The connection became read-only: nothing was run.", results[1].Error);
        Assert.Equal(ScriptOutcomeState.Skipped, Assert.Single(results[1].Scripts).State);
        Assert.Equal(MemberState.Done, results[2].State);
        Assert.Empty(Rows(targets[1]));
    }

    [Fact]
    public async Task Run_DisposesEveryConnection_AfterSuccessAndCancel()
    {
        var created = new ConcurrentBag<FakeDbConnection>();
        var runner = Runner((cfg, ct) =>
        {
            var c = new FakeDbConnection(affected: 0);
            created.Add(c);
            return Task.FromResult<DbConnection>(c);
        });
        var cfg = Fake("a");

        await runner.RunAsync([cfg], [S("s.sql", "SELECT 1")], Defaults, null, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var progress = new UpgradeSyncProgress { OnEvent = e => { if (e.State == MemberState.Running) cts.Cancel(); } };
        await runner.RunAsync([cfg], [S("s.sql", "SELECT 1")], Defaults, progress, cts.Token);

        Assert.Equal(2, created.Count);
        Assert.All(created, c => Assert.Equal(ConnectionState.Closed, c.State));
    }

    [Fact]
    public async Task Run_DisposesTheConnectionOfAFailedDatabase()
    {
        var created = new List<DbConnection>();
        var runner = Runner(async (cfg, ct) =>
        {
            var c = await Sqlite(cfg, ct);
            created.Add(c);
            return c;
        });

        var results = await runner.RunAsync([Target(0)], [S("s.sql", "INSERT INTO nosuchtable VALUES (1)")], Defaults, null, CancellationToken.None);

        Assert.Equal(MemberState.Failed, Assert.Single(results).State);
        Assert.Equal(ConnectionState.Closed, Assert.Single(created).State);
    }

    [Fact]
    public async Task Run_ALateConnection_AfterACancel_IsDisposed()
    {
        var fake = new FakeDbConnection(affected: 0);
        var release = new TaskCompletionSource();
        var connecting = false;
        var runner = Runner(async (cfg, ct) =>
        {
            Volatile.Write(ref connecting, true);
            await release.Task;   // ignores the token, like a driver stuck in a login
            return fake;
        });
        var cfg = Fake("slow");

        var run = runner.RunAsync([cfg], [S("s.sql", "SELECT 1")], Defaults, null, CancellationToken.None);
        await WaitUntil(() => Volatile.Read(ref connecting));
        runner.CancelMember(cfg.Id);
        var results = await run;
        Assert.Equal(MemberState.Cancelled, Assert.Single(results).State);

        release.SetResult();
        await WaitUntil(() => fake.State == ConnectionState.Closed);
        Assert.Empty(fake.Commands);
    }

    [Fact]
    public async Task Run_DuplicateTargets_RunOnceKeepingTheFirst_AndSaySoInTheResult()
    {
        var fake = new FakeDbConnection(affected: 0);
        var opened = new ConcurrentBag<Guid>();
        var runner = new UpgradeRunner((cfg, ct) => { opened.Add(cfg.Id); return Task.FromResult<DbConnection>(fake); });
        var a = Fake("a");
        var other = Fake("b");
        var aAgain = a with { Name = "a (copy)" };
        var progress = new UpgradeSyncProgress();

        var results = await runner.RunAsync([a, other, aAgain], [S("s.sql", "SELECT 1")], Defaults, progress, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal([a.Id, other.Id], results.Select(r => r.Connection.Id));
        Assert.Equal("a", results[0].Connection.Name);
        Assert.Equal(2, opened.Count);
        Assert.Equal(1, opened.Count(id => id == a.Id));
        Assert.Contains("2 times", results[0].Note);
        Assert.Null(results[1].Note);
        Assert.Equal(1, progress.Of(a.Id).Count(e => e.State == MemberState.Pending));
        Assert.Contains("NOTE: ", UpgradeLog.Format("L", results));
    }

    [Fact]
    public async Task Run_DuplicateTargets_CancelMemberCancelsTheOneSlot()
    {
        var a = Fake("a");
        var connecting = 0;
        var runner = new UpgradeRunner(async (cfg, ct) =>
        {
            Interlocked.Increment(ref connecting);
            await Task.Delay(Timeout.Infinite, ct);
            return new FakeDbConnection(0);
        });

        var run = runner.RunAsync([a, a], [S("s.sql", "SELECT 1")], Defaults, null, CancellationToken.None);
        await WaitUntil(() => Volatile.Read(ref connecting) == 1);
        runner.CancelMember(a.Id);
        var results = await run;

        Assert.Equal(MemberState.Cancelled, Assert.Single(results).State);
        Assert.Equal(1, connecting);
    }

    [Fact]
    public async Task Run_Refuses_EveryEngineButSqlServer_BeforeOpeningAnything()
    {
        var calls = 0;
        var runner = new UpgradeRunner((cfg, ct) => { Interlocked.Increment(ref calls); return Sqlite(cfg, ct); });
        var pg = new ConnectionConfig(Guid.NewGuid(), "pg", DbKind.Postgres, null, "h", null, "d", null, null, false, false, false);
        var scripts = new[] { S("s.sql", "INSERT INTO u VALUES (1)") };
        var lite = Target(0);

        var ex1 = await Assert.ThrowsAsync<UpgradeRefusedException>(() => runner.RunAsync([lite], scripts, Defaults, null, CancellationToken.None));
        var ex2 = await Assert.ThrowsAsync<UpgradeRefusedException>(() => runner.RunAsync([pg], scripts, Defaults, null, CancellationToken.None));

        Assert.Contains("SQL Server", ex1.Message);
        Assert.Contains("pg", ex2.Message);
        Assert.Equal(0, calls);
        Assert.Empty(Rows(lite));
    }

    [Fact]
    public async Task Run_AReporterThatThrows_NeverLosesTheResults()
    {
        var fake = new FakeDbConnection(affected: 0);
        var runner = new UpgradeRunner((cfg, ct) => Task.FromResult<DbConnection>(fake));
        var targets = new[] { Fake("a"), Fake("b") };

        var results = await runner.RunAsync(targets, [S("s.sql", "SELECT 1")], Defaults, new ThrowingProgress(), CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(MemberState.Done, r.State));
        Assert.Equal(2, fake.Commands.Count);
    }

    [Fact]
    public async Task Run_ASecondConcurrentRunOnTheSameInstance_IsRefused_AndTheFirstIsUntouched()
    {
        var release = new TaskCompletionSource();
        var connecting = 0;
        var runner = new UpgradeRunner(async (cfg, ct) =>
        {
            Interlocked.Increment(ref connecting);
            await release.Task;
            return new FakeDbConnection(0);
        });
        var a = Fake("a");

        var first = runner.RunAsync([a], [S("s.sql", "SELECT 1")], Defaults, null, CancellationToken.None);
        await WaitUntil(() => Volatile.Read(ref connecting) == 1);

        await Assert.ThrowsAsync<UpgradeRefusedException>(() =>
            runner.RunAsync([Fake("b")], [S("s.sql", "SELECT 1")], Defaults, null, CancellationToken.None));
        runner.CancelMember(a.Id);   // still addresses the first run
        release.SetResult();
        Assert.Equal(MemberState.Cancelled, Assert.Single(await first).State);

        // The refused call did not leave the instance locked, and a finished run frees it.
        var again = await runner.RunAsync([Fake("c")], [S("s.sql", "SELECT 1")], Defaults, null, CancellationToken.None);
        Assert.Equal(MemberState.Done, Assert.Single(again).State);
    }

    [Fact]
    public async Task Run_ARefusedRun_DoesNotLockTheInstance()
    {
        var runner = new UpgradeRunner((cfg, ct) => Task.FromResult<DbConnection>(new FakeDbConnection(0)));

        await Assert.ThrowsAsync<UpgradeRefusedException>(() => runner.RunAsync([], [S("s.sql", "SELECT 1")], Defaults, null, CancellationToken.None));
        var results = await runner.RunAsync([Fake("a")], [S("s.sql", "SELECT 1")], Defaults, null, CancellationToken.None);

        Assert.Equal(MemberState.Done, Assert.Single(results).State);
    }
}

internal sealed class ThrowingProgress : IProgress<UpgradeProgress>
{
    public void Report(UpgradeProgress value) => throw new InvalidOperationException("the reporter is broken");
}

/// <summary>A connection whose commands only end when their cancellation token fires: proves the token reaches the command.</summary>
internal sealed class HangingDbConnection : DbConnection
{
    private ConnectionState _state = ConnectionState.Open;
    public TaskCompletionSource Started { get; } = new();

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString { get; set; } = "";
    public override string Database => "hang";
    public override string DataSource => "hang";
    public override string ServerVersion => "0";
    public override ConnectionState State => _state;
    public override void ChangeDatabase(string databaseName) { }
    public override void Close() => _state = ConnectionState.Closed;
    public override void Open() => _state = ConnectionState.Open;
    protected override void Dispose(bool disposing)
    {
        if (disposing) Close();
        base.Dispose(disposing);
    }
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand() => new HangingCommand(this);

    private sealed class HangingCommand(HangingDbConnection connection) : DbCommand
    {
        private readonly FakeParameterCollection _parameters = new();

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => throw new NotSupportedException("only the async path is used");
        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            connection.Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new SqliteParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }
}
