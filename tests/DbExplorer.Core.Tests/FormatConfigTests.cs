using DbExplorer.Core.Config;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Tests;

public class FormatConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbExplorer-tests-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plain) => plain;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    [Fact]
    public void FormatOptions_SurviveSaveAndLoad()
    {
        var store = new ConfigStore(ConfigPath, new PlainProtector());
        var config = AppConfig.Empty();
        config.Format = new SqlFormatOptions { KeywordCase = KeywordCase.Lower, IndentSize = 2, UseTabs = true, CommaPosition = CommaPosition.Leading, MaxLineWidth = 80 };

        store.Save(config);
        var loaded = store.Load();

        Assert.Equal(config.Format, loaded.Format);
    }

    [Fact]
    public void OldFile_WithoutFormat_LoadsTheDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """{"Folders":[],"Connections":[],"VirtualKeys":{}}""");

        var loaded = new ConfigStore(ConfigPath, new PlainProtector()).Load();

        Assert.Equal(new SqlFormatOptions(), loaded.Format);
    }

    [Fact]
    public void HandEditedFormat_IsClamped_AndNullFallsBackToTheDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """{"Folders":[],"Connections":[],"VirtualKeys":{},"Format":{"IndentSize":99,"MaxLineWidth":5,"KeywordCase":42}}""");
        var loaded = new ConfigStore(ConfigPath, new PlainProtector()).Load();
        Assert.Equal(8, loaded.Format.IndentSize);
        Assert.Equal(40, loaded.Format.MaxLineWidth);
        Assert.Equal(KeywordCase.Upper, loaded.Format.KeywordCase);

        File.WriteAllText(ConfigPath, """{"Folders":[],"Connections":[],"VirtualKeys":{},"Format":null}""");
        Assert.Equal(new SqlFormatOptions(), new ConfigStore(ConfigPath, new PlainProtector()).Load().Format);
    }
}
