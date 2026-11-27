using DbExplorer.Core.Config;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbExplorer.Core.Tests;

public class ConnectionAuthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-auth-" + Guid.NewGuid().ToString("N"));

    public ConnectionAuthTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plain) => "p:" + plain;
        public string Unprotect(string protectedValue) => protectedValue[2..];
    }

    private static ConnectionConfig Config(DbKind kind, AuthMode auth = AuthMode.Password, string host = "db.example.test", int? port = null) =>
        new(Guid.NewGuid(), "c", kind, null, host, port, "shop", "bob", null, true, false, false) { Auth = auth };

    private static SqlConnectionStringBuilder SqlServer(ConnectionConfig c, string? password = "pw", int? tunnelPort = null)
    {
        IDbDriver driver = new SqlServerDriver();
        using var connection = tunnelPort is { } p
            ? driver.CreateTunneledConnection(c, password, p)
            : driver.CreateConnection(c, password);
        return new SqlConnectionStringBuilder(connection.ConnectionString);
    }

    [Fact]
    public void SqlServer_Password_SendsUserAndPassword()
    {
        var b = SqlServer(Config(DbKind.SqlServer));

        Assert.Equal("bob", b.UserID);
        Assert.Equal("pw", b.Password);
        Assert.False(b.IntegratedSecurity);
        Assert.Equal(SqlAuthenticationMethod.NotSpecified, b.Authentication);
    }

    [Fact]
    public void SqlServer_Integrated_SendsNoCredential()
    {
        var b = SqlServer(Config(DbKind.SqlServer, AuthMode.Integrated));

        Assert.True(b.IntegratedSecurity);
        Assert.Equal("", b.UserID);
        Assert.Equal("", b.Password);
    }

    [Theory]
    [InlineData(AuthMode.EntraDefault, "ActiveDirectoryDefault", false)]
    [InlineData(AuthMode.EntraIntegrated, "ActiveDirectoryIntegrated", false)]
    [InlineData(AuthMode.EntraInteractive, "ActiveDirectoryInteractive", false)]
    [InlineData(AuthMode.EntraPassword, "ActiveDirectoryPassword", true)]
    [InlineData(AuthMode.EntraServicePrincipal, "ActiveDirectoryServicePrincipal", true)]
    public void SqlServer_Entra_SetsTheAuthenticationMethod(AuthMode mode, string method, bool sendsPassword)
    {
        var b = SqlServer(Config(DbKind.SqlServer, mode));

        Assert.Equal(method, b.Authentication.ToString());
        Assert.Equal("bob", b.UserID);
        Assert.Equal(sendsPassword ? "pw" : "", b.Password);
    }

    [Fact]
    public void SqlServer_EntraProvidersComeWithTheAzureExtension()
    {
        _ = SqlServer(Config(DbKind.SqlServer, AuthMode.EntraInteractive));

#pragma warning disable CS0618
        foreach (var method in new[]
                 {
                     SqlAuthenticationMethod.ActiveDirectoryInteractive, SqlAuthenticationMethod.ActiveDirectoryDefault,
                     SqlAuthenticationMethod.ActiveDirectoryServicePrincipal, SqlAuthenticationMethod.ActiveDirectoryPassword,
                     SqlAuthenticationMethod.ActiveDirectoryIntegrated,
                 })
            Assert.NotNull(SqlAuthenticationProvider.GetProvider(method));
#pragma warning restore CS0618
    }

    [Fact]
    public void SqlServer_Tunnel_ChecksTheCertificateAgainstTheRealHost()
    {
        var b = SqlServer(Config(DbKind.SqlServer, port: 1444), tunnelPort: 50123);

        Assert.Equal("127.0.0.1,50123", b.DataSource);
        Assert.Equal("db.example.test", b.HostNameInCertificate);
        Assert.False(b.TrustServerCertificate);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, b.Encrypt);
        Assert.Equal("", b.ServerSPN);
    }

    [Fact]
    public void SqlServer_TunnelWithWindowsAuthentication_AsksATicketForTheRealServer()
    {
        var b = SqlServer(Config(DbKind.SqlServer, AuthMode.Integrated), tunnelPort: 50123);

        Assert.Equal("MSSQLSvc/db.example.test:1433", b.ServerSPN);
    }

    [Theory]
    [InlineData("srv", null, "srv", 1433)]
    [InlineData("tcp:srv,1500", null, "srv", 1500)]
    [InlineData(@"srv\SQLEXPRESS", 1501, "srv", 1501)]
    [InlineData(".", null, "localhost", 1433)]
    public void SqlServer_NetworkTarget_ReadsTheHostField(string host, int? port, string expectedHost, int expectedPort)
    {
        Assert.Equal((expectedHost, expectedPort), new SqlServerDriver().NetworkTarget(Config(DbKind.SqlServer, host: host, port: port)));
    }

    [Fact]
    public void SqlServer_NamedInstanceWithoutPort_CannotBeTunneled() =>
        Assert.Throws<NotSupportedException>(() => new SqlServerDriver().NetworkTarget(Config(DbKind.SqlServer, host: @"srv\X")));

    [Fact]
    public void Postgres_Integrated_SendsNoPassword()
    {
        using var c = new PostgresDriver().CreateConnection(Config(DbKind.Postgres, AuthMode.Integrated), "pw");

        Assert.Null(new NpgsqlConnectionStringBuilder(c.ConnectionString).Password);
    }

    [Fact]
    public void Postgres_Tunnel_DialsTheLocalPort_AndRefusesIntegrated()
    {
        IDbDriver driver = new PostgresDriver();
        using var c = driver.CreateTunneledConnection(Config(DbKind.Postgres), "pw", 50124);
        var b = new NpgsqlConnectionStringBuilder(c.ConnectionString);

        Assert.Equal(("127.0.0.1", 50124), (b.Host, b.Port));
        Assert.NotNull(((NpgsqlConnection)c).SslClientAuthenticationOptionsCallback);
        Assert.Throws<NotSupportedException>(
            () => driver.CreateTunneledConnection(Config(DbKind.Postgres, AuthMode.Integrated), null, 50124));
    }

    [Fact]
    public void ConfigStore_OldFileLoadsUnchanged()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, """
            {"Folders":[],"VirtualKeys":{},"Connections":[{"Id":"6f1c0c3e-0000-0000-0000-000000000001","Name":"old","Kind":"SqlServer",
             "Folder":null,"Host":"h","Port":1433,"Database":"d","User":"u","ProtectedPassword":"x","Encrypt":false,
             "TrustServerCertificate":false,"ReadOnly":false}]}
            """);

        var c = Assert.Single(new ConfigStore(path, new PlainProtector()).Load().Connections);

        Assert.Equal(AuthMode.Password, c.Auth);
        Assert.Null(c.Ssh);
        Assert.Equal("x", c.ProtectedPassword);
    }

    [Fact]
    public void ConfigStore_KeepsAuthAndSsh()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), new PlainProtector());
        var saved = Config(DbKind.SqlServer, AuthMode.EntraServicePrincipal) with
        {
            Ssh = new SshTunnelConfig("jump", 2222, "me", SshAuthKind.PrivateKey, KeyFile: @"C:\k", ProtectedPassphrase: "p:x"),
        };
        var config = AppConfig.Empty();
        config.Connections.Add(saved);

        store.Save(config);

        Assert.Equal(saved, Assert.Single(store.Load().Connections));
    }

    [Fact]
    public void SameDatabase_IgnoresStoredSecrets_SameTargetDoesNot()
    {
        var a = Config(DbKind.Postgres) with { Ssh = new SshTunnelConfig("jump", 22, "me", SshAuthKind.Password, "p:1") };
        var b = a with { Ssh = a.Ssh! with { ProtectedPassword = "p:2" } };

        Assert.False(SessionManager.SameTarget(a, b));
        Assert.True(SessionManager.SameDatabase(a, b));
        Assert.False(SessionManager.SameDatabase(a, a with { Ssh = a.Ssh! with { Host = "other" } }));
        Assert.False(SessionManager.SameDatabase(a, a with { Ssh = null }));
    }
}
