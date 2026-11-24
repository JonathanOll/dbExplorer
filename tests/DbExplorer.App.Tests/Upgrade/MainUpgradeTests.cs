using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests.Upgrade;

public class MainUpgradeTests : ShellTestBase, IDisposable
{
    private readonly TempDbUpdateTree _tree = TempDbUpdateTree.Standard();
    private readonly FakeUpgradeDialogs _upgrade = new();

    void IDisposable.Dispose()
    {
        _tree.Dispose();
        Dispose();
    }

    private MainViewModel Main(string? dbUpdatePath = null, AppConfig? config = null)
    {
        var main = NewMain(config: config ?? new AppConfig([], [T.Config], new()));
        main.Tree.Config.DbUpdatePath = dbUpdatePath;
        main.UseUpgrade(_upgrade);
        return main;
    }

    /// <summary>The Options dialog as the user uses it: type a path, press OK (which validates for real).</summary>
    private static bool TypeAndOk(OptionsViewModel vm, string path)
    {
        vm.DbUpdatePath = path;
        return vm.TryConfirm();
    }

    [Fact]
    public void UpgradeAvailable_IsFalseAtConstruction_EvenWithAValidPath()
    {
        var main = Main(_tree.Root);

        Assert.False(main.UpgradeAvailable);
        Assert.False(main.OpenUpgradeCommand.CanExecute(null));
    }

    [Fact]
    public void RefreshUpgradeAvailability_ValidTree_IsTrue()
    {
        var main = Main(_tree.Root);

        main.RefreshUpgradeAvailability();

        Assert.True(main.UpgradeAvailable);
        Assert.True(main.OpenUpgradeCommand.CanExecute(null));
    }

    [Fact]
    public void RefreshUpgradeAvailability_BlankMissingOrAFile_IsFalse()
    {
        var file = Path.Combine(_tree.Root, "cmi-01-service-broker-x.sql");
        foreach (var path in new string?[] { null, "", "  ", Path.Combine(_tree.Root, "nope"), file })
        {
            var main = Main(path);
            main.RefreshUpgradeAvailability();
            Assert.False(main.UpgradeAvailable);
            Assert.False(main.OpenUpgradeCommand.CanExecute(null));
        }
    }

    [Fact]
    public void RefreshUpgradeAvailability_FollowsTheFolderAfterwards()
    {
        var main = Main(_tree.Root);
        main.RefreshUpgradeAvailability();
        Directory.Delete(_tree.Root, recursive: true);

        main.RefreshUpgradeAvailability();

        Assert.False(main.UpgradeAvailable);
    }

    [Fact]
    public async Task OpenOptions_Ok_SavesPathInConfigJson_AndEnablesUpgrade()
    {
        var main = Main();
        _upgrade.OptionsScript = vm => TypeAndOk(vm, $"  {_tree.Root}  ");

        await main.OpenOptionsCommand.ExecuteAsync(null);

        Assert.Equal(_tree.Root, main.Tree.Config.DbUpdatePath);
        Assert.Equal(_tree.Root, T.Fx.Store.Load().DbUpdatePath);
        Assert.True(main.UpgradeAvailable);
        Assert.True(main.OpenUpgradeCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenOptions_StartsFromTheStoredPath()
    {
        var main = Main(_tree.Root);

        await main.OpenOptionsCommand.ExecuteAsync(null);

        Assert.Equal(_tree.Root, Assert.Single(_upgrade.Options).DbUpdatePath);
    }

    [Fact]
    public async Task OpenOptions_Cancel_ChangesNothing()
    {
        var main = Main();
        _upgrade.OptionsScript = vm =>
        {
            vm.DbUpdatePath = _tree.Root;
            return false;
        };

        await main.OpenOptionsCommand.ExecuteAsync(null);

        Assert.Null(main.Tree.Config.DbUpdatePath);
        Assert.False(main.UpgradeAvailable);
        Assert.False(File.Exists(T.Fx.ConfigPath));
    }

    [Fact]
    public async Task OpenOptions_InvalidPath_NeverSaved()
    {
        var main = Main();
        var file = Path.Combine(_tree.Root, "cmi-01-service-broker-x.sql");
        var accepted = new List<bool>();
        _upgrade.OptionsScript = vm =>
        {
            accepted.Add(TypeAndOk(vm, Path.Combine(_tree.Root, "nope")));
            accepted.Add(TypeAndOk(vm, file));
            return accepted.Any(a => a);   // the window only closes with OK when TryConfirm accepted
        };

        await main.OpenOptionsCommand.ExecuteAsync(null);

        Assert.Equal([false, false], accepted);
        Assert.Null(main.Tree.Config.DbUpdatePath);
        Assert.False(File.Exists(T.Fx.ConfigPath));
    }

    [Fact]
    public async Task OpenOptions_ValidTree_PathInfoCountsTheVersionFolders()
    {
        var main = Main();
        string? info = null;
        _upgrade.OptionsScript = vm =>
        {
            Assert.True(TypeAndOk(vm, _tree.Root));
            info = vm.PathInfo;
            return false;
        };

        await main.OpenOptionsCommand.ExecuteAsync(null);

        var count = Directory.GetDirectories(_tree.Root).Count(d => Path.GetFileName(d).StartsWith('v') || Path.GetFileName(d).StartsWith('V'));
        Assert.Equal($"{count} version folders found", info);
    }

    [Fact]
    public async Task OpenOptions_BlankPath_SwitchesTheFeatureOff()
    {
        var main = Main(_tree.Root);
        main.RefreshUpgradeAvailability();
        _upgrade.OptionsScript = vm => TypeAndOk(vm, "");

        await main.OpenOptionsCommand.ExecuteAsync(null);

        Assert.Null(main.Tree.Config.DbUpdatePath);
        Assert.False(main.UpgradeAvailable);
    }

    [Fact]
    public async Task OpenUpgrade_NoConnection_SaysSo_AndOpensNoDialog()
    {
        var tree = T.Fx.CreateTree(new AppConfig([], [], new()));
        tree.Config.DbUpdatePath = _tree.Root;
        var main = new MainViewModel(tree, T.Fx.Status);
        main.UseUpgrade(_upgrade);
        main.RefreshUpgradeAvailability();

        await main.OpenUpgradeCommand.ExecuteAsync(null);

        Assert.Empty(_upgrade.Upgrades);
        Assert.Contains("Select a connection", T.Fx.Status.Message);
    }

    [Fact]
    public async Task OpenUpgrade_InvalidPath_ReportsAndDisables()
    {
        var main = Main(_tree.Root);
        main.RefreshUpgradeAvailability();
        Directory.Delete(_tree.Root, recursive: true);   // gone between the menu opening and the click

        await main.OpenUpgradeCommand.ExecuteAsync(null);

        Assert.Empty(_upgrade.Upgrades);
        Assert.True(T.Fx.Status.IsError);
        Assert.Contains("not found", T.Fx.Status.Message);
        Assert.False(main.UpgradeAvailable);
    }

    [Fact]
    public async Task OpenUpgrade_BuildsTargets_ThisDatabaseThenItsGroups()
    {
        var other = TreeFixture.Conn("Other", database: T.Fx.CreateSqliteFile("other.db"));
        var group = new ConnectionGroup(Guid.NewGuid(), "Items", [T.Config.Id, other.Id]);
        var lonely = new ConnectionGroup(Guid.NewGuid(), "Lonely", [T.Config.Id]);
        var main = Main(_tree.Root, new AppConfig([], [T.Config, other], new()) { Groups = [lonely, group] });
        main.RefreshUpgradeAvailability();

        await main.OpenUpgradeCommand.ExecuteAsync(null);

        var vm = Assert.Single(_upgrade.Upgrades);
        Assert.Equal(2, vm.Targets.Count);
        Assert.Equal([T.Config.Id], vm.Targets[0].Members.Select(m => m.Id));
        Assert.Null(vm.Targets[0].Group);
        Assert.Equal("Items", vm.Targets[1].Group!.Name);
        Assert.Equal(2, vm.Targets[1].Members.Count);
        Assert.NotEmpty(vm.FolderChoices);   // the catalog was scanned
    }

    [Fact]
    public async Task OpenUpgrade_UsesTheActiveTabsConnection()
    {
        var other = TreeFixture.Conn("Other", database: T.Fx.CreateSqliteFile("other.db"));
        var main = Main(_tree.Root, new AppConfig([], [T.Config, other], new()));
        main.RefreshUpgradeAvailability();
        main.Tabs.Add(new PlaceholderTabViewModel(other, new TableRef(null, "users")));
        main.ActiveTab = main.Tabs[0];

        await main.OpenUpgradeCommand.ExecuteAsync(null);

        var vm = Assert.Single(_upgrade.Upgrades);
        Assert.Equal(other.Id, vm.Targets[0].Members.Single().Id);
    }
}
