using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.Tests;

/// <summary>
/// Closing a script tab asks about unsaved text; closing the window does not (the autosave brings the script back)
/// unless the autosave cannot be relied on. Also the prompts' wording/activation, Reload during a save and the
/// abandoned connect.
/// </summary>
public class ScriptCloseTests : ShellTestBase
{
    // ---- closing one script tab ----

    [Fact]
    public async Task CloseDirtyScript_Discard_ClosesAndDeletesTheAutosave()
    {
        var main = NewMain();
        AddScript(main, "SELECT 1");
        Assert.Single(Store.LoadAll());
        Dialogs.Choice = SaveChoice.Discard;

        await main.CloseActiveTabCommand.ExecuteAsync(null);

        Assert.Equal(1, Dialogs.AskCount);
        Assert.Empty(main.Tabs);
        Assert.Empty(Store.LoadAll());
    }

    [Fact]
    public async Task CloseDirtyScript_Cancel_KeepsTheTabAndItsText()
    {
        var main = NewMain();
        var script = AddScript(main, "SELECT 1");
        Dialogs.Choice = SaveChoice.Cancel;

        await main.CloseActiveTabCommand.ExecuteAsync(null);

        Assert.Same(script, Assert.Single(main.Tabs));
        Assert.Equal("SELECT 1", script.Text);
        Assert.Single(Store.LoadAll());   // still autosaved
    }

    [Fact]
    public async Task CloseDirtyScript_Save_WithoutAFile_AsksWhere_WritesItAndCloses()
    {
        var main = NewMain();
        AddScript(main, "SELECT 42");
        var path = Path.Combine(T.Fx.Dir, "saved.sql");
        Files.SavePath = path;
        Dialogs.Choice = SaveChoice.Save;

        await main.CloseActiveTabCommand.ExecuteAsync(null);

        Assert.Equal("SELECT 42", await File.ReadAllTextAsync(path));
        Assert.Empty(main.Tabs);
        Assert.Empty(Store.LoadAll());
    }

    [Fact]
    public async Task CloseDirtyScript_Save_CancellingTheFileDialog_CountsAsCancel()
    {
        var main = NewMain();
        var script = AddScript(main, "SELECT 42");
        Files.SavePath = null;   // the picker is cancelled
        Dialogs.Choice = SaveChoice.Save;

        await main.CloseActiveTabCommand.ExecuteAsync(null);

        Assert.Same(script, Assert.Single(main.Tabs));
        Assert.True(script.IsDirty);
        Assert.Equal("SELECT 42", Assert.Single(Store.LoadAll()).Text);
    }

    [Fact]
    public async Task CloseDirtyScript_Save_UnwritablePath_CountsAsCancel()
    {
        var main = NewMain();
        var script = AddScript(main, "SELECT 42");
        Files.SavePath = Path.Combine(T.Fx.Dir, "no-such-folder", "x.sql");
        Dialogs.Choice = SaveChoice.Save;

        await main.CloseActiveTabCommand.ExecuteAsync(null);

        Assert.Same(script, Assert.Single(main.Tabs));
        Assert.True(T.Fx.Status.IsError);
    }

    [Fact]
    public async Task CloseDirtyScript_OfAFile_Save_WritesBackToTheFile()
    {
        var main = NewMain();
        var path = Path.Combine(T.Fx.Dir, "orig.sql");
        await File.WriteAllTextAsync(path, "SELECT 1");
        Files.OpenPath = path;
        await main.OpenScriptFileAsync();
        var script = Assert.IsType<ScriptTabViewModel>(main.ActiveTab);
        Assert.False(script.NeedsCloseConfirmation);   // as loaded: nothing to ask
        script.Text = "SELECT 2";
        Dialogs.Choice = SaveChoice.Save;

        await main.CloseActiveTabCommand.ExecuteAsync(null);

        Assert.Empty(Files.SuggestedNames);   // no picker: it has a file
        Assert.Equal("SELECT 2", await File.ReadAllTextAsync(path));
        Assert.Empty(main.Tabs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n\t ")]
    public async Task CloseScript_NeverSavedAndBlank_ClosesWithoutAsking(string text)
    {
        var main = NewMain();
        AddScript(main, text, persisted: false);

        await main.CloseActiveTabCommand.ExecuteAsync(null);

        Assert.Equal(0, Dialogs.AskCount);
        Assert.Empty(main.Tabs);
    }

    [Fact]
    public async Task CloseScript_EqualToItsFile_ClosesWithoutAsking()
    {
        var main = NewMain();
        var path = Path.Combine(T.Fx.Dir, "same.sql");
        await File.WriteAllTextAsync(path, "SELECT 1");
        Files.OpenPath = path;
        await main.OpenScriptFileAsync();
        var script = Assert.IsType<ScriptTabViewModel>(main.ActiveTab);
        script.Text = "SELECT 1 -- x";
        script.Text = "SELECT 1";   // edited and put back

        await main.CloseActiveTabCommand.ExecuteAsync(null);

        Assert.Equal(0, Dialogs.AskCount);
        Assert.Empty(main.Tabs);
    }

    [Fact]
    public async Task CloseDirtyScript_WithoutDialogs_RefusesAndSaysSo()
    {
        var main = NewMain(scriptDialogs: false);
        var script = AddScript(main, "SELECT 1");

        await main.CloseActiveTabCommand.ExecuteAsync(null);

        Assert.Same(script, Assert.Single(main.Tabs));   // never lose the text without asking
        Assert.Contains("unsaved", T.Fx.Status.Message);
        Assert.Single(Store.LoadAll());
    }

    [Fact]
    public async Task DiscardedScript_IsNotRewrittenByATabSwitchOrTheAutosaveTimer()
    {
        var main = NewMain();
        var table = await AddTable(main, "customers");
        var script = AddScript(main, "SELECT 1");
        Dialogs.Choice = SaveChoice.Discard;
        Assert.True(await script.ConfirmCloseAsync());
        Assert.Empty(Store.LoadAll());

        main.ActiveTab = table;   // leaving a script flushes it
        script.FlushPersist();

        Assert.Empty(Store.LoadAll());

        script.Text = "SELECT 1 -- edited after all";   // the user kept working in it: autosave is back
        script.FlushPersist();
        Assert.Single(Store.LoadAll());
    }

    // ---- closing the window: scripts are kept by the autosave, not asked about ----

    [Fact]
    public async Task ConfirmExit_DirtyScripts_DoNotPrompt_AndTheirAutosaveStaysOnDisk()
    {
        var main = NewMain();
        var table = await AddTable(main, "orders", activate: false);
        AddScript(main, "SELECT 1", persisted: false);
        AddScript(main, "SELECT 2", persisted: false);
        main.ActiveTab = table;

        var asking = main.ConfirmExitAsync();

        Assert.True(asking.IsCompletedSuccessfully);   // synchronous: the window closes without being cancelled and re-closed
        Assert.True(asking.Result);
        Assert.Equal(0, Dialogs.AskCount);
        Assert.Same(table, main.ActiveTab);   // scripts are not brought to the front
        Assert.Equal(["SELECT 1", "SELECT 2"], Store.LoadAll().Select(s => s.Text).Order().ToArray());

        main.PersistScripts();   // what the window does once the close is confirmed
        Assert.Equal(2, Store.LoadAll().Count);
    }

    [Fact]
    public async Task ConfirmExit_TableTabsStillPrompt_WhileScriptsDoNot()
    {
        var main = NewMain();
        var dirtyTable = await AddTable(main, "customers");
        dirtyTable.SetCell(0, dirtyTable.FindColumn("name")!.Value, "Changed");
        AddScript(main, "SELECT 1", persisted: false);
        Dialogs.Choice = SaveChoice.Cancel;

        Assert.False(await main.ConfirmExitAsync());

        Assert.Equal(1, Dialogs.AskCount);
        Assert.Contains("customers", Dialogs.LastAskMessage);
    }

    [Fact]
    public async Task ConfirmExit_DirtyScript_WithoutAStore_StillPrompts_ShowingTheScript()
    {
        var main = NewMain(useStore: false);
        var script = AddScript(main, "SELECT 1");
        var table = await AddTable(main, "orders");   // in front
        TabViewModel? shown = null;
        Dialogs.OnAsk = () => shown = main.ActiveTab;
        Dialogs.Choice = SaveChoice.Cancel;

        Assert.False(await main.ConfirmExitAsync());

        Assert.Same(script, shown);
        Assert.Contains(T.Config.Name, Dialogs.LastAskMessage);
        Assert.NotSame(table, main.ActiveTab);

        Dialogs.Choice = SaveChoice.Discard;
        Assert.True(await main.ConfirmExitAsync());
    }

    private string BrokenStoreDir()
    {
        var path = Path.Combine(T.Fx.Dir, "not-a-folder");
        File.WriteAllText(path, "a file where the store folder should be: every write fails");
        return path;
    }

    [Fact]
    public async Task ConfirmExit_DirtyScript_WhoseAutosaveCannotBeWritten_StillPrompts_AndSaveWritesTheFile()
    {
        var main = NewMain(store: new ScriptSessionStore(BrokenStoreDir()));
        var script = AddScript(main, "SELECT 1");   // the write fails and is remembered
        Dialogs.Choice = SaveChoice.Cancel;

        Assert.False(await main.ConfirmExitAsync());
        Assert.Equal(1, Dialogs.AskCount);
        Assert.Same(script, Assert.Single(main.Tabs));

        var path = Path.Combine(T.Fx.Dir, "kept.sql");
        Files.SavePath = path;
        Dialogs.Choice = SaveChoice.Save;
        Assert.True(await main.ConfirmExitAsync());
        Assert.Equal("SELECT 1", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ConfirmExit_DiscardIsNotResurrected_ByTheAutosaveWrittenAtTheEnd()
    {
        var broken = BrokenStoreDir();
        var main = NewMain(store: new ScriptSessionStore(broken));
        AddScript(main, "SELECT 1");
        AddScript(main, "SELECT 2");
        Dialogs.Choice = SaveChoice.Discard;

        Assert.True(await main.ConfirmExitAsync());
        File.Delete(broken);   // the store works again by the time the window really closes
        main.PersistScripts();

        Assert.Empty(new ScriptSessionStore(broken).LoadAll());
    }

    [Fact]
    public async Task ConfirmExit_CancelOnALaterScript_PutsAnEarlierDiscardBack()
    {
        var broken = BrokenStoreDir();
        var main = NewMain(store: new ScriptSessionStore(broken));
        AddScript(main, "SELECT 1");
        AddScript(main, "SELECT 2");
        Dialogs.Choices.Enqueue(SaveChoice.Discard);   // first script
        Dialogs.Choices.Enqueue(SaveChoice.Cancel);    // second script: the window stays open

        Assert.False(await main.ConfirmExitAsync());
        File.Delete(broken);
        main.PersistScripts();

        // the first script was discarded only as part of a close that did not happen: it is an ordinary open tab again
        Assert.Equal(["SELECT 1", "SELECT 2"], new ScriptSessionStore(broken).LoadAll().Select(s => s.Text).Order().ToArray());
    }

    [AvaloniaFact]
    public void MainWindow_Close_WithADirtyScript_ClosesWithoutAsking_AndLeavesTheAutosave()
    {
        var main = NewMain();
        AddScript(main, "SELECT 1", persisted: false);
        var window = new MainWindow { DataContext = main };
        window.Show();

        window.Close();

        Assert.False(window.IsVisible);
        Assert.Equal(0, Dialogs.AskCount);
        Assert.Equal("SELECT 1", Assert.Single(Store.LoadAll()).Text);   // OnClosed persisted it
    }

    [AvaloniaFact]
    public async Task MainWindow_Close_WithADirtyScriptAndNoAutosave_AsksFirst_SaveWritesTheFileThenCloses()
    {
        var main = NewMain(useStore: false);
        var script = AddScript(main, "SELECT 1", persisted: false);
        Dialogs.Choice = SaveChoice.Cancel;
        var window = new MainWindow { DataContext = main };
        window.Show();

        window.Close();
        Assert.True(window.IsVisible);   // Cancel: stays open
        Assert.Equal(1, Dialogs.AskCount);

        Dialogs.Choice = SaveChoice.Save;
        Files.SavePath = Path.Combine(T.Fx.Dir, "w.sql");
        window.Close();
        for (var i = 0; i < 100 && window.IsVisible; i++)   // the file write is asynchronous: the close finishes after it
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Assert.False(window.IsVisible);
        Assert.False(script.IsDirty);
        Assert.Equal("SELECT 1", await File.ReadAllTextAsync(Path.Combine(T.Fx.Dir, "w.sql")));
        Assert.False(File.Exists(Path.Combine(T.Fx.Dir, "window.json")));   // no state file given: nothing written
    }

    // ---- the prompts say which tab they are about ----

    [Fact]
    public async Task ConfirmExit_ActivatesTheTableTabItAsksAbout_AndNamesTheConnection()
    {
        var main = NewMain();
        var clean = await AddTable(main, "orders", activate: false);
        var dirty = await AddTable(main, "customers", activate: false);
        main.ActiveTab = clean;
        dirty.SetCell(0, dirty.FindColumn("name")!.Value, "Changed");
        TabViewModel? shown = null;
        Dialogs.OnAsk = () => shown = main.ActiveTab;
        Dialogs.Choice = SaveChoice.Cancel;

        Assert.False(await main.ConfirmExitAsync());

        Assert.Same(dirty, shown);
        Assert.Same(dirty, main.ActiveTab);   // and it stays in front after Cancel
        Assert.Contains(T.Config.Name, Dialogs.LastAskMessage);
        Assert.Contains("customers", Dialogs.LastAskMessage);
    }

    [Fact]
    public async Task ConfirmExit_WithNothingToAsk_LeavesTheActiveTabAlone()
    {
        var main = NewMain();
        var first = await AddTable(main, "orders", activate: false);
        var second = await AddTable(main, "customers", activate: false);
        main.ActiveTab = first;

        var asking = main.ConfirmExitAsync();

        Assert.True(asking.IsCompletedSuccessfully);
        Assert.True(asking.Result);
        Assert.Same(first, main.ActiveTab);
        Assert.NotSame(second, main.ActiveTab);
    }

    [Fact]
    public async Task ClosingAnInactiveDirtyTab_ShowsItBeforeAsking()
    {
        var main = NewMain();
        var dirty = await AddTable(main, "customers", activate: false);
        var other = await AddTable(main, "orders");
        dirty.SetCell(0, dirty.FindColumn("name")!.Value, "Changed");
        TabViewModel? shown = null;
        Dialogs.OnAsk = () => shown = main.ActiveTab;
        Dialogs.Choice = SaveChoice.Cancel;
        Assert.Same(other, main.ActiveTab);

        await main.CloseTabCommand.ExecuteAsync(dirty);   // the x button of a tab that is not in front

        Assert.Same(dirty, shown);
    }

    [Fact]
    public async Task ScriptClosePrompt_ActivatesTheScript_AndNamesTheConnection()
    {
        var main = NewMain();
        var script = AddScript(main, "SELECT 1");
        await AddTable(main, "orders");   // in front now
        TabViewModel? shown = null;
        Dialogs.OnAsk = () => shown = main.ActiveTab;
        Dialogs.Choice = SaveChoice.Cancel;

        await main.CloseTabCommand.ExecuteAsync(script);   // x button of the script tab, which is not in front

        Assert.Same(script, shown);
        Assert.Same(script, main.ActiveTab);
        Assert.Contains(T.Config.Name, Dialogs.LastAskMessage);
        Assert.Contains("Script 1", Dialogs.LastAskMessage);
    }

    // ---- Reload while a save is running ----

    [Fact]
    public async Task Reload_WhileASaveIsInFlight_DoesNotAskAndDoesNotTouchTheEdits()
    {
        var main = NewMain();
        var tab = await AddTable(main, "customers");
        Assert.True(tab.SetCell(0, tab.FindColumn("name")!.Value, "Changed"));
        tab.IsSaving = true;
        Dialogs.Choice = SaveChoice.Discard;
        T.Log.Clear();

        await tab.ReloadAsync();

        Assert.Equal(0, Dialogs.AskCount);
        Assert.True(tab.HasPendingEdits);
        Assert.Empty(T.Log.Items);   // no query ran underneath the save
    }

    // ---- an abandoned connect that fails later ----

    [Fact]
    public async Task CancellingARunWhileConnecting_ObservesTheConnectThatFailsAfterwards()
    {
        var marker = "abandoned-connect-" + Guid.NewGuid().ToString("N");
        var unobserved = new List<string>();
        void Handler(object? sender, UnobservedTaskExceptionEventArgs e) => unobserved.AddRange(e.Exception.InnerExceptions.Select(x => x.Message));
        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            await CancelThenFail(marker);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.DoesNotContain(marker, unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    private async Task CancelThenFail(string message)
    {
        var connect = new TaskCompletionSource<Session?>();
        var hooks = new ScriptTabHooks(_ => connect.Task, null, null, T.Fx.Status);
        var tab = new ScriptTabViewModel(T.Config, null, hooks, 1) { Text = "SELECT 1" };

        var run = tab.RunScriptAsync();
        Assert.True(tab.IsRunning);   // waiting for the connection
        tab.Cancel();
        await run;
        Assert.False(tab.IsRunning);

        connect.SetException(new InvalidOperationException(message));   // the abandoned connect fails after the fact
        await Task.Delay(50);
    }
}
