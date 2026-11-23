using DbExplorer.App;
using DbExplorer.Core.Config;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests;

public class StartupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-startup-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public StartupTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ConfigStore Store() => new(ConfigPath, new PlainProtector());

    [Fact]
    public void CorruptConfig_IsMovedAside_AndAppStartsEmptyWithANotice()
    {
        File.WriteAllText(ConfigPath, "{ broken");

        var startup = ConfigBootstrapper.Load(Store());

        Assert.Empty(startup.Config.Connections);
        Assert.True(startup.PersistenceEnabled);
        Assert.Null(startup.Banner);
        Assert.Contains("corrupt", startup.Notice);
        Assert.False(File.Exists(ConfigPath));
        Assert.Equal("{ broken", File.ReadAllText(Assert.Single(Directory.GetFiles(_dir, "config.json.bad-*"))));
    }

    [Fact]
    public void NullConnectionElement_IsTreatedAsCorrupt_NotACrash()
    {
        File.WriteAllText(ConfigPath, """{ "Folders": [], "Connections": [null], "VirtualKeys": {} }""");

        var startup = ConfigBootstrapper.Load(Store());

        Assert.Empty(startup.Config.Connections);
        Assert.True(startup.PersistenceEnabled);
        Assert.Single(Directory.GetFiles(_dir, "config.json.bad-*"));
    }

    [Fact]
    public void UnreadableConfig_StartsInReadOnlyConfigMode_AndTouchesNothing()
    {
        File.WriteAllText(ConfigPath, """{ "Folders": ["Keep"], "Connections": [], "VirtualKeys": {} }""");
        using var locked = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var startup = ConfigBootstrapper.Load(Store());

        Assert.False(startup.PersistenceEnabled);
        Assert.StartsWith("Could not read config.json (", startup.Banner);
        Assert.EndsWith("Changes will NOT be saved.", startup.Banner);
        Assert.Empty(startup.Config.Folders);
        Assert.Equal(["config.json"], Directory.GetFiles(_dir).Select(Path.GetFileName)); // nothing else written
        locked.Dispose();
        Assert.Contains("Keep", File.ReadAllText(ConfigPath)); // original untouched
    }

    [Fact]
    public void CorruptConfig_WhenItCannotBeMovedAside_StartsInReadOnlyConfigMode()
    {
        File.WriteAllText(ConfigPath, "{ broken");
        using var readers = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read); // read ok, rename refused

        var startup = ConfigBootstrapper.Load(Store());

        Assert.False(startup.PersistenceEnabled);
        Assert.NotNull(startup.Banner);
        Assert.Empty(Directory.GetFiles(_dir, "config.json.bad-*"));
        readers.Dispose();
        Assert.Equal("{ broken", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void ValidConfig_LoadsNormally()
    {
        Store().Save(new AppConfig(["A"], [], new()));

        var startup = ConfigBootstrapper.Load(Store());

        Assert.Equal(["A"], startup.Config.Folders);
        Assert.True(startup.PersistenceEnabled);
        Assert.Null(startup.Notice);
        Assert.Null(startup.Banner);
    }
}

public class AppPathsTests
{
    private static string? NoEnv(string _) => null;

    [Fact]
    public void DataDirOnly_NeverFallsBackToTheRealDBeaverWorkspace()
    {
        var paths = AppPaths.Resolve(["--data-dir", @"C:\tmp\x"], NoEnv);

        Assert.Equal(@"C:\tmp\x", paths.DataDir);
        Assert.Equal(@"C:\tmp\x\dbeaver", paths.DBeaverDir);
    }

    [Fact]
    public void FlagWithoutValue_IsAnExplicitError()
    {
        Assert.Throws<ArgumentException>(() => AppPaths.Resolve(["--data-dir"], NoEnv));
        Assert.Throws<ArgumentException>(() => AppPaths.Resolve(["--data-dir", "--dbeaver-dir", "x"], NoEnv));
    }
}
