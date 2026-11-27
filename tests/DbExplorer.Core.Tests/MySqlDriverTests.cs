using DbExplorer.Core.Config;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Export;
using DbExplorer.Core.Model;
using DbExplorer.Core.Plans;
using DbExplorer.Core.Querying;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;
using MySqlConnector;

namespace DbExplorer.Core.Tests;

public class MySqlDriverTests : IDisposable
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plain) => "p:" + plain;
        public string Unprotect(string protectedValue) => protectedValue[2..];
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-mysql-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static readonly IDbDriver Driver = DriverFactory.For(DbKind.MySql);

    private static ConnectionConfig Config(TlsConfig? tls = null) =>
        new ConnectionConfig(Guid.NewGuid(), "m", DbKind.MySql, null, "db.example", null, "shop", "u", null, false, false, false) { Tls = tls };

    private static MySqlConnectionStringBuilder Built(System.Data.Common.DbConnection c) => new(c.ConnectionString);

    [Fact]
    public void QuotingPagingAndLike()
    {
        Assert.Equal("`a``b`", Driver.Quote("a`b"));
        Assert.Equal("`t`", Driver.QuoteTable(new TableRef(null, "t")));
        Assert.Equal("`other`.`t`", Driver.QuoteTable(new TableRef("other", "t")));
        Assert.Equal("SELECT * FROM t ORDER BY `id` LIMIT 10 OFFSET 20", Driver.PageSql("SELECT * FROM t", "`id`", 20, 10));
        Assert.Equal("a|%b|_c||d\\", Driver.EscapeLike("a%b_c|d\\"));
    }

    [Fact]
    public void FilterAndDefaultInsert_UseMySqlSyntax()
    {
        var table = new TableInfo(new TableRef(null, "t"), false, [new ColumnInfo("name", "varchar(20)", typeof(string), true, false, 0)], [], []);
        var query = TableQueryBuilder.Build(Driver, new TableQuery(table, [new ColumnFilter("name", "ab")], null, [], 0, 10));

        Assert.Equal("SELECT * FROM `t` WHERE CAST(`name` AS CHAR) LIKE @p0 ESCAPE '|' LIMIT 10 OFFSET 0", query.Sql);
        var insert = Assert.Single(EditPlanner.Plan(Driver, table, [], [new InsertRow(new Dictionary<string, object?>())]));
        Assert.Equal("INSERT INTO `t` () VALUES ()", insert.Command.Sql);
    }

    [Fact]
    public void Literals_ReadTheSameInEverySqlMode()
    {
        string L(object? v) => SqlLiterals.Format(DbKind.MySql, v);

        Assert.Equal("'it''s'", L("it's"));
        Assert.Equal("_utf8mb4 X'615C27'", L("a\\'"));
        Assert.Equal("_utf8mb4 X'610062'", L("a\0b"));
        Assert.Equal("1", L(true));
        Assert.Equal("X'0AFF'", L(new byte[] { 10, 255 }));
        Assert.Equal("'2026-01-02 03:04:05.123456'", L(new DateTime(2026, 1, 2, 3, 4, 5).AddTicks(1234567)));
        Assert.Equal("'2026-01-02 02:00:00'", L(new DateTimeOffset(2026, 1, 2, 3, 0, 0, TimeSpan.FromHours(1))));
        Assert.Equal("'49:00:01.5'", L(new TimeSpan(2, 1, 0, 1, 500)));
        Assert.Equal("'-00:30:00'", L(TimeSpan.FromMinutes(-30)));
        Assert.Equal("NULL", L(double.NaN));
        var preview = EditPlanner.Plan(Driver,
            new TableInfo(new TableRef(null, "t"), false, [new ColumnInfo("s", "text", typeof(string), true, false, 0)], [], []),
            [], [new InsertRow(new Dictionary<string, object?> { ["s"] = "x\\" })])[0].PreviewSql;
        Assert.Equal("INSERT INTO `t` (`s`) VALUES (_utf8mb4 X'785C')", preview);
    }

    [Theory]
    [InlineData("tinyint", "tinyint(1)", typeof(bool))]
    [InlineData("tinyint", "tinyint(4)", typeof(sbyte))]
    [InlineData("tinyint", "tinyint(1) unsigned", typeof(byte))]
    [InlineData("int", "int unsigned", typeof(uint))]
    [InlineData("bigint", "bigint(20)", typeof(long))]
    [InlineData("decimal", "decimal(10,2)", typeof(decimal))]
    [InlineData("datetime", "datetime(6)", typeof(DateTime))]
    [InlineData("time", "time", typeof(TimeSpan))]
    [InlineData("longblob", "longblob", typeof(byte[]))]
    [InlineData("enum", "enum('a','b')", typeof(string))]
    [InlineData("bit", "bit(8)", typeof(ulong))]
    public void MapsTypes(string data, string column, Type expected) => Assert.Equal(expected, MySqlDriver.MapClrType(data, column));

    [Fact]
    public void VirtualKeys_LeaveOutTypesWithoutEquality()
    {
        bool Keyable(string type) => KeyResolver.IsKeyable(new ColumnInfo("c", type, typeof(string), true, false, 0), DbKind.MySql);
        Assert.False(Keyable("json"));
        Assert.False(Keyable("float unsigned"));
        Assert.False(Keyable("point"));
        Assert.True(Keyable("text"));
        Assert.True(Keyable("varchar(10)"));
    }

    [Fact]
    public void ConnectionString_DefaultsAreSafe()
    {
        var b = Built(Driver.CreateConnection(Config(), "secret"));

        Assert.Equal(("db.example", 3306u, "shop", "u", "secret"), (b.Server, b.Port, b.Database, b.UserID, b.Password));
        Assert.False(b.AllowLoadLocalInfile);
        Assert.False(b.AllowPublicKeyRetrieval);
        Assert.True(b.AllowUserVariables);
        Assert.False(b.UseAffectedRows);
        Assert.Equal(MySqlSslMode.Preferred, b.SslMode);
        Assert.Equal("", Built(Driver.CreateConnection(Config() with { Auth = AuthMode.Integrated }, "secret")).Password);
    }

    [Theory]
    [InlineData(TlsMode.Disable, MySqlSslMode.Disabled)]
    [InlineData(TlsMode.Require, MySqlSslMode.Required)]
    [InlineData(TlsMode.VerifyCA, MySqlSslMode.VerifyCA)]
    [InlineData(TlsMode.VerifyFull, MySqlSslMode.VerifyFull)]
    public void Tls_Modes(TlsMode mode, MySqlSslMode expected) =>
        Assert.Equal(expected, Built(Driver.CreateConnection(Config(new TlsConfig(mode)), null)).SslMode);

    [Fact]
    public void Tls_Files_AndAnEncryptedPemKeyIsRefused()
    {
        var pem = Built(Driver.CreateConnection(Config(new TlsConfig(TlsMode.VerifyCA, "ca.pem", "c.pem", "k.pem")), null));
        Assert.Equal(("ca.pem", "c.pem", "k.pem"), (pem.SslCa, pem.SslCert, pem.SslKey));
        var driver = Driver.WithSecrets(new PlainProtector());
        var pfx = Built(driver.CreateConnection(Config(new TlsConfig(TlsMode.Require, ClientCertificate: "c.pfx", ProtectedKeyPassphrase: "p:x")), null));
        Assert.Equal(("c.pfx", "x"), (pfx.CertificateFile, pfx.CertificatePassword));
        var refused = Assert.Throws<NotSupportedException>(() =>
            driver.CreateConnection(Config(new TlsConfig(TlsMode.Require, ClientCertificate: "c.pem", ProtectedKeyPassphrase: "p:x")), null));
        Assert.Contains(".pfx", driver.DescribeTlsFailure(Config(), refused));
    }

    [Fact]
    public void Tunnel_ConnectsLocally_AndVerifyFullStillChecksTheServerName()
    {
        var direct = Built(Driver.CreateTunneledConnection(Config(), null, 4100));
        Assert.Equal(("127.0.0.1", 4100u), (direct.Server, direct.Port));

        var verified = (MySqlConnection)Driver.CreateTunneledConnection(Config(new TlsConfig(TlsMode.VerifyFull)), null, 4100);
        Assert.Equal(MySqlSslMode.Required, Built(verified).SslMode);
        Assert.NotNull(verified.RemoteCertificateValidationCallback);
        Assert.Equal(("db.example", 3306), Driver.NetworkTarget(Config()));
    }

    [Fact]
    public void AuthModes_PasswordOrWindows() =>
        Assert.Equal([AuthMode.Password, AuthMode.Integrated], AuthModes.For(DbKind.MySql));

    [Fact]
    public void ReadOnlyUpgradeAndDriver()
    {
        Assert.IsType<MySqlDriver>(Driver);
        Assert.Contains("T-SQL", Core.Upgrade.UpgradeRules.Refusal([Config()], requireSqlServer: true));
    }

    [Fact]
    public void DBeaverImport_MySqlAndMariaDbWithSsl()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "data-sources.json"), """
            {"connections":{
              "a":{"provider":"mysql","driver":"mysql8","name":"My","configuration":{"host":"h","port":"3307","database":"d",
                   "handlers":{"mysql_ssl":{"enabled":true,"properties":{"ssl.require":"true","ssl.verify.server":"true","ssl.ca.cert":"C:\\ca.pem"}}}}},
              "b":{"provider":"mysql","driver":"mariaDB","name":"Maria","configuration":{"host":"h","database":"d",
                   "provider-properties":{"sslMode":"REQUIRED"}}},
              "c":{"provider":"mariadb","name":"No db","configuration":{"host":"h"}}
            }}
            """);

        var target = AppConfig.Empty();
        var result = DBeaverImporter.Import(_dir, target, new PlainProtector());
        var list = target.Connections;

        Assert.All(list, c => Assert.Equal(DbKind.MySql, c.Kind));
        var my = list.Single(c => c.Name == "My");
        Assert.Equal((3307, TlsMode.VerifyFull, "C:\\ca.pem"), (my.Port, my.Tls!.Mode, my.Tls.RootCertificate));
        Assert.Equal(TlsMode.Require, list.Single(c => c.Name == "Maria").Tls!.Mode);
        Assert.Contains(result.Warnings, w => w.Contains("No db") && w.Contains("no database"));
    }
}
