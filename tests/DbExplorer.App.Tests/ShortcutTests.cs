using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App.Tests;

/// <summary>Ctrl+L (column jump) and the window-level shortcuts: Ctrl+W, Ctrl+Tab, Ctrl+Shift+Tab, F5.</summary>
public class ShortcutTests : ShellTestBase
{
    private static async Task Pump(int rounds = 10)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
    }

    // ---- Ctrl+L ----

    [Fact]
    public async Task JumpToColumn_OnATableTab_ListsItsColumns_AndChoosingOneRequestsTheFocus()
    {
        var main = NewMain();
        var tab = await AddTable(main, "orders");
        var focused = new List<int>();
        tab.FocusColumnRequested += focused.Add;

        main.JumpToColumnCommand.Execute(null);

        var picker = Assert.IsType<QuickOpenViewModel>(main.QuickOpen);
        Assert.Equal("Go to column", picker.Title);
        picker.Query = "note";
        var item = picker.Results[0];
        Assert.Equal("note", item.Label);
        await picker.ConfirmAsync(item);

        Assert.Equal([tab.FindColumn("note")!.Value], focused);
        Assert.Null(main.QuickOpen);
    }

    [Fact]
    public void JumpToColumn_WithoutATableTab_OnlySetsAStatusMessage()
    {
        var main = NewMain();
        main.JumpToColumnCommand.Execute(null);
        Assert.Null(main.QuickOpen);
        Assert.Contains("Ctrl+L", T.Fx.Status.Message);

        T.Fx.Status.Info("");
        AddScript(main, "SELECT 1");   // a script is active: no columns either
        main.JumpToColumnCommand.Execute(null);
        Assert.Null(main.QuickOpen);
        Assert.Contains("Ctrl+L", T.Fx.Status.Message);
    }

    [AvaloniaFact]
    public async Task ResultGrid_ScrollToColumn_WhileDetached_DoesNothing_AndIsAppliedWhenItIsShown()
    {
        var tab = await T.OpenTab("orders");
        var grid = new ResultGrid { Columns = tab.GridColumns, ItemsSource = tab.Rows };

        grid.ScrollToColumn(3);   // not in the visual tree (e.g. the Structure sub-tab is selected): must not throw or act

        var window = new Window { Content = grid };
        window.Show();
        await Pump(5);   // the pending request is posted after the first layout
        var dataGrid = grid.GetVisualDescendants().OfType<DataGrid>().First();
        Assert.Same(dataGrid.Columns[3], dataGrid.CurrentColumn);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ColumnJump_FromTheStructureSubTab_SwitchesBackToData_AndFocusesTheColumnFilter()
    {
        var tab = await T.OpenTab("orders");
        var view = new TableTabView { DataContext = tab };
        var window = new Window { Content = view, Width = 1400, Height = 700 };
        window.Show();
        await Pump();
        var sub = view.FindControl<TabControl>("SubTabs")!;
        sub.SelectedIndex = 1;   // Structure: the data grid leaves the visual tree
        await Pump();

        var note = tab.FindColumn("note")!.Value;
        tab.RequestFocusColumn(note);
        await Pump(20);

        Assert.Equal(0, sub.SelectedIndex);
        var filters = view.GetVisualDescendants().OfType<TextBox>().Where(t => Avalonia.Automation.AutomationProperties.GetName(t)?.StartsWith("Filter ") == true).ToList();
        Assert.Equal(tab.Columns.Count, filters.Count);
        Assert.True(filters[note].IsFocused, "the filter box of the requested column should have the focus");
        window.Close();
    }

    // ---- Ctrl+W, Ctrl+Tab, F5 ----

    [Fact]
    public async Task CtrlW_ClosesTheActiveTab_ThroughThePromptPath()
    {
        var main = NewMain();
        var clean = await AddTable(main, "orders", activate: false);
        var dirty = await AddTable(main, "customers");
        dirty.SetCell(0, dirty.FindColumn("name")!.Value, "Changed");
        Dialogs.Choice = SaveChoice.Cancel;

        Assert.True(MainShortcuts.Handle(main, Key.W, KeyModifiers.Control));
        Assert.Equal(1, Dialogs.AskCount);
        Assert.Equal(2, main.Tabs.Count);   // Cancel keeps it

        Dialogs.Choice = SaveChoice.Discard;
        Assert.True(MainShortcuts.Handle(main, Key.W, KeyModifiers.Control));
        Assert.Equal(2, Dialogs.AskCount);
        Assert.Same(clean, Assert.Single(main.Tabs));
        Assert.Same(clean, main.ActiveTab);

        Assert.True(MainShortcuts.Handle(main, Key.W, KeyModifiers.Control));   // a clean tab closes silently
        Assert.Equal(2, Dialogs.AskCount);
        Assert.Empty(main.Tabs);
        Assert.Null(main.ActiveTab);
        Assert.False(MainShortcuts.Handle(main, Key.W, KeyModifiers.Control));   // nothing left to close
    }

    [Fact]
    public async Task CtrlW_WhileAnotherCloseIsBeingAsked_IsIgnored()
    {
        var main = NewMain();
        var dirty = await AddTable(main, "customers");
        dirty.SetCell(0, dirty.FindColumn("name")!.Value, "Changed");
        Dialogs.AskGate = new TaskCompletionSource();
        Dialogs.Choice = SaveChoice.Cancel;

        var first = main.CloseActiveTabCommand.ExecuteAsync(null);
        await main.CloseActiveTabCommand.ExecuteAsync(null);
        main.CloseTabCommand.Execute(dirty);
        Assert.Equal(1, Dialogs.AskCount);   // no second prompt stacked on the first

        Dialogs.AskGate.SetResult();
        await first;
        Assert.Single(main.Tabs);
    }

    [Fact]
    public async Task CloseTab_WhenThePromptThrows_KeepsTheTab_AndReportsInsteadOfThrowing()
    {
        var main = NewMain();
        var dirty = await AddTable(main, "customers");
        dirty.SetCell(0, dirty.FindColumn("name")!.Value, "Changed");
        Dialogs.ThrowOnAsk = true;

        await main.CloseActiveTabCommand.ExecuteAsync(null);

        Assert.Single(main.Tabs);
        Assert.True(T.Fx.Status.IsError);
        Assert.Contains("Cannot close", T.Fx.Status.Message);
        Dialogs.ThrowOnAsk = false;
        Dialogs.Choice = SaveChoice.Discard;
        await main.CloseActiveTabCommand.ExecuteAsync(null);   // not stuck
        Assert.Empty(main.Tabs);
    }

    [Fact]
    public async Task CtrlTab_CyclesForwardAndBackward_WrappingAround()
    {
        var main = NewMain();
        var a = await AddTable(main, "orders");
        var b = await AddTable(main, "customers");
        var c = await AddTable(main, "big");
        main.ActiveTab = a;

        Assert.True(MainShortcuts.Handle(main, Key.Tab, KeyModifiers.Control));
        Assert.Same(b, main.ActiveTab);
        MainShortcuts.Handle(main, Key.Tab, KeyModifiers.Control);
        Assert.Same(c, main.ActiveTab);
        MainShortcuts.Handle(main, Key.Tab, KeyModifiers.Control);
        Assert.Same(a, main.ActiveTab);   // wrapped

        Assert.True(MainShortcuts.Handle(main, Key.Tab, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Same(c, main.ActiveTab);   // wrapped backwards
        MainShortcuts.Handle(main, Key.Tab, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.Same(b, main.ActiveTab);
    }

    [Fact]
    public void CtrlTab_WithNoTabs_IsNotHandled_AndWithNoActiveTabPicksAnEnd()
    {
        var main = NewMain();
        Assert.False(MainShortcuts.Handle(main, Key.Tab, KeyModifiers.Control));

        var first = AddScript(main, "a", persisted: false);
        var second = AddScript(main, "b", persisted: false);
        main.ActiveTab = null;
        main.NextTabCommand.Execute(null);
        Assert.Same(first, main.ActiveTab);
        main.ActiveTab = null;
        main.PreviousTabCommand.Execute(null);
        Assert.Same(second, main.ActiveTab);
    }

    [Fact]
    public async Task F5_InATableTab_RefreshesThePage_AndKeepsThePendingEditsPrompt()
    {
        var main = NewMain();
        var tab = await AddTable(main, "customers");
        T.Log.Clear();

        await main.RefreshActiveTableCommand.ExecuteAsync(null);
        Assert.NotEmpty(T.Log.Items);   // the page was queried again

        tab.SetCell(0, tab.FindColumn("name")!.Value, "Changed");
        Dialogs.Choice = SaveChoice.Cancel;
        T.Log.Clear();
        Assert.True(MainShortcuts.Handle(main, Key.F5, KeyModifiers.None));
        Assert.Equal(1, Dialogs.AskCount);   // the existing prompt, not a silent reload
        Assert.True(tab.HasPendingEdits);
        Assert.Empty(T.Log.Items);
    }

    [Fact]
    public void F5_InAScriptTab_IsLeftToTheEditor()
    {
        var main = NewMain();
        AddScript(main, "SELECT 1", persisted: false);

        Assert.False(MainShortcuts.Handle(main, Key.F5, KeyModifiers.None));   // the script view runs the script on it
        Assert.False(MainShortcuts.Handle(new MainViewModel(main.Tree, T.Fx.Status), Key.F5, KeyModifiers.None));   // no tab at all
    }

    [Fact]
    public async Task Shortcuts_AreIgnoredWhileTheQuickOpenOverlayIsShowing()
    {
        var main = NewMain();
        var tab = await AddTable(main, "orders");
        main.JumpToColumnCommand.Execute(null);
        Assert.NotNull(main.QuickOpen);

        Assert.False(MainShortcuts.Handle(main, Key.W, KeyModifiers.Control));
        Assert.False(MainShortcuts.Handle(main, Key.Tab, KeyModifiers.Control));
        Assert.False(MainShortcuts.Handle(main, Key.F5, KeyModifiers.None));
        Assert.Same(tab, Assert.Single(main.Tabs));
    }

    [Theory]
    [InlineData(Key.W, KeyModifiers.None)]
    [InlineData(Key.W, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(Key.W, KeyModifiers.Alt)]
    [InlineData(Key.Tab, KeyModifiers.None)]            // typing a Tab in an editor
    [InlineData(Key.Tab, KeyModifiers.Shift)]
    [InlineData(Key.F5, KeyModifiers.Control)]
    [InlineData(Key.A, KeyModifiers.None)]
    [InlineData(Key.L, KeyModifiers.None)]
    [InlineData(Key.T, KeyModifiers.Shift)]
    public async Task OrdinaryTypingIsNeverHandled(Key key, KeyModifiers modifiers)
    {
        var main = NewMain();
        await AddTable(main, "orders");
        Assert.False(MainShortcuts.Handle(main, key, modifiers));
        Assert.Single(main.Tabs);
    }

    [Fact]
    public void ExistingShortcuts_StillMapToTheirCommands()
    {
        var main = NewMain();
        Assert.True(MainShortcuts.Handle(main, Key.N, KeyModifiers.Control));   // new script on the selected connection
        Assert.IsType<ScriptTabViewModel>(main.ActiveTab);
        Assert.True(MainShortcuts.Handle(main, Key.L, KeyModifiers.Control));
        Assert.True(MainShortcuts.Handle(main, Key.T, KeyModifiers.Control));
        Assert.True(MainShortcuts.Handle(main, Key.T, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.True(MainShortcuts.Handle(main, Key.O, KeyModifiers.Control));
    }

    // ---- an open cell editor is committed first ----

    [Fact]
    public async Task ChordsThatLeaveTheTab_CommitTheOpenCellEditFirst_AndAnInvalidTextSwallowsThem()
    {
        var main = NewMain();
        var other = await AddTable(main, "orders", activate: false);
        var tab = await AddTable(main, "customers");
        var commits = 0;
        var accepted = false;
        bool Commit()
        {
            commits++;
            return accepted;
        }
        T.Log.Clear();

        // the typed text is refused: nothing happens, the chord is swallowed (handled) so the editor keeps its key
        Assert.True(MainShortcuts.Handle(main, Key.W, KeyModifiers.Control, Commit));
        Assert.True(MainShortcuts.Handle(main, Key.Tab, KeyModifiers.Control, Commit));
        Assert.True(MainShortcuts.Handle(main, Key.Tab, KeyModifiers.Control | KeyModifiers.Shift, Commit));
        Assert.True(MainShortcuts.Handle(main, Key.F5, KeyModifiers.None, Commit));
        Assert.Equal(4, commits);
        Assert.Equal(2, main.Tabs.Count);
        Assert.Same(tab, main.ActiveTab);
        Assert.Empty(T.Log.Items);
        Assert.Equal(0, Dialogs.AskCount);

        // accepted: the edit is staged by the commit, so the close now sees it and asks
        bool CommitAndStage()
        {
            commits++;
            return tab.SetCell(0, tab.FindColumn("name")!.Value, "Typed");
        }
        Dialogs.Choice = SaveChoice.Cancel;
        Assert.True(MainShortcuts.Handle(main, Key.W, KeyModifiers.Control, CommitAndStage));
        Assert.Equal(5, commits);
        Assert.Equal(1, Dialogs.AskCount);
        Assert.Equal(2, main.Tabs.Count);
        Assert.True(tab.HasPendingEdits);

        // chords that do not leave the tab never touch the editor
        MainShortcuts.Handle(main, Key.L, KeyModifiers.Control, Commit);
        MainShortcuts.Handle(main, Key.T, KeyModifiers.Control, Commit);
        Assert.Equal(5, commits);
        Assert.NotNull(other);
    }
}
