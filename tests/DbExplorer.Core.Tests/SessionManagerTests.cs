using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DbExplorer.Core.Config;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Metadata;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

public class SessionManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-sessions-" + Guid.NewGuid().ToString("N"));

    public SessionManagerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plain) => "p:" + plain;
        public string Unprotect(string protectedValue) => protectedValue[2..];
    }

    private SessionManager NewManager(out MetadataCache cache)
    {
        cache = new MetadataCache(Path.Combine(_dir, "cache"));
        var protector = new PlainProtector();
        return new SessionManager(new ConfigStore(Path.Combine(_dir, "config.json"), protector), cache, protector);
    }

    private string CreateSqliteFile()
    {
        var file = Path.Combine(_dir, "test.db");
        using (var c = new SqliteConnection($"Data Source={file}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT); CREATE VIEW v_users AS SELECT * FROM users;";
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        return file;
    }

    private static ConnectionConfig SqliteConfig(string path) =>
        new(Guid.NewGuid(), "local", DbKind.Sqlite, null, null, null, path, null, null, false, false, false);

    [Fact]
    public async Task GetOrOpen_LoadsSchemaAndWritesCache()
    {
        var manager = NewManager(out var cache);
        var cfg = SqliteConfig(CreateSqliteFile());

        var session = await manager.GetOrOpenAsync(cfg, CancellationToken.None);

        Assert.Equal(cfg, session.Config);
        Assert.Contains(session.Schema, t => t.Ref.Name == "users" && !t.IsView);
        Assert.Contains(session.Schema, t => t.Ref.Name == "v_users" && t.IsView);
        var cached = cache.Load(cfg.Id);
        Assert.Contains(cached, t => t.Ref.Name == "users" && t.Columns.SequenceEqual(new[] { "id", "name" }));
        Assert.Same(session, Assert.Single(manager.Open));
    }

    [Fact]
    public async Task GetOrOpen_SecondCall_ReturnsSameSession()
    {
        var manager = NewManager(out _);
        var cfg = SqliteConfig(CreateSqliteFile());

        var first = await manager.GetOrOpenAsync(cfg, CancellationToken.None);
        var second = await manager.GetOrOpenAsync(cfg, CancellationToken.None);

        Assert.Same(first, second);
        Assert.Single(manager.Open);
    }

    [Fact]
    public async Task GetOrOpen_ConcurrentCalls_OpenOnlyOneSession()
    {
        var manager = NewManager(out _);
        var cfg = SqliteConfig(CreateSqliteFile());

        var sessions = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => manager.GetOrOpenAsync(cfg, CancellationToken.None)));

        Assert.All(sessions, s => Assert.Same(sessions[0], s));
        Assert.Single(manager.Open);
    }

    [Fact]
    public async Task GetOrOpen_WrongPath_ThrowsAndLeavesNoSession()
    {
        var manager = NewManager(out var cache);
        var cfg = SqliteConfig(Path.Combine(_dir, "missing.db"));

        await Assert.ThrowsAnyAsync<Exception>(() => manager.GetOrOpenAsync(cfg, CancellationToken.None));

        Assert.Empty(manager.Open);
        Assert.Empty(cache.Load(cfg.Id));
        // A later attempt retries instead of replaying the failure.
        File.Copy(CreateSqliteFile(), Path.Combine(_dir, "missing.db"));
        var session = await manager.GetOrOpenAsync(cfg, CancellationToken.None);
        Assert.NotNull(session);
    }

    [Fact]
    public async Task GetOrOpen_Cancelled_ThrowsOperationCanceled()
    {
        var manager = NewManager(out _);
        var cfg = SqliteConfig(CreateSqliteFile());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.GetOrOpenAsync(cfg, cts.Token));

        Assert.Empty(manager.Open);
    }

    [Fact]
    public async Task OpenConnectionAsync_ReturnsNewOpenConnectionEachTime()
    {
        var manager = NewManager(out _);
        var session = await manager.GetOrOpenAsync(SqliteConfig(CreateSqliteFile()), CancellationToken.None);

        await using var a = await session.OpenConnectionAsync(CancellationToken.None);
        await using var b = await session.OpenConnectionAsync(CancellationToken.None);

        Assert.NotSame(a, b);
        Assert.Equal(System.Data.ConnectionState.Open, a.State);
        Assert.Equal(System.Data.ConnectionState.Open, b.State);
    }

    [Fact]
    public async Task Close_RemovesSession_AndReopenCreatesNewOne()
    {
        var manager = NewManager(out _);
        var cfg = SqliteConfig(CreateSqliteFile());
        var first = await manager.GetOrOpenAsync(cfg, CancellationToken.None);

        manager.Close(cfg.Id);

        Assert.Empty(manager.Open);
        var second = await manager.GetOrOpenAsync(cfg, CancellationToken.None);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Remove_ClosesSessionAndDeletesCache()
    {
        var manager = NewManager(out var cache);
        var cfg = SqliteConfig(CreateSqliteFile());
        await manager.GetOrOpenAsync(cfg, CancellationToken.None);

        manager.Remove(cfg.Id);

        Assert.Empty(manager.Open);
        Assert.Empty(cache.Load(cfg.Id));
    }

    [Fact]
    public async Task RefreshSchemaAsync_PicksUpNewTables_AndRewritesCache()
    {
        var manager = NewManager(out var cache);
        var file = CreateSqliteFile();
        var cfg = SqliteConfig(file);
        var session = await manager.GetOrOpenAsync(cfg, CancellationToken.None);
        using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE extra (id INTEGER PRIMARY KEY)";
            cmd.ExecuteNonQuery();
        }

        await manager.RefreshSchemaAsync(cfg.Id, CancellationToken.None);

        Assert.Contains(session.Schema, t => t.Ref.Name == "extra");
        Assert.Contains(cache.Load(cfg.Id), t => t.Ref.Name == "extra");
    }

    [Fact]
    public async Task TestConnectionAsync_Succeeds_ForValidSqlite_AndThrowsForMissingFile()
    {
        await SessionManager.TestConnectionAsync(SqliteConfig(CreateSqliteFile()), null, CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            SessionManager.TestConnectionAsync(SqliteConfig(Path.Combine(_dir, "nope.db")), null, CancellationToken.None));
    }

    // ---- deterministic tests with a fake driver (no network) ----

    private sealed class FakeConnection(FakeDriver owner, ConnectionConfig cfg) : DbConnection
    {
        [AllowNull] public override string ConnectionString { get; set; } = "fake";
        public override string Database => cfg.Database;
        public override string DataSource => cfg.Host ?? "";
        public override string ServerVersion => "0";
        public override System.Data.ConnectionState State => System.Data.ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() => throw new NotSupportedException();
        public override Task OpenAsync(CancellationToken ct) => owner.OnOpen(cfg, ct);
        protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    private sealed class FakeDriver : IDbDriver
    {
        public Func<ConnectionConfig, CancellationToken, Task> OnOpen { get; set; } = (_, _) => Task.CompletedTask;
        public Func<ConnectionConfig, CancellationToken, Task> OnLoadSchema { get; set; } = (_, _) => Task.CompletedTask;
        public int SchemaLoads;
        private ConnectionConfig? _last;

        public DbKind Kind => DbKind.Sqlite;
        public string ParamPrefix => "@";
        public string Quote(string identifier) => identifier;
        public string QuoteTable(TableRef t) => t.Name;
        public string CastToText(string quotedColumn) => quotedColumn;
        public string EscapeLike(string text) => text;
        public string PageSql(string selectSql, string orderBy, int offset, int limit) => selectSql;
        public DbConnection CreateConnection(ConnectionConfig cfg, string? plainPassword) => new FakeConnection(this, _last = cfg);

        public async Task<IReadOnlyList<TableInfo>> LoadSchemaAsync(DbConnection c, CancellationToken ct)
        {
            Interlocked.Increment(ref SchemaLoads);
            await OnLoadSchema(_last!, ct);
            return [new TableInfo(new TableRef(null, "t_" + c.Database), false, [], [], [])];
        }
    }

    private SessionManager FakeManager(FakeDriver driver, out MetadataCache cache)
    {
        cache = new MetadataCache(Path.Combine(_dir, "cache"));
        var protector = new PlainProtector();
        return new SessionManager(new ConfigStore(Path.Combine(_dir, "config.json"), protector), cache, protector, _ => driver);
    }

    private static ConnectionConfig FakeConfig(string database = "db", string host = "h1") =>
        new(Guid.NewGuid(), "fake", DbKind.Sqlite, null, host, null, database, null, null, false, false, false);

    [Fact]
    public async Task GetOrOpen_CancelledWhileConnecting_ThrowsOperationCanceledAndLeavesNothing()
    {
        var entered = new TaskCompletionSource();
        var observedCancel = new TaskCompletionSource();
        var driver = new FakeDriver
        {
            OnOpen = async (_, ct) =>
            {
                entered.SetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { observedCancel.SetResult(); throw; }
            },
        };
        var manager = FakeManager(driver, out var cache);
        var cfg = FakeConfig();
        using var cts = new CancellationTokenSource();

        var open = manager.GetOrOpenAsync(cfg, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        await observedCancel.Task.WaitAsync(TimeSpan.FromSeconds(5)); // the last waiter leaving cancels the connect itself
        Assert.Empty(manager.Open);
        Assert.Empty(cache.Load(cfg.Id));
    }

    [Fact]
    public async Task GetOrOpen_ConnectFails_PropagatesTheDriverExceptionAndNextCallRetries()
    {
        var fail = true;
        var driver = new FakeDriver
        {
            OnOpen = (_, _) => fail ? Task.FromException(new InvalidOperationException("boom: no route")) : Task.CompletedTask,
        };
        var manager = FakeManager(driver, out _);
        var cfg = FakeConfig();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetOrOpenAsync(cfg, CancellationToken.None));

        Assert.Equal("boom: no route", ex.Message);
        Assert.Empty(manager.Open);
        fail = false;
        Assert.NotNull(await manager.GetOrOpenAsync(cfg, CancellationToken.None));
    }

    [Fact]
    public async Task GetOrOpen_FirstCallerCancels_SecondCallerStillGetsTheSession()
    {
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var driver = new FakeDriver { OnOpen = async (_, _) => { entered.TrySetResult(); await gate.Task; } };
        var manager = FakeManager(driver, out _);
        var cfg = FakeConfig();
        using var cts1 = new CancellationTokenSource();

        var first = manager.GetOrOpenAsync(cfg, cts1.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = manager.GetOrOpenAsync(cfg, CancellationToken.None);
        cts1.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        gate.SetResult();

        var session = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(session, Assert.Single(manager.Open));
    }

    [Fact]
    public async Task Close_DuringOpen_DoesNotResurrectSessionOrCache()
    {
        var (driver, entered, release) = BlockingSchemaDriver();
        var manager = FakeManager(driver, out var cache);
        var cfg = FakeConfig();

        var open = manager.GetOrOpenAsync(cfg, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        manager.Close(cfg.Id);
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        Assert.Empty(manager.Open);
        Assert.Empty(cache.Load(cfg.Id));
    }

    [Fact]
    public async Task Remove_DuringOpen_DoesNotRecreateTheDeletedCache()
    {
        var (driver, entered, release) = BlockingSchemaDriver();
        var manager = FakeManager(driver, out var cache);
        var cfg = FakeConfig();

        var open = manager.GetOrOpenAsync(cfg, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        manager.Remove(cfg.Id);
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        Assert.Empty(manager.Open);
        Assert.False(Directory.Exists(Path.Combine(_dir, "cache")) && Directory.GetFiles(Path.Combine(_dir, "cache")).Length > 0);
    }

    [Fact]
    public async Task GetOrOpen_ConfigEditedDuringOpen_NewSettingsWinAndOldAttemptIsDiscarded()
    {
        var (driver, entered, release) = BlockingSchemaDriver(blockFirstOnly: true);
        var manager = FakeManager(driver, out var cache);
        var old = FakeConfig(host: "old-host");
        var edited = old with { Host = "new-host" };

        var first = manager.GetOrOpenAsync(old, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await manager.GetOrOpenAsync(edited, CancellationToken.None); // does not join the old attempt
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("new-host", second.Config.Host);
        var open = Assert.Single(manager.Open);
        Assert.Same(second, open);
        Assert.Single(cache.Load(old.Id));
    }

    [Fact]
    public async Task GetOrOpen_ConfigEditedAfterOpen_ReplacesTheStaleSession()
    {
        var driver = new FakeDriver();
        var manager = FakeManager(driver, out _);
        var old = FakeConfig(host: "old-host");
        var first = await manager.GetOrOpenAsync(old, CancellationToken.None);

        var second = await manager.GetOrOpenAsync(old with { Host = "new-host" }, CancellationToken.None);

        Assert.NotSame(first, second);
        Assert.Same(second, Assert.Single(manager.Open));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.OpenConnectionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetOrOpen_CosmeticChange_ReusesTheSession()
    {
        var manager = FakeManager(new FakeDriver(), out _);
        var cfg = FakeConfig();
        var first = await manager.GetOrOpenAsync(cfg, CancellationToken.None);

        var second = await manager.GetOrOpenAsync(cfg with { Name = "renamed", ReadOnly = true, Folder = "F" }, CancellationToken.None);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task Close_DisposesTheSession_SoFurtherOpensFail()
    {
        var manager = FakeManager(new FakeDriver(), out _);
        var cfg = FakeConfig();
        var session = await manager.GetOrOpenAsync(cfg, CancellationToken.None);

        manager.Close(cfg.Id);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.OpenConnectionAsync(CancellationToken.None));
    }

    // ---- cosmetic changes reach an existing or still connecting session ----

    [Fact]
    public async Task GetOrOpen_ExistingSession_TakesTheCallersReadOnlyFlag()
    {
        var manager = FakeManager(new FakeDriver(), out _);
        var cfg = FakeConfig();
        var session = await manager.GetOrOpenAsync(cfg, CancellationToken.None);
        Assert.False(session.Config.ReadOnly);

        var again = await manager.GetOrOpenAsync(cfg with { ReadOnly = true, Name = "renamed" }, CancellationToken.None);

        Assert.Same(session, again);
        Assert.True(session.Config.ReadOnly);
        Assert.Equal("renamed", session.Config.Name);
    }

    [Fact]
    public async Task GetOrOpen_JoiningAConnectInFlight_ProducesASessionWithTheNewestFlag()
    {
        var (driver, entered, release) = BlockingSchemaDriver();
        var manager = FakeManager(driver, out _);
        var cfg = FakeConfig();

        var first = manager.GetOrOpenAsync(cfg, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = manager.GetOrOpenAsync(cfg with { ReadOnly = true }, CancellationToken.None);   // toggled while connecting
        release.SetResult();

        var session = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(session, await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(session.Config.ReadOnly);
    }

    [Fact]
    public async Task GetOrOpen_DifferentTarget_StillReplacesTheSession()
    {
        var manager = FakeManager(new FakeDriver(), out _);
        var cfg = FakeConfig();
        var session = await manager.GetOrOpenAsync(cfg, CancellationToken.None);

        var other = await manager.GetOrOpenAsync(cfg with { Host = "h2" }, CancellationToken.None);

        Assert.NotSame(session, other);
    }

    [Fact]
    public async Task GetOrOpen_CacheWriteFailure_DoesNotFailTheConnect()
    {
        var protector = new PlainProtector();
        var blocker = Path.Combine(_dir, "cache-is-a-file");
        File.WriteAllText(blocker, "x");   // the cache folder cannot be created: every Save throws
        var manager = new SessionManager(
            new ConfigStore(Path.Combine(_dir, "config.json"), protector), new MetadataCache(blocker), protector, _ => new FakeDriver());

        var session = await manager.GetOrOpenAsync(FakeConfig(), CancellationToken.None);

        Assert.Same(session, Assert.Single(manager.Open));
        await manager.RefreshSchemaAsync(session.Config.Id, CancellationToken.None);   // same for a refresh
    }

    /// <summary>A driver whose schema load blocks until released (only the first load when requested).</summary>
    private static (FakeDriver Driver, TaskCompletionSource Entered, TaskCompletionSource Release) BlockingSchemaDriver(bool blockFirstOnly = false)
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var calls = 0;
        var driver = new FakeDriver
        {
            OnLoadSchema = async (_, _) =>
            {
                if (blockFirstOnly && Interlocked.Increment(ref calls) > 1) return;
                entered.TrySetResult();
                await release.Task;
            },
        };
        return (driver, entered, release);
    }
}

