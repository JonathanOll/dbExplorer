using DbExplorer.App.ViewModels;
using DbExplorer.Core.Config;
using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.Tests;

/// <summary>Open tabs survive closing the app: what is saved, what is restored, and that restoring stays lazy.</summary>
public class TabSessionTests : ShellTestBase
{
    private TabSessionStore SessionStore => new(Path.Combine(T.Fx.Dir, "session.json"));

    /// <summary>A shell that saves its tabs (as the app does once it has restored): restore ran with no session file.</summary>
    private MainViewModel Saving(AppConfig? config = null, bool scriptDialogs = true, bool useStore = true)
    {
        var main = NewMain(scriptDialogs, useStore, config: config);
        main.UseTableTabs(T.Fx.Sessions);
        main.UseTabSession(SessionStore, null);
        main.SessionSaveDelay = TimeSpan.FromMinutes(5);   // only explicit flushes write
        main.RestoreTabs();
        return main;
    }

    /// <summary>A new run of the app over the same data dir: nothing connected, session loaded from the file.</summary>
    private MainViewModel Restarted(AppConfig? config = null)
    {
        var main = NewMain(config: config);
        main.UseTableTabs(T.Fx.Sessions);
        main.UseTabSession(SessionStore, SessionStore.Load().Session);
        main.SessionSaveDelay = TimeSpan.FromMinutes(5);
        return main;
    }

    private SavedTabs Saved() => SessionStore.Load().Session!;

    // ---- saving ----

    [Fact]
    public async Task PersistSession_WritesTheTabsInOrder_WithTheActiveOne_Filters_Where_Sort_AndScripts()
    {
        var main = Saving();
        var customers = await AddTable(main, "customers", activate: false);
        customers.ColumnFilters["name"] = "=Bob";
        customers.RawWhere = "id > 1";
        await customers.ToggleSortAsync("name");
        var orders = await AddTable(main, "orders");
        var script = AddScript(main, "select 1");
        main.ActiveTab = orders;

        main.PersistSession();

        var saved = Saved();
        Assert.Equal(1, saved.ActiveIndex);
        Assert.Equal([SavedTabKind.Table, SavedTabKind.Table, SavedTabKind.Script], saved.Tabs.Select(t => t.Kind));
        var first = saved.Tabs[0];
        Assert.Equal((T.Config.Id, "customers", null), (first.ConnectionId, first.Table, first.Schema));
        Assert.Equal("=Bob", Assert.Single(first.Filters!).Value);
        Assert.Equal(("id > 1", "name", false), (first.RawWhere, first.SortColumn, first.SortDescending));
        Assert.Equal("orders", saved.Tabs[1].Table);
        Assert.Equal(script.Id, saved.Tabs[2].ScriptId);
        Assert.Null(saved.Tabs[2].FilePath);
    }

    [Fact]
    public async Task ChangesAreWrittenAfterTheDebounce_WithoutAnExplicitFlush()
    {
        var main = Saving();
        main.SessionSaveDelay = TimeSpan.FromMilliseconds(40);

        await AddTable(main, "customers");

        await WaitFor(() => SessionStore.Load().Session is { Tabs.Count: 1 });
        Assert.Equal("customers", Saved().Tabs[0].Table);
    }

    [Fact]
    public async Task ChangingTheActiveTab_ClosingATab_AndAppliedFilters_AllReschedule()
    {
        var main = Saving();
        main.SessionSaveDelay = TimeSpan.FromMilliseconds(40);
        var a = await AddTable(main, "customers");
        var b = await AddTable(main, "orders");
        await WaitFor(() => SessionStore.Load().Session is { ActiveIndex: 1 });

        main.ActiveTab = a;
        await WaitFor(() => SessionStore.Load().Session is { ActiveIndex: 0 });

        a.ColumnFilters["name"] = "=Alice";
        await a.ApplyFiltersAsync();
        await WaitFor(() => SessionStore.Load().Session!.Tabs[0].Filters is { Count: 1 });

        await main.CloseTabCommand.ExecuteAsync(b);
        await WaitFor(() => SessionStore.Load().Session!.Tabs.Count == 1);
    }

    [Fact]
    public async Task TabsClosedOneByOne_AreNotRestored()
    {
        var main = Saving();
        var a = await AddTable(main, "customers");
        await AddTable(main, "orders");
        await main.CloseTabCommand.ExecuteAsync(a);

        main.PersistSession();

        Assert.Equal(["orders"], Saved().Tabs.Select(t => t.Table));
    }

    [Fact]
    public void NothingIsWrittenBeforeTheRestoreRan_SoAnEarlyCloseCannotWipeTheSession()
    {
        SessionStore.Save(new SavedTabs(0, [new SavedTab(SavedTabKind.Table, T.Config.Id, null, "customers")]));
        var main = NewMain();
        main.UseTabSession(SessionStore, SessionStore.Load().Session);

        main.PersistSession();   // the window closed before the post-paint restore

        Assert.Equal(["customers"], Saved().Tabs.Select(t => t.Table));
    }

    [Fact]
    public void WithoutASessionStore_NothingIsWritten()
    {
        var main = NewMain();
        main.PersistSession();
        Assert.False(File.Exists(Path.Combine(T.Fx.Dir, "session.json")));
    }

    [Fact]
    public async Task ADiscardedScript_IsNotSaved_AtExit()
    {
        var main = Saving(useStore: false);   // no autosave: closing the window asks about the unsaved script
        var script = AddScript(main, "delete me", persisted: false);
        await AddTable(main, "customers");
        Dialogs.Choices.Enqueue(SaveChoice.Discard);

        Assert.True(await main.ConfirmExitAsync());
        main.PersistSession();

        Assert.Equal(["customers"], Saved().Tabs.Select(t => t.Table));
        Assert.True(script.IsDiscarded);
    }

    // ---- restoring ----

    [Fact]
    public async Task Restore_BringsBackTheTabsInOrder_AndActivatesTheSavedOne_WithoutConnectingTheOthers()
    {
        var first = Saving();
        var customers = await AddTable(first, "customers", activate: false);
        customers.ColumnFilters["name"] = "=Bob";
        customers.RawWhere = "id > 1";
        await customers.ToggleSortAsync("name");
        await customers.ToggleSortAsync("name");   // descending
        await AddTable(first, "orders", activate: false);
        var script = AddScript(first, "select 1");   // the script is the active tab: no connection is needed to show it
        first.PersistSession();

        var main = Restarted();
        Assert.Equal(3, main.RestoreTabs());

        Assert.Equal(["customers", "orders"], main.Tabs.OfType<TableTabViewModel>().Select(t => t.Table.Name));
        Assert.IsType<ScriptTabViewModel>(main.Tabs[2]);
        Assert.Same(main.Tabs[2], main.ActiveTab);
        var restored = (TableTabViewModel)main.Tabs[0];
        Assert.True(restored.IsDeferred);
        Assert.Empty(restored.Rows);
        Assert.Equal("Lite", restored.Connection.Name);
        Assert.Equal("=Bob", restored.GetFilter("name"));
        Assert.Equal("id > 1", restored.RawWhere);
        Assert.Equal(new SortSpec("name", true), restored.Sort);
        Assert.All(main.Tabs.OfType<TableTabViewModel>(), t => Assert.True(t.IsDeferred));
        Assert.Null(main.Tree.FindSession(T.Config.Id));   // nothing connected: not by the restore, not by the inactive tabs
        Assert.Equal(script.Text, ((ScriptTabViewModel)main.Tabs[2]).Text);
    }

    [Fact]
    public async Task Restore_OnlyTheActiveTableLoads_TheOthersLoadWhenFirstSelected()
    {
        var first = Saving();
        await AddTable(first, "customers", activate: false);
        var big = await AddTable(first, "big");
        first.PersistSession();
        Assert.Equal(1, Saved().ActiveIndex);

        var main = Restarted();
        main.RestoreTabs();
        var customers = (TableTabViewModel)main.Tabs[0];
        var active = (TableTabViewModel)main.Tabs[1];
        Assert.Same(active, main.ActiveTab);

        await active.EnsureLoadedAsync();
        Assert.False(active.IsDeferred);
        Assert.Equal(TableTabViewModel.PageSize, active.Rows.Count);
        Assert.Equal(big.Columns.Count, active.Columns.Count);
        Assert.True(customers.IsDeferred);
        Assert.Empty(customers.Rows);

        main.ActiveTab = customers;   // first selected: connects (already connected here) and loads like a normal tab
        await customers.EnsureLoadedAsync();
        Assert.False(customers.IsDeferred);
        Assert.Equal(4, customers.Rows.Count);
        Assert.Null(customers.ErrorMessage);
    }

    [Fact]
    public async Task Restore_FiltersAndSortAreAppliedOnTheFirstLoad()
    {
        var first = Saving();
        var customers = await AddTable(first, "customers");
        customers.ColumnFilters["boss_id"] = "=1";
        await customers.ToggleSortAsync("name");
        await customers.ToggleSortAsync("name");   // name descending
        first.PersistSession();

        var main = Restarted();
        main.RestoreTabs();
        var tab = (TableTabViewModel)main.ActiveTab!;
        await tab.EnsureLoadedAsync();

        Assert.Equal(["Carol", "Bob"], tab.Rows.Select(r => r.Values[tab.FindColumn("name")!.Value]));
        Assert.Equal(new SortSpec("name", true), tab.Sort);
    }

    [Fact]
    public async Task Restore_ATabSwitchedToAnotherGroupMember_ComesBackOnThatMember_AndLoadsFromIt()
    {
        var fileB = T.Fx.CreateSqliteFileWith("b.db",
            "CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, boss_id INTEGER); INSERT INTO customers VALUES (1, 'Zed', NULL), (2, 'Yan', 1);");
        var b = TreeFixture.Conn("LiteB", database: fileB);
        var group = new ConnectionGroup(Guid.NewGuid(), "G", [T.Config.Id, b.Id]);
        AppConfig Config() => new([], [T.Config, b], new()) { Groups = [group] };

        var first = Saving(Config());
        await first.OpenTable(await T.OpenSession(), new TableRef(null, "customers"));
        var tab = (TableTabViewModel)first.ActiveTab!;
        await tab.LoadAsync();
        Assert.True(await tab.SwitchConnectionAsync(b));
        first.PersistSession();
        Assert.Equal(b.Id, Saved().Tabs[0].ConnectionId);   // the connection it is ON, not the one it was opened on

        var main = Restarted(Config());
        main.RestoreTabs();
        var restored = (TableTabViewModel)main.ActiveTab!;
        Assert.Equal("LiteB", restored.Connection.Name);
        Assert.True(restored.HasGroupSwitch);
        await restored.EnsureLoadedAsync();

        Assert.Equal(["Zed", "Yan"], restored.Rows.Select(r => r.Values[restored.FindColumn("name")!.Value]));
        Assert.Equal("LiteB", restored.Connection.Name);
    }

    [Fact]
    public async Task Restore_SkipsATabWhoseConnectionWasDeleted()
    {
        var gone = Guid.NewGuid();
        SessionStore.Save(new SavedTabs(0,
        [
            new SavedTab(SavedTabKind.Table, gone, null, "customers"),
            new SavedTab(SavedTabKind.Table, T.Config.Id, null, "orders"),
            new SavedTab(SavedTabKind.Script, gone, ScriptId: Guid.NewGuid()),
        ]));

        var main = Restarted();
        Assert.Equal(1, main.RestoreTabs());

        var only = Assert.IsType<TableTabViewModel>(Assert.Single(main.Tabs));
        Assert.Equal("orders", only.Table.Name);
        Assert.Same(only, main.ActiveTab);   // the saved active one was skipped: the first tab left is shown
        await only.EnsureLoadedAsync();
        Assert.Equal(3, only.Rows.Count);
        main.PersistSession();
        Assert.Equal(["orders"], Saved().Tabs.Select(t => t.Table));
    }

    [Fact]
    public async Task Restore_AnUnreachableConnection_ShowsTheReloadState_AndNeverThrows()
    {
        var down = TreeFixture.Conn("Down", database: Path.Combine(T.Fx.Dir, "no-such-folder", "x.db"));
        var config = new AppConfig([], [T.Config, down], new());
        SessionStore.Save(new SavedTabs(1,
        [
            new SavedTab(SavedTabKind.Table, T.Config.Id, null, "customers"),
            new SavedTab(SavedTabKind.Table, down.Id, null, "customers"),
        ]));

        var main = Restarted(config);
        main.RestoreTabs();
        var tab = (TableTabViewModel)main.ActiveTab!;
        await tab.EnsureLoadedAsync();

        Assert.Equal("Down", tab.Connection.Name);
        Assert.NotNull(tab.ErrorMessage);
        Assert.True(tab.NeedsReload);
        Assert.False(tab.IsLoading);
        Assert.Empty(tab.Rows);
        var other = (TableTabViewModel)main.Tabs[0];
        Assert.True(other.IsDeferred);   // the failure of one tab does not touch the others
    }

    [Fact]
    public async Task Restore_FirstSelectionOfADeferredTab_StartsItsLoad_ThroughTheShell()
    {
        SessionStore.Save(new SavedTabs(0,
        [
            new SavedTab(SavedTabKind.Table, T.Config.Id, null, "customers"),
            new SavedTab(SavedTabKind.Table, T.Config.Id, null, "orders"),
        ]));
        var main = Restarted();
        main.RestoreTabs();
        var customers = (TableTabViewModel)main.Tabs[0];
        var orders = (TableTabViewModel)main.Tabs[1];
        Assert.False(customers.IsDeferred);   // the active one started loading when it became active
        Assert.True(orders.IsDeferred);

        main.NextTab();   // Ctrl+Tab selects it: no explicit load call
        Assert.Same(orders, main.ActiveTab);
        Assert.False(orders.IsDeferred);

        await orders.EnsureLoadedAsync();
        Assert.Equal(3, orders.Rows.Count);
    }

    [Fact]
    public async Task Restore_ADeferredTab_ApplyBeforeItsFirstLoad_LoadsItInsteadOfFailing()
    {
        SessionStore.Save(new SavedTabs(1,
        [
            new SavedTab(SavedTabKind.Table, T.Config.Id, null, "customers"),
            new SavedTab(SavedTabKind.Table, T.Config.Id, null, "orders"),
        ]));
        var main = Restarted();
        main.RestoreTabs();
        var inactive = (TableTabViewModel)main.Tabs[0];
        var active = (TableTabViewModel)main.Tabs[1];

        await inactive.ApplyFiltersAsync();   // not shown, but reachable from code (F5 / Apply)
        Assert.Null(inactive.ErrorMessage);
        Assert.Equal(4, inactive.Rows.Count);
        Assert.False(inactive.IsDeferred);

        await active.ApplyFiltersAsync();     // while its first load is still connecting
        Assert.Null(active.ErrorMessage);
        Assert.Equal(3, active.Rows.Count);
    }

    // ---- scripts ----

    [Fact]
    public async Task RestoreScripts_UnsavedText_FromTheStore_CleanFile_FromTheFile_NewEmpty_Empty()
    {
        var file = Path.Combine(T.Fx.Dir, "clean.sql");
        File.WriteAllText(file, "select 'from file'");
        var first = Saving();
        var dirty = AddScript(first, "select 'unsaved'");                                 // autosave copy in the store
        var clean = AddScript(first, "", persisted: false);
        await clean.OpenAsync(file);                                                     // equal to its file: nothing in the store
        var empty = first.NewScript()!;
        first.PersistSession();
        Assert.Single(Store.LoadAll());

        var main = Restarted();
        Assert.Equal(3, main.RestoreTabs());

        var scripts = main.Tabs.OfType<ScriptTabViewModel>().ToList();
        Assert.Equal([dirty.Id, clean.Id, empty.Id], scripts.Select(s => s.Id));
        Assert.Equal(("select 'unsaved'", true, null), (scripts[0].Text, scripts[0].IsDirty, scripts[0].FilePath));
        Assert.Equal(("select 'from file'", false, file), (scripts[1].Text, scripts[1].IsDirty, scripts[1].FilePath));
        Assert.Equal(("", false, null), (scripts[2].Text, scripts[2].IsDirty, scripts[2].FilePath));
        Assert.Equal("Lite", scripts[0].Connection.Name);
        Assert.Null(main.Tree.FindSession(T.Config.Id));   // a restored script never connects before it runs
    }

    [Fact]
    public void RestoreScripts_ACleanScriptWhoseFileIsGone_ComesBackAsAnEmptyNewScript()
    {
        SessionStore.Save(new SavedTabs(0,
            [new SavedTab(SavedTabKind.Script, T.Config.Id, ScriptId: Guid.NewGuid(), FilePath: Path.Combine(T.Fx.Dir, "deleted.sql"))]));

        var main = Restarted();
        main.RestoreTabs();

        var script = Assert.IsType<ScriptTabViewModel>(Assert.Single(main.Tabs));
        Assert.Equal(("", false, null), (script.Text, script.IsDirty, script.FilePath));
    }

    [Fact]
    public void RestoreScripts_FollowsTheConnectionTheScriptWasSwitchedTo()
    {
        var other = TreeFixture.Conn("Other");
        var config = new AppConfig([], [T.Config, other], new());
        SessionStore.Save(new SavedTabs(0, [new SavedTab(SavedTabKind.Script, other.Id, ScriptId: Guid.NewGuid())]));

        var main = Restarted(config);
        main.RestoreTabs();

        Assert.Equal("Other", main.Tabs[0].Connection.Name);
    }

    [Fact]
    public void Restore_WithASession_DoesNotDuplicateScripts_AndKeepsOrphanedUnsavedCopies()
    {
        var first = Saving();
        var inSession = AddScript(first, "in session");
        first.PersistSession();
        var orphan = Guid.NewGuid();   // an autosave copy written after the last session save (e.g. a crash)
        Store.Save(orphan, T.Config.Id, "orphan text", null);

        var main = Restarted();
        Assert.Equal(2, main.RestoreTabs());

        Assert.Equal([inSession.Id, orphan], main.Tabs.OfType<ScriptTabViewModel>().Select(s => s.Id));
        Assert.Same(main.Tabs[0], main.ActiveTab);
    }

    [Fact]
    public void Restore_WithoutASessionFile_BehavesAsBefore_RestoresTheUnsavedScripts()
    {
        var first = Saving();
        var script = AddScript(first, "unsaved from the last run");   // session.json is never written: the app "crashed"
        Assert.False(File.Exists(Path.Combine(T.Fx.Dir, "session.json")));

        var main = Restarted();
        Assert.Equal(1, main.RestoreTabs());

        Assert.Equal(script.Id, Assert.Single(main.Tabs.OfType<ScriptTabViewModel>()).Id);
        Assert.NotNull(main.ActiveTab);
        main.PersistSession();
        Assert.Single(Saved().Tabs);   // and from now on the session is kept
    }

    [Fact]
    public void Restore_AnEmptySession_RestoresNoTabs()
    {
        SessionStore.Save(new SavedTabs(-1, []));

        var main = Restarted();

        Assert.Equal(0, main.RestoreTabs());
        Assert.Empty(main.Tabs);
        Assert.Null(main.ActiveTab);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "timed out");
    }
}
