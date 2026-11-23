using System.Runtime.Versioning;
using DbExplorer.Core.Config;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbExplorer-tests-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(string plain) => new string(plain.Reverse().ToArray()) + "#";
        public string Unprotect(string protectedValue) => new string(protectedValue[..^1].Reverse().ToArray());
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyConfig()
    {
        var store = new ConfigStore(ConfigPath, new ReversingProtector());

        var config = store.Load();

        Assert.Empty(config.Folders);
        Assert.Empty(config.Connections);
        Assert.Empty(config.VirtualKeys);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsFoldersConnectionsAndVirtualKeys()
    {
        var protector = new ReversingProtector();
        var store = new ConfigStore(ConfigPath, protector);
        var id = Guid.NewGuid();
        var connection = new ConnectionConfig(
            id, "Prod", DbKind.SqlServer, "Work", "srv", 1433, "Sales", "sa",
            protector.Protect("s3cret"), true, true, true);
        var virtualKeys = new Dictionary<Guid, Dictionary<string, string[]>>
        {
            [id] = new() { ["dbo.Orders"] = new[] { "OrderId", "LineNo" } }
        };
        var config = new AppConfig(new List<string> { "Work" }, new List<ConnectionConfig> { connection }, virtualKeys);

        store.Save(config);
        var loaded = store.Load();

        Assert.Equal(new[] { "Work" }, loaded.Folders);
        var c = Assert.Single(loaded.Connections);
        Assert.Equal(connection, c);
        Assert.Equal("s3cret", protector.Unprotect(c.ProtectedPassword!));
        Assert.Equal(new[] { "OrderId", "LineNo" }, loaded.VirtualKeys[id]["dbo.Orders"]);
        Assert.Contains("\"SqlServer\"", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Save_NeverWritesPlainPassword()
    {
        var protector = new ReversingProtector();
        var store = new ConfigStore(ConfigPath, protector);
        var connection = new ConnectionConfig(
            Guid.NewGuid(), "Dev", DbKind.Postgres, null, "localhost", 5432, "app", "u",
            protector.Protect("s3cret"), false, false, false);
        var config = new AppConfig(new List<string>(), new List<ConnectionConfig> { connection }, new());

        store.Save(config);

        var text = File.ReadAllText(ConfigPath);
        Assert.DoesNotContain("s3cret", text);
        Assert.False(File.Exists(ConfigPath + ".tmp"));
    }

    [Fact]
    public void LoadOrRecover_CorruptFile_MovesItAsideAndReturnsEmptyConfigWithWarning()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, "{ this is not json");
        var store = new ConfigStore(ConfigPath, new ReversingProtector());

        var result = store.LoadOrRecover();

        Assert.Empty(result.Config.Connections);
        Assert.Empty(result.Config.Folders);
        Assert.NotNull(result.Warning);
        Assert.False(File.Exists(ConfigPath));
        var bad = Assert.Single(Directory.GetFiles(_dir, "config.json.bad-*"));
        Assert.Equal("{ this is not json", File.ReadAllText(bad));
        Assert.Contains(Path.GetFileName(bad), result.Warning);
    }

    [Fact]
    public void LoadOrRecover_NullLists_AreReplacedByEmptyOnes()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """{ "Folders": null, "Connections": null, "VirtualKeys": null }""");
        var store = new ConfigStore(ConfigPath, new ReversingProtector());

        var result = store.LoadOrRecover();

        Assert.Null(result.Warning);
        Assert.Empty(result.Config.Folders);
        Assert.Empty(result.Config.Connections);
        Assert.Empty(result.Config.VirtualKeys);
        Assert.True(File.Exists(ConfigPath));
    }

    [Theory]
    [InlineData("""{ "Folders": [], "Connections": [null], "VirtualKeys": {} }""")]
    [InlineData("""{ "Folders": [null], "Connections": [], "VirtualKeys": {} }""")]
    [InlineData("""{ "Folders": [], "Connections": [], "VirtualKeys": { "00000000-0000-0000-0000-000000000001": null } }""")]
    public void LoadOrRecover_NullElements_AreTreatedAsCorrupt(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, json);
        var store = new ConfigStore(ConfigPath, new ReversingProtector());

        var result = store.LoadOrRecover();

        Assert.NotNull(result.Warning);
        Assert.Empty(result.Config.Connections);
        Assert.Single(Directory.GetFiles(_dir, "config.json.bad-*"));
    }

    [Fact]
    public void LoadOrRecover_ValidOrMissingFile_HasNoWarning()
    {
        var store = new ConfigStore(ConfigPath, new ReversingProtector());
        Assert.Null(store.LoadOrRecover().Warning);

        store.Save(new AppConfig(new() { "A" }, new(), new()));
        var result = store.LoadOrRecover();

        Assert.Null(result.Warning);
        Assert.Equal(new[] { "A" }, result.Config.Folders);
    }

    [Fact]
    public void Load_OldConfigWithoutGroups_HasEmptyGroups()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """{ "Folders": ["A"], "Connections": [], "VirtualKeys": {} }""");
        var store = new ConfigStore(ConfigPath, new ReversingProtector());

        var result = store.LoadOrRecover();

        Assert.Null(result.Warning);
        Assert.NotNull(result.Config.Groups);
        Assert.Empty(result.Config.Groups);
        Assert.Equal(new[] { "A" }, result.Config.Folders);
    }

    [Theory]
    [InlineData("""{ "Folders": [], "Connections": [], "VirtualKeys": {}, "Groups": null }""")]
    [InlineData("""{ "Folders": [], "Connections": [], "VirtualKeys": {}, "Groups": [null] }""")]
    public void LoadOrRecover_NullGroups_AreTreatedAsCorrupt(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, json);
        var store = new ConfigStore(ConfigPath, new ReversingProtector());

        var result = store.LoadOrRecover();

        Assert.NotNull(result.Warning);
        Assert.Single(Directory.GetFiles(_dir, "config.json.bad-*"));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsGroups()
    {
        var store = new ConfigStore(ConfigPath, new ReversingProtector());
        var c1 = Guid.NewGuid();
        var c2 = Guid.NewGuid();
        var gid = Guid.NewGuid();
        var config = new AppConfig(new(), new(), new())
        {
            Groups = new() { new ConnectionGroup(gid, "Tenants", new() { c1, c2 }) }
        };

        store.Save(config);
        var loaded = store.Load();

        var g = Assert.Single(loaded.Groups);
        Assert.Equal(gid, g.Id);
        Assert.Equal("Tenants", g.Name);
        Assert.Equal(new[] { c1, c2 }, g.ConnectionIds);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void DpapiProtector_RoundTrips()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protector = new DpapiSecretProtector();

        var cipher = protector.Protect("s3cret");

        Assert.NotEqual("s3cret", cipher);
        Assert.DoesNotContain("s3cret", cipher);
        Assert.Equal("s3cret", protector.Unprotect(cipher));
    }
}

