using DbExplorer.Core.Config;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Tests.Updates;

public class AutoUpdateCheckSettingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbExplorer-tests-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class NoProtector : ISecretProtector
    {
        public string Protect(string plain) => plain;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private ConfigStore Store() => new(ConfigPath, new NoProtector());

    [Fact]
    public void DefaultIsOn()
    {
        Assert.True(AppConfig.Empty().AutoUpdateCheck);
        Assert.True(Store().Load().AutoUpdateCheck);   // no file at all
    }

    [Fact]
    public void OldConfigWithoutTheSetting_LoadsWithTheCheckOn()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """{ "Folders": ["A"], "Connections": [], "VirtualKeys": {} }""");

        var config = Store().Load();

        Assert.True(config.AutoUpdateCheck);
        Assert.Equal(["A"], config.Folders);
    }

    [Fact]
    public void SwitchedOff_SurvivesSaveAndLoad()
    {
        var store = Store();
        var config = AppConfig.Empty();
        config.AutoUpdateCheck = false;

        store.Save(config);

        Assert.False(store.Load().AutoUpdateCheck);
    }
}
