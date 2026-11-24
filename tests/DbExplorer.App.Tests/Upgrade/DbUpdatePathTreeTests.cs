using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests.Upgrade;

public class DbUpdatePathTreeTests : IDisposable
{
    private readonly TreeFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void SetDbUpdatePath_WritesTheConfigFile_AndReturnsTrue()
    {
        var tree = _fx.CreateTree(AppConfig.Empty());

        Assert.True(tree.SetDbUpdatePath(@"C:\dbupdate"));

        Assert.Equal(@"C:\dbupdate", tree.Config.DbUpdatePath);
        Assert.Equal(@"C:\dbupdate", _fx.Store.Load().DbUpdatePath);
    }

    [Fact]
    public void SetDbUpdatePath_Null_SwitchesTheFeatureOff()
    {
        var tree = _fx.CreateTree(AppConfig.Empty());
        tree.SetDbUpdatePath(@"C:\dbupdate");

        Assert.True(tree.SetDbUpdatePath(null));

        Assert.Null(_fx.Store.Load().DbUpdatePath);
    }

    [Fact]
    public void WithPersistenceOff_ReturnsFalse_ChangesMemory_AndReportsNotSaved()
    {
        var tree = new ConnectionTreeViewModel(_fx.Store, AppConfig.Empty(), _fx.Sessions, _fx.Status, _fx.Dialogs, _fx.DBeaverDir, persistenceEnabled: false);

        Assert.False(tree.SetDbUpdatePath(@"C:\dbupdate"));

        Assert.Equal(@"C:\dbupdate", tree.Config.DbUpdatePath);
        Assert.Contains("NOT saved", _fx.Status.Message);
        Assert.False(File.Exists(_fx.ConfigPath));
    }

    [Fact]
    public void FailingSave_ReturnsFalse_AndReports()
    {
        Directory.CreateDirectory(_fx.ConfigPath);   // the config path is a directory: the save cannot succeed
        var tree = _fx.CreateTree(AppConfig.Empty());

        Assert.False(tree.SetDbUpdatePath(@"C:\dbupdate"));

        Assert.Contains("Cannot save config", _fx.Status.Message);
        Assert.Equal(@"C:\dbupdate", tree.Config.DbUpdatePath);
    }

    [Fact]
    public void SetAutoUpdateCheck_StillWorks()
    {
        var tree = _fx.CreateTree(AppConfig.Empty());

        Assert.True(tree.SetAutoUpdateCheck(false));

        Assert.False(_fx.Store.Load().AutoUpdateCheck);
    }
}
