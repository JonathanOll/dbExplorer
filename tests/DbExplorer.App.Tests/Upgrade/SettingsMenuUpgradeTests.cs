using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests.Upgrade;

/// <summary>The gear menu of the real window: Upgrade database..., Settings... and the update toggle.</summary>
public class SettingsMenuUpgradeTests : ShellTestBase, IDisposable
{
    private readonly TempDbUpdateTree _tree = TempDbUpdateTree.Standard();
    private readonly FakeUpgradeDialogs _upgrade = new();

    void IDisposable.Dispose()
    {
        _tree.Dispose();
        Dispose();
    }

    private (MainViewModel Main, MainWindow Window, Button Gear) Open(string? dbUpdatePath)
    {
        var main = NewMain();
        main.Tree.Config.DbUpdatePath = dbUpdatePath;
        main.UseUpgrade(_upgrade);
        var window = new MainWindow { DataContext = main };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var gear = window.GetVisualDescendants().OfType<Button>().First(b => b.Flyout is MenuFlyout && Avalonia.Automation.AutomationProperties.GetName(b) == "Settings");
        return (main, window, gear);
    }

    private static MenuItem Item(Button gear, string header) =>
        ((MenuFlyout)gear.Flyout!).Items.OfType<MenuItem>().Single(i => i.Header as string == header);

    [AvaloniaFact]
    public void Flyout_HoldsTheEntriesInOrder()
    {
        var (_, window, gear) = Open(null);

        var items = ((MenuFlyout)gear.Flyout!).Items.Cast<Control>().ToList();

        Assert.Equal("Upgrade database…", (items[0] as MenuItem)?.Header);
        Assert.Equal("Settings…", (items[1] as MenuItem)?.Header);
        Assert.Equal("Keyboard shortcuts", (items[2] as MenuItem)?.Header);
        Assert.IsType<Separator>(items[3]);
        Assert.Equal("Check for updates automatically", (items[4] as MenuItem)?.Header);
        Assert.Equal(5, items.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void UpgradeEntry_IsDisabledWithNoPath()
    {
        var (_, window, gear) = Open(null);

        gear.Flyout!.ShowAt(gear);
        Dispatcher.UIThread.RunJobs();

        Assert.False(Item(gear, "Upgrade database…").IsEffectivelyEnabled);
        Assert.True(Item(gear, "Settings…").IsEffectivelyEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void UpgradeEntry_IsValidatedWhenTheFlyoutOpens_NotBefore()
    {
        var (main, window, gear) = Open(_tree.Root);
        Assert.False(main.UpgradeAvailable);   // the window is up and nothing touched the disk yet

        gear.Flyout!.ShowAt(gear);
        Dispatcher.UIThread.RunJobs();

        Assert.True(main.UpgradeAvailable);
        Assert.True(Item(gear, "Upgrade database…").IsEffectivelyEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void UpgradeEntry_ClickOpensTheUpgradeDialog()
    {
        var (main, window, gear) = Open(_tree.Root);
        gear.Flyout!.ShowAt(gear);
        Dispatcher.UIThread.RunJobs();

        Item(gear, "Upgrade database…").Command!.Execute(null);
        for (var i = 0; i < 100 && _upgrade.Upgrades.Count == 0; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }

        Assert.True(_upgrade.Upgrades.Count == 1, "status: " + main.Status.Message);
        window.Close();
    }

    [AvaloniaFact]
    public void SettingsEntry_ClickOpensTheSettingsDialogOnce()
    {
        var (_, window, gear) = Open(null);
        gear.Flyout!.ShowAt(gear);
        Dispatcher.UIThread.RunJobs();

        Item(gear, "Settings…").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(_upgrade.Options);
        window.Close();
    }
}
