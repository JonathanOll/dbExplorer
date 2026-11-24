using DbExplorer.Core.Config;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Tests.Upgrade;

public class DbUpdatePathSettingTests : IDisposable
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
    public void DefaultIsNull()
    {
        Assert.Null(AppConfig.Empty().DbUpdatePath);
        Assert.Null(Store().Load().DbUpdatePath);   // no file at all
    }

    [Fact]
    public void OldConfigWithoutTheSetting_LoadsWithNull()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """{ "Folders": ["A"], "Connections": [], "VirtualKeys": {} }""");

        var config = Store().Load();

        Assert.Null(config.DbUpdatePath);
        Assert.Equal(["A"], config.Folders);
    }

    [Fact]
    public void SetValue_SurvivesSaveAndLoad()
    {
        var store = Store();
        var config = AppConfig.Empty();
        config.DbUpdatePath = @"C:\repos\n2f\Web\Common\n2jWeb.Data\dbupdate";

        store.Save(config);

        Assert.Equal(@"C:\repos\n2f\Web\Common\n2jWeb.Data\dbupdate", store.Load().DbUpdatePath);
    }

    [Fact]
    public void ConfigCopy_KeepsTheValue()
    {
        var config = AppConfig.Empty();
        config.DbUpdatePath = @"D:\dbupdate";

        var copy = config with { Folders = ["X"] };

        Assert.Equal(@"D:\dbupdate", copy.DbUpdatePath);
    }
}
