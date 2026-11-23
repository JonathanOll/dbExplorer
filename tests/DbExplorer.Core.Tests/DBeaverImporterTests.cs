using System.Security.Cryptography;
using System.Text;
using DbExplorer.Core.Config;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Tests;

public class DBeaverImporterTests : IDisposable
{
    private const string N2FLocalId = "microsoft-1a05bf273cf-1a6d26bd70a9b734";
    private static readonly byte[] DBeaverKey = Convert.FromHexString("babb4a9f774ab853c96c2d653dfe544a");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbExplorer-dbeaver-" + Guid.NewGuid().ToString("N"));

    public DBeaverImporterTests()
    {
        Directory.CreateDirectory(_dir);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "data-sources.json"),
            Path.Combine(_dir, "data-sources.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(string plain) => new string(plain.Reverse().ToArray()) + "#";
        public string Unprotect(string protectedValue) => new string(protectedValue[..^1].Reverse().ToArray());
    }

    private static void WriteCredentials(string dir, string json)
    {
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Key = DBeaverKey;
        aes.GenerateIV();
        var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(json), aes.IV, PaddingMode.PKCS7);
        File.WriteAllBytes(Path.Combine(dir, "credentials-config.json"), aes.IV.Concat(cipher).ToArray());
    }

    [Fact]
    public void Import_ReadsFoldersAndConnections()
    {
        var target = AppConfig.Empty();

        var result = DBeaverImporter.Import(_dir, target, new ReversingProtector());

        Assert.Equal(9, result.Added);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(4, target.Folders.Count);
        Assert.Equal(9, target.Connections.Count);
        Assert.Contains("Translate", target.Folders);

        var local = Assert.Single(target.Connections, c => c.Name == "N2F local");
        Assert.Equal(DbKind.SqlServer, local.Kind);
        Assert.Equal("127.0.0.1", local.Host);
        Assert.Equal(1433, local.Port);
        Assert.Equal("n2f", local.Database);
        Assert.Equal("N2F", local.Folder);
        Assert.True(local.TrustServerCertificate);

        var remote = Assert.Single(target.Connections, c => c.Name == "N2F resto ano");
        Assert.Equal("devsql.example.test", remote.Host);
        Assert.False(remote.TrustServerCertificate);

        var keeple = Assert.Single(target.Connections, c => c.Name == "KeepleAuth");
        Assert.Equal(DbKind.Postgres, keeple.Kind);
        Assert.Equal(5454, keeple.Port);
        Assert.Null(keeple.Folder);

        var sqlite = Assert.Single(target.Connections, c => c.Name == "Admin SQLite");
        Assert.Equal(DbKind.Sqlite, sqlite.Kind);
        Assert.Equal(@"C:\Temp\_N2F\adminDB.db", sqlite.Database);
        Assert.Null(sqlite.Host);
        Assert.Null(sqlite.Port);

        Assert.All(target.Connections, c => Assert.False(c.ReadOnly));
        Assert.Equal(9, target.Connections.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void Import_MissingCredentialsFile_WarnsAndLeavesCredentialsEmpty()
    {
        var target = AppConfig.Empty();

        var result = DBeaverImporter.Import(_dir, target, new ReversingProtector());

        Assert.Contains(result.Warnings, w => w.Contains("credentials-config.json"));
        Assert.All(target.Connections, c =>
        {
            Assert.Null(c.User);
            Assert.Null(c.ProtectedPassword);
        });
    }

    [Fact]
    public void Import_DecryptsCredentials()
    {
        WriteCredentials(_dir, "{\"" + N2FLocalId + "\":{\"#connection\":{\"user\":\"sa\",\"password\":\"pw\"}}}");
        var protector = new ReversingProtector();
        var target = AppConfig.Empty();

        var result = DBeaverImporter.Import(_dir, target, protector);

        Assert.Empty(result.Warnings);
        var local = Assert.Single(target.Connections, c => c.Name == "N2F local");
        Assert.Equal("sa", local.User);
        Assert.NotNull(local.ProtectedPassword);
        Assert.NotEqual("pw", local.ProtectedPassword);
        Assert.Equal("pw", protector.Unprotect(local.ProtectedPassword!));

        var other = Assert.Single(target.Connections, c => c.Name == "KeepleAuth");
        Assert.Null(other.User);
        Assert.Null(other.ProtectedPassword);
    }

    [Fact]
    public void Import_UnreadableCredentialsFile_WarnsButStillImportsConnections()
    {
        File.WriteAllBytes(Path.Combine(_dir, "credentials-config.json"), new byte[] { 1, 2, 3 });
        var target = AppConfig.Empty();

        var result = DBeaverImporter.Import(_dir, target, new ReversingProtector());

        Assert.Equal(9, result.Added);
        Assert.Contains(result.Warnings, w => w.Contains("credentials-config.json"));
    }

    [Fact]
    public void Import_RunTwice_SkipsExistingNames()
    {
        var target = AppConfig.Empty();
        var protector = new ReversingProtector();

        DBeaverImporter.Import(_dir, target, protector);
        var second = DBeaverImporter.Import(_dir, target, protector);

        Assert.Equal(0, second.Added);
        Assert.Equal(9, second.Skipped);
        Assert.Equal(9, target.Connections.Count);
        Assert.Equal(4, target.Folders.Count);
        Assert.Single(target.VirtualKeys);
    }

    [Fact]
    public void Import_ImportsVirtualKeys()
    {
        var target = AppConfig.Empty();

        DBeaverImporter.Import(_dir, target, new ReversingProtector());

        var local = Assert.Single(target.Connections, c => c.Name == "N2F local");
        var keys = Assert.Single(target.VirtualKeys);
        Assert.Equal(local.Id, keys.Key);
        var entry = Assert.Single(keys.Value);
        Assert.Equal("liquibase.DATABASECHANGELOG", entry.Key);
        Assert.Equal(new[] { "*" }, entry.Value);
    }

    [Fact(Skip = "manual")]
    public void Manual_ImportsRealDBeaverDirectory()
    {
        var target = AppConfig.Empty();

        var result = DBeaverImporter.Import(DBeaverImporter.DefaultDirectory, target, new ReversingProtector());

        Assert.Equal(9, result.Added);
    }
}
