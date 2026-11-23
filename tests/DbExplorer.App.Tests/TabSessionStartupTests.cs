using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Config;

namespace DbExplorer.App.Tests;

/// <summary>The startup wiring of the tab session: restored after the window opened, written back when it closes (temp data dir only).</summary>
public class TabSessionStartupTests : ShellTestBase
{
    private string SessionFile => Path.Combine(T.Fx.Dir, "session.json");
    private TabSessionStore SessionStore => new(SessionFile);

    private (MainWindow Window, MainViewModel Main) Start(bool persistenceEnabled = true)
    {
        var main = NewMain();
        main.UseTableTabs(T.Fx.Sessions);
        var window = new MainWindow { DataContext = main };
        var startup = new StartupConfig(main.Tree.Config, null, null, persistenceEnabled);
        SessionStartup.Wire(window, main, startup, SessionStore, () => SessionStore.Load());
        return (window, main);
    }

    [AvaloniaFact]
    public async Task Window_RestoresTheSavedTabsAfterItOpened_LoadsOnlyTheActiveOne_AndWritesTheSessionBackOnClose()
    {
        SessionStore.Save(new SavedTabs(1,
        [
            new SavedTab(SavedTabKind.Table, T.Config.Id, null, "customers", RawWhere: "id > 1"),
            new SavedTab(SavedTabKind.Table, T.Config.Id, null, "orders"),
            new SavedTab(SavedTabKind.Script, T.Config.Id, ScriptId: Guid.NewGuid()),
        ]));
        var (window, main) = Start();
        Assert.Empty(main.Tabs);   // nothing before the window opened

        window.Show();
        await Pump();

        Assert.Equal(3, main.Tabs.Count);
        var customers = (TableTabViewModel)main.Tabs[0];
        var orders = (TableTabViewModel)main.Tabs[1];
        Assert.Same(orders, main.ActiveTab);
        Assert.True(customers.IsDeferred);
        Assert.Empty(customers.Rows);
        await WaitFor(() => orders.Rows.Count == 3);

        main.ActiveTab = main.Tabs[2];   // the user ends on the script tab
        File.Delete(SessionFile);
        window.Close();

        var saved = SessionStore.Load().Session!;
        Assert.Equal(2, saved.ActiveIndex);
        Assert.Equal(["customers", "orders", null], saved.Tabs.Select(t => t.Table));
        Assert.Equal("id > 1", saved.Tabs[0].RawWhere);   // a tab that never loaded keeps what it was restored with
    }

    [AvaloniaFact]
    public async Task Window_CorruptSessionFile_IsMovedAside_StartsWithNoTabs_AndTellsTheUser()
    {
        File.WriteAllText(SessionFile, "{ broken");
        var (window, main) = Start();

        window.Show();
        await Pump();

        Assert.Empty(main.Tabs);
        Assert.Contains("session.json", main.Status.Message);
        Assert.Single(Directory.GetFiles(T.Fx.Dir, "session.json.bad-*"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Window_WithPersistenceDisabled_NeitherRestoresNorWritesTheSession()
    {
        var original = new SavedTabs(0, [new SavedTab(SavedTabKind.Table, T.Config.Id, null, "customers")]);
        SessionStore.Save(original);
        var before = File.ReadAllText(SessionFile);
        var (window, main) = Start(persistenceEnabled: false);

        window.Show();
        await Pump();

        Assert.Empty(main.Tabs);
        window.Close();
        Assert.Equal(before, File.ReadAllText(SessionFile));
    }

    [AvaloniaFact]
    public async Task Window_SessionFileLockedAtStart_IsNotOverwrittenOnceItIsReleased()
    {
        SessionStore.Save(new SavedTabs(0, [new SavedTab(SavedTabKind.Table, T.Config.Id, null, "customers")]));
        var before = File.ReadAllText(SessionFile);
        var (window, main) = Start();
        main.SessionSaveDelay = TimeSpan.FromMilliseconds(40);

        var locked = new FileStream(SessionFile, FileMode.Open, FileAccess.Read, FileShare.None);   // e.g. an antivirus scan
        window.Show();
        await Pump();
        locked.Dispose();
        await Task.Delay(300);   // longer than the save delay: a wrongly scheduled write would have landed

        Assert.Empty(main.Tabs);   // could not be read: unsaved scripts only
        Assert.Equal(before, File.ReadAllText(SessionFile));
        window.Close();
        Assert.Equal(before, File.ReadAllText(SessionFile));   // not even on close
    }

    private static async Task Pump()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
        Assert.True(condition(), "timed out");
    }
}
