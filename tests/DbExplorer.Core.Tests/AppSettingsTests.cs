using DbExplorer.Core.Config;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Tests;

public class AppSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbExplorer-settings-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plain) => plain + "#";
        public string Unprotect(string protectedValue) => protectedValue[..^1];
    }

    [Fact]
    public void Defaults_AreTheDocumentedOnes()
    {
        var s = new AppSettings();

        Assert.Equal(ThemeChoice.System, s.Appearance.Theme);
        Assert.Equal(100, s.Appearance.UiScalePercent);
        Assert.Equal(AccentPreset.Blue, s.Appearance.Accent);
        Assert.Equal(28, s.Appearance.RowHeight);
        Assert.Equal("", s.Editor.FontFamily);
        Assert.Equal(13, s.Editor.FontSize);
        Assert.True(s.Editor.LineNumbers);
        Assert.False(s.Editor.WordWrap);
        Assert.Equal(4, s.Editor.TabSize);
        Assert.Equal(12.5, s.Results.GridFontSize);
        Assert.True(s.Results.Zebra);
        Assert.Equal(1000, s.Results.DefaultRowLimit);
        Assert.Equal("NULL", s.Results.NullLabel);
        Assert.Equal(200, s.Results.MaxCellLength);
    }

    [Theory]
    [InlineData(GridDensity.Comfortable, 28)]
    [InlineData(GridDensity.Compact, 24)]
    [InlineData(GridDensity.Spacious, 32)]
    public void Density_SetsTheRowHeight(GridDensity density, double height) =>
        Assert.Equal(height, new AppearanceSettings { Density = density }.RowHeight);

    [Fact]
    public void Normalized_ClampsBadValues()
    {
        var bad = new AppSettings
        {
            Appearance = new() { UiScalePercent = 400, Theme = (ThemeChoice)42, Accent = (AccentPreset)99 },
            Editor = new() { FontSize = 3, TabSize = 5, FontFamily = "  Cascadia Mono " },
            Results = new() { GridFontSize = double.NaN, DefaultRowLimit = -4, MaxCellLength = 5, NullText = (NullDisplay)9 },
        }.Normalized();

        Assert.Equal(150, bad.Appearance.UiScalePercent);
        Assert.Equal(ThemeChoice.System, bad.Appearance.Theme);
        Assert.Equal(AccentPreset.Blue, bad.Appearance.Accent);
        Assert.Equal(10, bad.Editor.FontSize);
        Assert.Equal(4, bad.Editor.TabSize);
        Assert.Equal("Cascadia Mono", bad.Editor.FontFamily);
        Assert.Equal(12.5, bad.Results.GridFontSize);
        Assert.Equal(1, bad.Results.DefaultRowLimit);
        Assert.Equal(20, bad.Results.MaxCellLength);
        Assert.Equal(NullDisplay.Null, bad.Results.NullText);
    }

    [Fact]
    public void ConfigStore_RoundTripsTheSettings()
    {
        var store = new ConfigStore(ConfigPath, new PlainProtector());
        var config = AppConfig.Empty();
        config.Settings = new AppSettings
        {
            Appearance = new() { Theme = ThemeChoice.Dark, UiScalePercent = 125, Accent = AccentPreset.Teal, Density = GridDensity.Compact },
            Editor = new() { FontFamily = "Consolas", FontSize = 15, WordWrap = true, TabSize = 2, ShowWhitespace = true },
            Results = new() { Zebra = false, NullText = NullDisplay.Empty, ThousandsSeparator = true, DateFormat = DateDisplay.Locale },
        };
        store.Save(config);

        var loaded = store.Load().Settings;

        Assert.Equal(config.Settings, loaded);
        Assert.Contains("\"Theme\": \"Dark\"", File.ReadAllText(ConfigPath));   // readable enum names, not numbers
    }

    [Fact]
    public void OldConfigWithoutSettings_LoadsTheDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """{ "Folders": [], "Connections": [], "VirtualKeys": {}, "AutoUpdateCheck": false }""");

        var config = new ConfigStore(ConfigPath, new PlainProtector()).Load();

        Assert.Equal(new AppSettings(), config.Settings);
        Assert.False(config.AutoUpdateCheck);
    }

    [Fact]
    public void ConfigWithNullOrOutOfRangeSettings_LoadsSafeValues()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """
            { "Folders": [], "Connections": [], "VirtualKeys": {},
              "Settings": { "Appearance": null, "Editor": { "FontSize": 999, "TabSize": 3 }, "Results": { "DefaultRowLimit": 0 } } }
            """);

        var s = new ConfigStore(ConfigPath, new PlainProtector()).Load().Settings;

        Assert.Equal(new AppearanceSettings(), s.Appearance);
        Assert.Equal(24, s.Editor.FontSize);
        Assert.Equal(4, s.Editor.TabSize);
        Assert.Equal(1, s.Results.DefaultRowLimit);
    }

    [Fact]
    public void SavingSettings_DoesNotTouchTheOtherConfigFields()
    {
        var store = new ConfigStore(ConfigPath, new PlainProtector());
        var config = AppConfig.Empty();
        config.Folders.Add("Prod");
        config.DbUpdatePath = @"C:\dbupdate";
        config.ShowGroups = false;
        store.Save(config);

        var loaded = store.Load();
        loaded.Settings = new AppSettings { Appearance = new() { Theme = ThemeChoice.Light } };
        store.Save(loaded);
        var again = store.Load();

        Assert.Equal(["Prod"], again.Folders);
        Assert.Equal(@"C:\dbupdate", again.DbUpdatePath);
        Assert.False(again.ShowGroups);
        Assert.Equal(ThemeChoice.Light, again.Settings.Appearance.Theme);
    }
}
