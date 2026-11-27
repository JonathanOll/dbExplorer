using System.Data.Common;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using DbExplorer.Core.Config;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Metadata;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;
using DbExplorer.Core.Tunnels;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

public class SshTunnelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-ssh-" + Guid.NewGuid().ToString("N"));

    public SshTunnelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a pooled handle still closing */ }
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plain) => "p:" + plain;
        public string Unprotect(string protectedValue) => protectedValue[2..];
    }

    private sealed class Prompt(bool answer) : IHostKeyPrompt
    {
        public int Asked { get; private set; }

        public Task<bool> ConfirmNewHostAsync(HostKeyInfo key, CancellationToken ct)
        {
            Asked++;
            return Task.FromResult(answer);
        }
    }

    private static readonly HostKeyInfo Key = new("jump", 22, "ssh-ed25519", "SHA256:aaa");

    private KnownHostsStore Store() => new(Path.Combine(_dir, KnownHostsStore.FileName));

    [Fact]
    public async Task HostKey_NewServer_IsStoredOnlyWhenAccepted()
    {
        var store = Store();

        var refused = await Assert.ThrowsAsync<ConnectionStageException>(
            () => new HostKeyVerifier(store, new Prompt(false)).ApproveAsync(Key, default));
        Assert.Equal(ConnectionStage.SshHostKey, refused.Stage);
        Assert.Equal(HostKeyStatus.Unknown, store.Check(Key));

        await new HostKeyVerifier(store, new Prompt(true)).ApproveAsync(Key, default);
        Assert.Equal(HostKeyStatus.Trusted, Store().Check(Key));
        Assert.Equal(HostKeyStatus.Trusted, Store().Check(Key with { Host = "JUMP" }));
        Assert.Equal(HostKeyStatus.Unknown, Store().Check(Key with { Port = 2222 }));
    }

    [Fact]
    public async Task HostKey_WithoutAPrompt_UnknownServerIsRefused() =>
        await Assert.ThrowsAsync<ConnectionStageException>(() => new HostKeyVerifier(Store(), null).ApproveAsync(Key, default));

    [Fact]
    public async Task HostKey_Changed_IsRefusedWithoutAsking_AndTheStoredKeyStays()
    {
        var store = Store();
        store.Add(Key);
        var prompt = new Prompt(true);
        var changed = Key with { Fingerprint = "SHA256:bbb" };

        var error = await Assert.ThrowsAsync<ConnectionStageException>(() => new HostKeyVerifier(store, prompt).ApproveAsync(changed, default));

        Assert.Equal(0, prompt.Asked);
        Assert.Contains("CHANGED", error.Message);
        Assert.Contains("SHA256:aaa", error.Message);
        Assert.Contains("SHA256:bbb", error.Message);
        store.Add(changed);   // never replaces
        Assert.Equal(HostKeyStatus.Trusted, store.Check(Key));
    }

    [Fact]
    public void HostKey_CorruptFile_IsAnError_NotAnEmptyList()
    {
        File.WriteAllText(Store().FilePath, "not json");

        Assert.ThrowsAny<Exception>(() => Store().Check(Key));
    }

    // ---- the real tunnel, failures that need no server ----

    private ISshTunnel Real(SshTunnelConfig config) =>
        SshTunnels.Factory(Store(), new PlainProtector(), null)(config, "db", 5432);

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task RealTunnel_UnreachableServer_FailsAtTheConnectStage_WithoutTheSecret()
    {
        using var tunnel = Real(new SshTunnelConfig("127.0.0.1", ClosedPort(), "me", SshAuthKind.Password, "p:hunter2secret"));

        var error = await Assert.ThrowsAsync<ConnectionStageException>(() => tunnel.EnsureOpenAsync(default));

        Assert.Equal(ConnectionStage.SshConnect, error.Stage);
        Assert.DoesNotContain("hunter2secret", error.ToString());
        Assert.False(tunnel.IsAlive);
    }

    [Fact]
    public async Task RealTunnel_MissingKeyFile_FailsAtTheSignInStage()
    {
        using var tunnel = Real(new SshTunnelConfig("127.0.0.1", 22, "me", SshAuthKind.PrivateKey, KeyFile: Path.Combine(_dir, "none")));

        var error = await Assert.ThrowsAsync<ConnectionStageException>(() => tunnel.EnsureOpenAsync(default));

        Assert.Equal(ConnectionStage.SshSignIn, error.Stage);
    }

    // ---- sessions and the tunnel they own (a fake tunnel; SQLite behind a driver that records the local port) ----

    private sealed class FakeTunnel : ISshTunnel
    {
        public int Port = 40000;
        public int Opens;
        public bool Disposed;
        public Exception? Failure;
        public bool IsAlive { get; set; }

        public Task<int> EnsureOpenAsync(CancellationToken ct)
        {
            if (Failure is not null) throw Failure;
            if (!IsAlive)
            {
                Opens++;
                Port++;
                IsAlive = true;
            }
            return Task.FromResult(Port);
        }

        public string? TakeForwardError() => null;

        public void Dispose()
        {
            Disposed = true;
            IsAlive = false;
        }
    }

    private sealed class TunneledSqlite : IDbDriver
    {
        private readonly SqliteDriver _inner = new();
        public List<int> Ports { get; } = [];
        public int Direct;
        public DbKind Kind => DbKind.Postgres;
        public string ParamPrefix => _inner.ParamPrefix;
        public string Quote(string identifier) => _inner.Quote(identifier);
        public string QuoteTable(TableRef t) => _inner.QuoteTable(t);
        public string CastToText(string c) => _inner.CastToText(c);
        public string EscapeLike(string text) => _inner.EscapeLike(text);
        public string PageSql(string s, string o, int offset, int limit) => _inner.PageSql(s, o, offset, limit);

        public DbConnection CreateConnection(ConnectionConfig cfg, string? plainPassword)
        {
            Direct++;
            return _inner.CreateConnection(cfg with { Kind = DbKind.Sqlite }, null);
        }

        public (string Host, int Port) NetworkTarget(ConnectionConfig cfg) => ("db", 5432);

        public DbConnection CreateTunneledConnection(ConnectionConfig cfg, string? plainPassword, int localPort)
        {
            Ports.Add(localPort);
            return _inner.CreateConnection(cfg with { Kind = DbKind.Sqlite }, null);
        }

        public Task<IReadOnlyList<TableInfo>> LoadSchemaAsync(DbConnection c, CancellationToken ct) => _inner.LoadSchemaAsync(c, ct);    }

    private (SessionManager Manager, ConnectionConfig Config, FakeTunnel Tunnel, TunneledSqlite Driver) Tunneled()
    {
        var file = Path.Combine(_dir, "t.db");
        using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER)";
            cmd.ExecuteNonQuery();
        }
        var protector = new PlainProtector();
        var tunnel = new FakeTunnel();
        var driver = new TunneledSqlite();
        var manager = new SessionManager(new ConfigStore(Path.Combine(_dir, "config.json"), protector),
            new MetadataCache(Path.Combine(_dir, "cache")), protector, _ => driver, tunnelFactory: (_, _, _) => tunnel);
        var config = new ConnectionConfig(Guid.NewGuid(), "c", DbKind.Postgres, null, "db", null, file, null, null, false, false, false)
        {
            Ssh = new SshTunnelConfig("jump", 22, "me", SshAuthKind.Password),
        };
        return (manager, config, tunnel, driver);
    }

    [Fact]
    public async Task Session_OpensThroughItsTunnel_ReopensItAfterADrop_AndClosesIt()
    {
        var (manager, config, tunnel, driver) = Tunneled();

        var session = await manager.GetOrOpenAsync(config, default);
        await using (await session.OpenConnectionAsync(default)) { }
        Assert.Equal(1, tunnel.Opens);

        tunnel.IsAlive = false;
        await using (await session.OpenConnectionAsync(default)) { }

        Assert.Equal(2, tunnel.Opens);
        Assert.Equal(40002, driver.Ports[^1]);
        Assert.Equal(0, driver.Direct);
        Assert.False(tunnel.Disposed);

        manager.Close(config.Id);
        Assert.True(tunnel.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.OpenConnectionAsync(default));
    }

    [Fact]
    public async Task ScriptConnection_IsReplacedWhenTheTunnelDropped()
    {
        var (manager, config, tunnel, _) = Tunneled();
        var session = await manager.GetOrOpenAsync(config, default);
        using var script = new ScriptConnection(session);
        var statements = ScriptSplitter.Split("SELECT 1", DbKind.Sqlite);
        async Task Run()
        {
            await foreach (var result in script.RunAsync(statements, new ScriptOptions(), default))
                Assert.Null(result.Error);
        }

        await Run();
        tunnel.IsAlive = false;
        await Run();

        Assert.Equal(2, tunnel.Opens);
        Assert.True(script.Reopened);
    }

    [Fact]
    public async Task FailedConnect_ClosesTheTunnel()
    {
        var (manager, config, tunnel, _) = Tunneled();
        tunnel.Failure = new ConnectionStageException(ConnectionStage.SshSignIn, "refused");

        var error = await Assert.ThrowsAsync<ConnectionStageException>(() => manager.GetOrOpenAsync(config, default));

        Assert.Equal(ConnectionStage.SshSignIn, error.Stage);
        Assert.True(tunnel.Disposed);
        Assert.Empty(manager.Open);
    }

    [Fact]
    public async Task Test_ReportsTheStage_AndClosesTheTunnel()
    {
        var (manager, config, tunnel, _) = Tunneled();

        await manager.TestAsync(config, null, default);
        Assert.True(tunnel.Disposed);

        var error = await Assert.ThrowsAsync<ConnectionStageException>(
            () => manager.TestAsync(config with { Database = Path.Combine(_dir, "missing", "x.db") }, null, default));
        Assert.Equal(ConnectionStage.Database, error.Stage);
        Assert.StartsWith("Database connection failed", error.Message);
    }

    [Fact]
    public async Task Session_WithoutATunnel_NeverConnectsDirectlyWhenOneIsAsked()
    {
        var (_, config, _, driver) = Tunneled();
        using var session = new Session(config, driver, []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.OpenConnectionAsync(default));
        Assert.Equal(0, driver.Direct);
    }

    // ---- TLS through the tunnel: a stand-in PostgreSQL server notes the name the client asks for ----

    [Fact]
    public async Task Postgres_ThroughATunnel_TlsStillNamesTheRealServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var asked = new TaskCompletionSource<string?>();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var request = new byte[8];
            while (true)
            {
                await stream.ReadExactlyAsync(request);
                var ssl = request[4] == 0x04 && request[5] == 0xD2 && request[7] == 0x2F;   // SSLRequest
                await stream.WriteAsync(new[] { (byte)(ssl ? 'S' : 'N') });
                if (ssl) break;
            }
            await using var tls = new SslStream(stream);
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, name) =>
                    {
                        asked.TrySetResult(name);
                        return null!;   // the name is all this test needs
                    },
                });
            }
            catch (Exception)
            {
                asked.TrySetResult(null);
            }
        });

        IDbDriver driver = new PostgresDriver();
        var config = new ConnectionConfig(Guid.NewGuid(), "c", DbKind.Postgres, null, "db.internal.example", null, "x", "u", null, false, false, false);
        await using var connection = driver.CreateTunneledConnection(config, "pw", ((IPEndPoint)listener.LocalEndpoint).Port);
        await Assert.ThrowsAnyAsync<Exception>(() => connection.OpenAsync());

        Assert.Equal("db.internal.example", await asked.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        listener.Stop();
        Npgsql.NpgsqlConnection.ClearAllPools();
    }

    // ---- opt-in: a real SSH server (see docs/development.md) ----

    /// <summary>DBX_SSH = "user:password@host:port|targetHost:targetPort".</summary>
    [SkippableFact]
    public async Task RealServer_ForwardsAPort_AfterTheHostKeyIsAccepted()
    {
        var spec = Environment.GetEnvironmentVariable("DBX_SSH");
        Skip.If(string.IsNullOrWhiteSpace(spec), "DBX_SSH not set");
        var parts = spec!.Split('|');
        var at = parts[0].LastIndexOf('@');
        var login = parts[0][..at].Split(':', 2);
        var server = parts[0][(at + 1)..].Split(':');
        var target = parts[1].Split(':');
        var prompt = new Prompt(true);
        using var tunnel = SshTunnels.Factory(Store(), new PlainProtector(), prompt)(
            new SshTunnelConfig(server[0], int.Parse(server[1]), login[0], SshAuthKind.Password, "p:" + login[1]),
            target[0], int.Parse(target[1]));

        var port = await tunnel.EnsureOpenAsync(default);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        Assert.True(tunnel.IsAlive);
        Assert.Equal(1, prompt.Asked);
    }
}
