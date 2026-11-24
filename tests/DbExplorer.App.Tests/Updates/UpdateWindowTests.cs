using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.Updates;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using DbExplorer.Core.Updates;

namespace DbExplorer.App.Tests.Updates;

/// <summary>The update flow on the real window: the banner, the setting in the settings menu, and the exit prompts of the close path.</summary>
public class UpdateWindowTests : IDisposable
{
    private readonly TableTabFixture _t = new();
    private readonly FakeEditDialogs _dialogs = new();
    private readonly List<string> _log = [];

    public void Dispose() => _t.Dispose();

    private async Task<(MainViewModel Main, TableTabViewModel Tab)> MainWithOneTab()
    {
        var tree = _t.Fx.CreateTree(new AppConfig([], [_t.Config], new()));
        var session = await _t.OpenSession();
        var hooks = new TableTabHooks(
            ct => _t.Sessions.GetOrOpenAsync(_t.Config, ct).ContinueWith(x => (Session?)x.Result, ct),
            (_, _, _) => Task.CompletedTask, _dialogs, () => tree.Config, tree.SetVirtualKey);
        var tab = new TableTabViewModel(session, new TableRef(null, "customers"), hooks);
        await tab.LoadAsync();
        var main = new MainViewModel(tree, _t.Fx.Status);
        main.Tabs.Add(tab);
        main.ActiveTab = tab;
        return (main, tab);
    }

    private UpdateViewModel Wire(MainViewModel main, MainWindow window, bool autoCheck = true, Action<bool>? save = null)
    {
        var checker = new UpdateViewModelTests.FakeChecker();
        var vm = new UpdateViewModel(checker, new UpdateViewModelTests.FakeDownloader(_log), new UpdateViewModelTests.FakeApplier(_log),
            () => Path.Combine(_t.Fx.Dir, "staging"), autoCheck, false, save)
        {
            Host = new WindowUpdateHost(main, window),
        };
        main.UseUpdates(vm);
        return vm;
    }

    private static async Task PumpUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static Button? ButtonNamed(MainWindow window, string text) =>
        window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == text);

    [AvaloniaFact]
    public async Task Banner_ShowsWhenAnUpdateIsAvailable_AndLaterHidesIt()
    {
        var (main, _) = await MainWithOneTab();
        var window = new MainWindow { DataContext = main };
        var vm = Wire(main, window);
        window.Show();

        Assert.False(ButtonNamed(window, "Update and restart")?.IsEffectivelyVisible ?? false);

        await vm.CheckAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(ButtonNamed(window, "Update and restart")!.IsEffectivelyVisible);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Version 1.2.0 is available.");

        ButtonNamed(window, "Later")!.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(ButtonNamed(window, "Update and restart")!.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SettingsMenu_TogglesTheAutomaticCheck()
    {
        var (main, _) = await MainWithOneTab();
        var window = new MainWindow { DataContext = main };
        var saved = new List<bool>();
        var vm = Wire(main, window, save: saved.Add);
        window.Show();

        var settings = window.GetVisualDescendants().OfType<Button>().First(b => b.Flyout is MenuFlyout);
        var item = (MenuItem)((MenuFlyout)settings.Flyout!).Items.OfType<MenuItem>()
            .Single(i => i.Header as string == "Check for updates automatically");
        settings.Flyout.ShowAt(settings);   // bindings of a menu apply when it opens
        Dispatcher.UIThread.RunJobs();
        Assert.True(item.IsChecked);   // the setting reaches the menu through the binding

        item.IsChecked = false;        // what a click on the item does
        Assert.False(vm.AutoCheckEnabled);
        Assert.Equal([false], saved);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Update_WithPendingEdits_AsksTheCloseQuestion_ThenClosesWithoutAskingAgain()
    {
        var (main, tab) = await MainWithOneTab();
        tab.SetCell(0, 1, "x");
        var window = new MainWindow { DataContext = main };
        var vm = Wire(main, window);
        window.Show();
        await vm.CheckAsync();
        _dialogs.Choice = SaveChoice.Discard;

        var update = vm.UpdateCommand.ExecuteAsync(null);
        await PumpUntil(() => update.IsCompleted);
        await PumpUntil(() => !window.IsVisible);

        Assert.Equal(1, _dialogs.AskCount);   // the close that follows does not ask a second time
        Assert.False(window.IsVisible);
        Assert.Equal(["download", "launch"], _log);   // the host here is the real shell: its prompt is the dialog counted above
    }

    [AvaloniaFact]
    public async Task Update_CancelledAtThePrompt_KeepsTheWindowAndTheEdits()
    {
        var (main, tab) = await MainWithOneTab();
        tab.SetCell(0, 1, "x");
        var window = new MainWindow { DataContext = main };
        var vm = Wire(main, window);
        window.Show();
        await vm.CheckAsync();
        _dialogs.Choice = SaveChoice.Cancel;

        await vm.UpdateCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVisible);
        Assert.True(tab.HasPendingEdits);
        Assert.DoesNotContain("launch", _log);
        Assert.Equal(UpdateState.Available, vm.State);
        window.Close();
    }
}
