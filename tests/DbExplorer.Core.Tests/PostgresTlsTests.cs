using System.Text;
using DbExplorer.Core.Config;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Metadata;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using DbExplorer.Core.Tunnels;
using Npgsql;

namespace DbExplorer.Core.Tests;

public class PostgresTlsTests(PostgresTlsTests.Files files) : IClassFixture<PostgresTlsTests.Files>, IDisposable
{
    public sealed class Files : IDisposable
    {
        internal TestCertificates Certificates { get; } = new();
        public void Dispose() => Certificates.Dispose();
    }

    private readonly TestCertificates _certs = files.Certificates;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-tlscfg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        NpgsqlConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plain) => "p:" + plain;
        public string Unprotect(string protectedValue) => protectedValue[2..];
    }

    private sealed class OpenTunnel(int port) : ISshTunnel
    {
        public bool IsAlive => true;
        public Task<int> EnsureOpenAsync(CancellationToken ct) => Task.FromResult(port);
        public string? TakeForwardError() => null;
        public void Dispose() { }
    }

    private static ConnectionConfig Config(TlsConfig? tls, string host = "127.0.0.1", int? port = null) =>
        new(Guid.NewGuid(), "c", DbKind.Postgres, null, host, port, "d", "u", null, false, false, false) { Tls = tls };

    /// <summary>The Test button's path, directly or through a stand-in tunnel to the server.</summary>
    private async Task<ConnectionStageException> TestAsync(PostgresTlsServer server, TlsConfig? tls, string? tunneledHost = null)
    {
        var protector = new PlainProtector();
        var manager = new SessionManager(new ConfigStore(Path.Combine(_dir, "config.json"), protector),
            new MetadataCache(Path.Combine(_dir, "cache")), protector, tunnelFactory: (_, _, _) => new OpenTunnel(server.Port));
        var config = tunneledHost is null
            ? Config(tls, port: server.Port)
            : Config(tls, tunneledHost) with { Ssh = new SshTunnelConfig("jump", 22, "me", SshAuthKind.Password) };
        return await Assert.ThrowsAsync<ConnectionStageException>(() => manager.TestAsync(config, "pw", default));
    }

    // The stand-in refuses every sign-in: reaching that stage means TLS (or its absence) was accepted.
    private static void AssertReachedSignIn(ConnectionStageException error) =>
        Assert.True(error.Stage == ConnectionStage.DatabaseSignIn, error.ToString());

    private static void AssertTls(ConnectionStageException error, params string[] parts)
    {
        Assert.True(error.Stage == ConnectionStage.Tls, error.ToString());
        Assert.StartsWith("TLS, the secure connection failed: ", error.Message);
        foreach (var part in parts) Assert.Contains(part, error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TlsMode.Prefer)]
    [InlineData(TlsMode.Require)]
    public async Task ModesThatDoNotVerify_AcceptAnyCertificate(TlsMode? mode)
    {
        using var server = new PostgresTlsServer(_certs.Server);

        AssertReachedSignIn(await TestAsync(server, mode is { } m ? new TlsConfig(m) : null));

        Assert.True(server.TlsStartups > 0);
    }

    [Fact]
    public async Task Disable_SendsTheSignInInClear()
    {
        using var server = new PostgresTlsServer(_certs.Server);

        AssertReachedSignIn(await TestAsync(server, new TlsConfig(TlsMode.Disable)));

        Assert.Equal(0, server.TlsStartups);
        Assert.True(server.ClearStartups > 0);
    }

    [Fact]
    public async Task VerifyFull_ThroughATunnel_ChecksTheRealHostName()
    {
        using var server = new PostgresTlsServer(_certs.Server);
        var tls = new TlsConfig(TlsMode.VerifyFull, _certs.CaFile);

        AssertReachedSignIn(await TestAsync(server, tls, tunneledHost: TestCertificates.ServerName));
        Assert.Equal(1, server.TlsStartups);

        AssertTls(await TestAsync(server, tls, tunneledHost: "other.internal.example"),
            "certificate was refused", "'other.internal.example'");
        Assert.Equal(1, server.TlsStartups);
    }

    [Fact]
    public async Task VerifyFull_Direct_RefusesAnotherName_WhichVerifyCaAccepts()
    {
        using var server = new PostgresTlsServer(_certs.Server);

        AssertTls(await TestAsync(server, new TlsConfig(TlsMode.VerifyFull, _certs.CaFile)),
            "certificate was refused", "'127.0.0.1'", _certs.CaFile);
        Assert.Equal(0, server.TlsStartups);

        AssertReachedSignIn(await TestAsync(server, new TlsConfig(TlsMode.VerifyCA, _certs.CaFile)));
    }

    [Theory]
    [InlineData(TlsMode.VerifyCA)]
    [InlineData(TlsMode.VerifyFull)]
    public async Task VerifyingModes_RefuseAnUnknownAuthority(TlsMode mode)
    {
        using var server = new PostgresTlsServer(_certs.Server);

        AssertTls(await TestAsync(server, new TlsConfig(mode, _certs.OtherCaFile), TestCertificates.ServerName),
            "certificate was refused", mode.ToString());
        AssertTls(await TestAsync(server, new TlsConfig(mode), TestCertificates.ServerName),
            "an authority this computer trusts");
        Assert.Equal(0, server.TlsStartups);
    }

    [Fact]
    public async Task MissingRootCaFile_IsNamed()
    {
        using var server = new PostgresTlsServer(_certs.Server);
        var missing = Path.Combine(_dir, "nowhere.crt");

        AssertTls(await TestAsync(server, new TlsConfig(TlsMode.VerifyFull, missing)), "root CA file was not found", missing);
    }

    [Theory]
    [InlineData(TlsMode.Require)]
    [InlineData(TlsMode.VerifyFull)]
    public async Task ServerWithoutTls_IsATlsFailure(TlsMode mode)
    {
        using var server = new PostgresTlsServer(null);

        AssertTls(await TestAsync(server, new TlsConfig(mode)), "does not offer TLS", mode.ToString());
        Assert.Equal(0, server.ClearStartups);
    }

    [Fact]
    public async Task ClientCertificate_WithAnEncryptedKey_IsSent()
    {
        using var server = new PostgresTlsServer(_certs.Server, askClientCertificate: true);
        var tls = new TlsConfig(TlsMode.Require, null, _certs.ClientCertificateFile, _certs.ClientKeyFile,
            "p:" + TestCertificates.Passphrase);

        AssertReachedSignIn(await TestAsync(server, tls));

        Assert.Equal("CN=dbx client", server.ClientSubject);
    }

    [Theory]
    [InlineData("p:wrong")]
    [InlineData(null)]
    public async Task ClientKey_WithoutItsPassphrase_IsATlsFailure(string? passphrase)
    {
        using var server = new PostgresTlsServer(_certs.Server, askClientCertificate: true);
        var tls = new TlsConfig(TlsMode.Require, null, _certs.ClientCertificateFile, _certs.ClientKeyFile, passphrase);

        var error = await TestAsync(server, tls);

        AssertTls(error, "client certificate or its key", "passphrase");
        Assert.DoesNotContain("wrong", error.Message);
    }

    [Fact]
    public async Task MissingClientKeyFile_IsNamed()
    {
        using var server = new PostgresTlsServer(_certs.Server);
        var missing = Path.Combine(_dir, "nowhere.key");

        AssertTls(await TestAsync(server, new TlsConfig(TlsMode.Require, null, _certs.ClientCertificateFile, missing)),
            "client key file was not found", missing);
    }

    [Fact]
    public void NoTlsSettings_LeaveTheDriverDefaults()
    {
        using var c = new PostgresDriver().CreateConnection(Config(null), "pw");

        Assert.DoesNotContain("SSL", c.ConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GSS", c.ConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Certificate", c.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(TlsMode.Disable, SslMode.Disable)]
    [InlineData(TlsMode.Allow, SslMode.Allow)]
    [InlineData(TlsMode.Prefer, SslMode.Prefer)]
    [InlineData(TlsMode.Require, SslMode.Require)]
    [InlineData(TlsMode.VerifyCA, SslMode.VerifyCA)]
    [InlineData(TlsMode.VerifyFull, SslMode.VerifyFull)]
    public void Mode_MapsToTheDriver_AndOnlyEncryptingOnesRuleOutGss(TlsMode mode, SslMode expected)
    {
        var tls = new TlsConfig(mode, @"%TEMP%\ca.crt");

        using var c = new PostgresDriver().CreateConnection(Config(tls), "pw");

        var b = new NpgsqlConnectionStringBuilder(c.ConnectionString);
        Assert.Equal(expected, b.SslMode);
        Assert.Equal(mode >= TlsMode.Require ? GssEncryptionMode.Disable : GssEncryptionMode.Prefer, b.GssEncryptionMode);
        Assert.Equal(TlsConfig.Verifies(mode) ? Environment.ExpandEnvironmentVariables(@"%TEMP%\ca.crt") : null, b.RootCertificate);
    }

    [Fact]
    public void ConfigStore_OldFileHasNoTls_AndSavedSettingsComeBack()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), new PlainProtector());
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath, """
            {"Folders":[],"VirtualKeys":{},"Connections":[{"Id":"6f1c0c3e-0000-0000-0000-000000000001","Name":"old","Kind":"Postgres",
             "Folder":null,"Host":"h","Port":5432,"Database":"d","User":"u","ProtectedPassword":"x","Encrypt":false,
             "TrustServerCertificate":false,"ReadOnly":false}]}
            """);

        var config = store.Load();
        Assert.Null(Assert.Single(config.Connections).Tls);

        var saved = Config(new TlsConfig(TlsMode.VerifyFull, @"C:\ca.crt", @"C:\c.crt", @"C:\c.key", "p:x"));
        config.Connections.Add(saved);
        store.Save(config);

        Assert.Contains("\"VerifyFull\"", File.ReadAllText(store.FilePath, Encoding.UTF8));
        Assert.Equal(saved, store.Load().Connections[^1]);
    }

    [Fact]
    public void SameDatabase_IgnoresTheStoredPassphraseOnly()
    {
        var a = Config(new TlsConfig(TlsMode.Require, null, "c.crt", "c.key", "p:1"));
        var b = a with { Tls = a.Tls! with { ProtectedKeyPassphrase = "p:2" } };

        Assert.False(SessionManager.SameTarget(a, b));
        Assert.True(SessionManager.SameDatabase(a, b));
        Assert.False(SessionManager.SameDatabase(a, a with { Tls = a.Tls! with { Mode = TlsMode.VerifyFull } }));
        Assert.False(SessionManager.SameDatabase(a, a with { Tls = null }));
    }
}
