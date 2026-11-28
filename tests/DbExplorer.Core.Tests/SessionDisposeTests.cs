using System.Data.Common;
using DbExplorer.Core.Config;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Metadata;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using DbExplorer.Core.Tunnels;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

public class SessionDisposeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-dispose-" + Guid.NewGuid().ToString("N"));

    public SessionDisposeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string CreateSqliteFile()
    {
        var file = Path.Combine(_dir, "test.db");
        using (var c = new SqliteConnection($"Data Source={file}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT)";
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        return file;
    }

    private static ConnectionConfig SqliteConfig(string path) =>
        new(Guid.NewGuid(), "local", DbKind.Sqlite, null, null, null, path, null, null, false, false, false);

    /// <summary>Counts how often the session tears its link down (the tunnel is closed exactly once per teardown).</summary>
    private sealed class CountingTunnel : ISshTunnel
    {
        public int Disposals;
        public Task<int> EnsureOpenAsync(CancellationToken ct) => Task.FromResult(0);
        public bool IsAlive => true;
        public string? TakeForwardError() => null;
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    /// <summary>SQLite, but building a connection is slow: it widens the window in which concurrent disposes overlap.</summary>
    private sealed class SlowSqliteDriver : IDbDriver
    {
        private readonly SqliteDriver _inner = new();
        public DbKind Kind => DbKind.Sqlite;
        public string ParamPrefix => _inner.ParamPrefix;
        public string Quote(string identifier) => _inner.Quote(identifier);
        public string QuoteTable(TableRef t) => _inner.QuoteTable(t);
        public string CastToText(string quotedColumn) => _inner.CastToText(quotedColumn);
        public string EscapeLike(string text) => _inner.EscapeLike(text);
        public string PageSql(string selectSql, string orderBy, int offset, int limit) => _inner.PageSql(selectSql, orderBy, offset, limit);

        public DbConnection CreateConnection(ConnectionConfig cfg, string? plainPassword)
        {
            Thread.Sleep(50);
            return _inner.CreateConnection(cfg, plainPassword);
        }

        public Task<IReadOnlyList<TableInfo>> LoadSchemaAsync(DbConnection c, CancellationToken ct) => _inner.LoadSchemaAsync(c, ct);
    }

    [Fact]
    public void Dispose_CalledConcurrently_TearsDownOnce()
    {
        var tunnel = new CountingTunnel();
        var session = new Session(SqliteConfig(CreateSqliteFile()), new ConnectionLink(new SlowSqliteDriver(), tunnel), [], null);
        using var start = new Barrier(8);

        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() => { start.SignalAndWait(); session.Dispose(); })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Equal(1, tunnel.Disposals);
        session.Dispose();
        Assert.Equal(1, tunnel.Disposals);
    }

    [Fact]
    public async Task OpenConnection_AfterDispose_ThrowsObjectDisposed()
    {
        var session = new Session(SqliteConfig(CreateSqliteFile()), new SqliteDriver(), []);
        await (await session.OpenConnectionAsync(CancellationToken.None)).DisposeAsync();

        session.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.OpenConnectionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reads_AfterDispose_ThrowObjectDisposed()
    {
        var session = new Session(SqliteConfig(CreateSqliteFile()), new SqliteDriver(), []);

        session.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ReadSchemaAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.LoadRoutinesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CloseInBackground_DetachesAtOnce_AndDisposesTheSession()
    {
        var protector = new PassThroughProtector();
        var manager = new SessionManager(
            new ConfigStore(Path.Combine(_dir, "config.json"), protector), new MetadataCache(Path.Combine(_dir, "cache")), protector);
        var cfg = SqliteConfig(CreateSqliteFile());
        var session = await manager.GetOrOpenAsync(cfg, CancellationToken.None);

        var closing = manager.CloseInBackground(cfg.Id);

        Assert.Null(manager.Find(cfg.Id));
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.OpenConnectionAsync(CancellationToken.None));
        await manager.CloseInBackground(cfg.Id);   // nothing left to close
    }

    [Fact]
    public async Task Remove_AfterRefresh_LeavesNoCache()
    {
        var protector = new PassThroughProtector();
        var cache = new MetadataCache(Path.Combine(_dir, "cache"));
        var manager = new SessionManager(new ConfigStore(Path.Combine(_dir, "config.json"), protector), cache, protector);
        var cfg = SqliteConfig(CreateSqliteFile());
        await manager.GetOrOpenAsync(cfg, CancellationToken.None);
        await manager.RefreshSchemaAsync(cfg.Id, CancellationToken.None);
        Assert.NotEmpty(cache.Load(cfg.Id));

        manager.Remove(cfg.Id);

        Assert.Empty(cache.Load(cfg.Id));
        await manager.RefreshSchemaAsync(cfg.Id, CancellationToken.None);   // closed: does not write the cache again
        Assert.Empty(cache.Load(cfg.Id));
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plain) => plain;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
