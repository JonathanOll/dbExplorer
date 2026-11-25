using DbExplorer.App.Settings;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests.Settings;

/// <summary>Settings go to the config file through the tree (the existing settings path), without touching anything else in it.</summary>
public class SettingsPersistenceTests : IDisposable
{
    private readonly TreeFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void SetSettings_WritesTheConfigFile_AndKeepsTheRest()
    {
        var config = AppConfig.Empty();
        config.DbUpdatePath = @"C:\dbupdate";
        config.Folders.Add("Prod");
        var tree = _fx.CreateTree(config);

        Assert.True(tree.SetSettings(new AppSettings { Editor = new() { WordWrap = true } }));

        var loaded = _fx.Store.Load();
        Assert.True(loaded.Settings.Editor.WordWrap);
        Assert.Equal(@"C:\dbupdate", loaded.DbUpdatePath);
        Assert.Equal(["Prod"], loaded.Folders);
    }

    [Fact]
    public void ServiceChange_IsSavedThroughThePersistHook()
    {
        var tree = _fx.CreateTree(AppConfig.Empty());
        var service = new SettingsService(tree.Config.Settings) { Persist = tree.SetSettings };

        service.Update(s => s with { Appearance = s.Appearance with { Theme = ThemeChoice.Dark, Accent = AccentPreset.Pink } });

        var saved = _fx.Store.Load().Settings.Appearance;
        Assert.Equal(ThemeChoice.Dark, saved.Theme);
        Assert.Equal(AccentPreset.Pink, saved.Accent);
    }

    [Fact]
    public void WithPersistenceOff_TheStatusBarSaysTheSettingIsNotSaved_ButItStillApplies()
    {
        var tree = new ConnectionTreeViewModel(_fx.Store, AppConfig.Empty(), _fx.Sessions, _fx.Status, _fx.Dialogs, _fx.DBeaverDir, persistenceEnabled: false);
        var service = new SettingsService(new()) { Persist = tree.SetSettings };

        var saved = service.Update(s => s with { Editor = s.Editor with { FontSize = 18 } });

        Assert.False(saved);
        Assert.Contains("NOT saved", _fx.Status.Message);
        Assert.Equal(18, service.Current.Editor.FontSize);
        Assert.False(File.Exists(_fx.Store.FilePath));
    }

    [Fact]
    public void ReadOnlyConfigFile_ReportsAnErrorInTheStatusBar()
    {
        var tree = _fx.CreateTree(AppConfig.Empty());
        Directory.CreateDirectory(_fx.Store.FilePath);   // a directory where the file should be: Save fails

        Assert.False(tree.SetSettings(new AppSettings()));

        Assert.Contains("Cannot save config", _fx.Status.Message);
    }
}
