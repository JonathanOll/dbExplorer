using System.Data.Common;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;
using DbExplorer.Core.Upgrade;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests.Upgrade;

/// <summary>
/// The upgrade is the one write path of the feature. These tests pin the other side: the existing "Run on group" stays read-only,
/// whatever the upgrade runner can do, and the two share nothing.
/// </summary>
public class UpgradeIsolationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-upgrade-iso-" + Guid.NewGuid().ToString("N"));

    public UpgradeIsolationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static List<ConnectionConfig> SqlServerMembers(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new ConnectionConfig(Guid.NewGuid(), "c" + i, DbKind.SqlServer, null, "h", null, "d", null, null, false, false, ReadOnly: false))
            .ToList();

    private static GroupRunner NeverConnects(Action onCall) =>
        new((cfg, ct) => { onCall(); throw new InvalidOperationException("must not connect"); });

    [Theory]
    [InlineData("IF NOT EXISTS (SELECT 1 FROM t) BEGIN CREATE NONCLUSTERED INDEX IX_t_name ON t(name) END")]
    [InlineData("CREATE INDEX IX_t_name ON t(name)")]
    [InlineData("ALTER TABLE t ADD c int")]
    [InlineData("UPDATE t SET name = 'x'")]
    public async Task GroupRun_StillRefusesEveryUpgradeStyleWrite_AndConnectsToNothing(string sql)
    {
        var calls = 0;
        var runner = NeverConnects(() => Interlocked.Increment(ref calls));
        var progress = new SyncProgress();

        await Assert.ThrowsAsync<GroupRunRefusedException>(() =>
            runner.RunAsync(SqlServerMembers(3), [new ScriptStatement(sql, 0, sql.Length)], new GroupRunOptions(), progress, CancellationToken.None));

        Assert.Equal(0, calls);
        Assert.Empty(progress.Events);
    }

    [Fact]
    public async Task GroupRun_RefusesTheBatchesOfARealUpgradeScript_HoweverTheyAreCut()
    {
        const string script =
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_a') BEGIN\n  DROP INDEX IF EXISTS [IX_a] ON [dbo].[T];\n  CREATE NONCLUSTERED INDEX [IX_a] ON [dbo].[T] ([c]);\nEND\nGO\nALTER TABLE [dbo].[T] ADD d int NULL\nGO\nUPDATE [dbo].[T] SET d = 0";
        var calls = 0;
        var runner = NeverConnects(() => Interlocked.Increment(ref calls));

        // As the upgrade splits it, as the script tab splits it for a group run, and as one statement per batch.
        foreach (var statements in new[]
                 {
                     ScriptSplitter.SplitBatches(script),
                     ScriptSplitter.Split(script, DbKind.SqlServer),
                     [new ScriptStatement(script, 0, script.Length)],
                 })
        {
            await Assert.ThrowsAsync<GroupRunRefusedException>(() =>
                runner.RunAsync(SqlServerMembers(2), statements, new GroupRunOptions(), null, CancellationToken.None));
        }

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task GroupRun_RefusesAWriteEvenWhenTheUpgradeRunnerJustWroteToTheSameDatabase()
    {
        var file = Path.Combine(_dir, "db-" + Guid.NewGuid().ToString("N") + ".db");
        using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE u (a INTEGER)";
            cmd.ExecuteNonQuery();
        }
        var cfg = new ConnectionConfig(Guid.NewGuid(), "lite", DbKind.Sqlite, null, null, null, file, null, null, false, false, false);

        var upgrade = await new UpgradeRunner((c, ct) => new Session(c, new SqliteDriver(), [], null).OpenConnectionAsync(ct), requireSqlServer: false)
            .RunAsync([cfg], [new UpgradeScript("s.sql", "INSERT INTO u VALUES (1)")], new UpgradeRunOptions(), null, CancellationToken.None);
        Assert.Equal(MemberState.Done, Assert.Single(upgrade).State);

        var groupCalls = 0;
        var group = new GroupRunner((c, ct) =>
        {
            Interlocked.Increment(ref groupCalls);
            return Task.FromResult(new ScriptConnection(new Session(c, new SqliteDriver(), [], null)));
        });
        const string write = "INSERT INTO u VALUES (2)";
        await Assert.ThrowsAsync<GroupRunRefusedException>(() =>
            group.RunAsync([cfg], [new ScriptStatement(write, 0, write.Length)], new GroupRunOptions(), null, CancellationToken.None));

        Assert.Equal(0, groupCalls);
        using var check = new SqliteConnection($"Data Source={file};Pooling=False");
        check.Open();
        using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM u";
        Assert.Equal(1L, count.ExecuteScalar());
    }

    [Theory]
    [InlineData("CREATE NONCLUSTERED INDEX IX ON t(a)")]
    [InlineData("ALTER INDEX IX ON t REBUILD")]
    [InlineData("DROP INDEX IX ON t")]
    [InlineData("ALTER TABLE t ADD c int")]
    [InlineData("UPDATE t SET a = 1")]
    [InlineData("DELETE FROM t")]
    public void ReadOnlyGuard_StillFlagsTheStatementsAnUpgradeRuns(string sql)
    {
        Assert.True(ReadOnlyGuard.IsWrite(sql, DbKind.SqlServer));
    }

    [Fact]
    public void GroupRunner_IsSealed_AndTheUpgradeRunnerIsNotOne()
    {
        Assert.True(typeof(GroupRunner).IsSealed);
        Assert.False(typeof(GroupRunner).IsAssignableFrom(typeof(UpgradeRunner)));
        Assert.False(typeof(UpgradeRunner).IsAssignableFrom(typeof(GroupRunner)));
        Assert.True(typeof(UpgradeRunner).IsSealed);
    }

    [Fact]
    public void TheTwoRunnersHaveDifferentConnectionContracts()
    {
        // The group run is handed a ScriptConnection (read-only forced, rows capped); the upgrade a plain DbConnection.
        var group = typeof(GroupRunner).GetConstructors().Single().GetParameters().Single().ParameterType;
        var upgrade = typeof(UpgradeRunner).GetConstructors().Single().GetParameters().Single().ParameterType;

        Assert.Equal(typeof(Task<ScriptConnection>), group.GetGenericArguments()[2]);
        Assert.Equal(typeof(Task<DbConnection>), upgrade.GetGenericArguments()[2]);
        Assert.NotEqual(group, upgrade);
    }

    [Fact]
    public void TheReadOnlySources_NeverMentionTheUpgradeFeature()
    {
        // GroupRunner, ReadOnlyGuard, ScriptRunner and ScriptConnection must not know the upgrade exists: no reference from the
        // read-only side to the write-capable side (the opposite direction is covered by the type checks above).
        var root = FindRepoRoot();
        Assert.True(root is not null, "DbExplorer.sln was not found above the test binaries: the isolation of the read-only sources cannot be verified");

        foreach (var relative in new[]
                 {
                     @"src\DbExplorer.Core\Groups\GroupRunner.cs",
                     @"src\DbExplorer.Core\Groups\GroupRules.cs",
                     @"src\DbExplorer.Core\Scripting\ReadOnlyGuard.cs",
                     @"src\DbExplorer.Core\Scripting\ScriptRunner.cs",
                     @"src\DbExplorer.Core\Scripting\ScriptConnection.cs",
                 })
        {
            var text = File.ReadAllText(Path.Combine(root!, relative));
            Assert.DoesNotContain("UpgradeRunner", text);
            Assert.DoesNotContain("DbExplorer.Core.Upgrade", text);
        }
    }

    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DbExplorer.sln"))) return dir.FullName;
        return null;
    }
}
