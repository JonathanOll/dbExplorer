using Avalonia.Input;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Metadata;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests;

/// <summary>Ctrl+Shift+T (reopen closed tabs, oldest first), the Ctrl+T text memory and the typo-tolerant search.</summary>
public class ReopenAndQuickOpenMemoryTests : ShellTestBase
{
    private MainViewModel NewTableMain()
    {
        var main = NewMain();
        main.UseTableTabs(T.Fx.Sessions, Dialogs);
        Dialogs.Choice = SaveChoice.Discard;   // closing a script with unsaved text asks first
        return main;
    }

    private async Task<TableTabViewModel> OpenAsync(MainViewModel main, string table)
    {
        // Through the tree's own session manager, so the tree (Ctrl+T, reopen) sees the connection as open.
        var session = await T.Fx.Sessions.GetOrOpenAsync(T.Config, CancellationToken.None);
        await main.OpenTable(session, new TableRef(null, table));
        var tab = (TableTabViewModel)main.ActiveTab!;
        await tab.LoadAsync();
        return tab;
    }

    // ---- reopen closed tabs ----

    [Fact]
    public async Task Reopen_GivesTheOldestClosedTabFirst_ThenTheNext()
    {
        var main = NewTableMain();
        var orders = await OpenAsync(main, "orders");
        var customers = await OpenAsync(main, "customers");
        await main.CloseTabCommand.ExecuteAsync(orders);
        await main.CloseTabCommand.ExecuteAsync(customers);
        Assert.Empty(main.Tabs);
        Assert.Equal(2, main.ClosedTabCount);

        await main.ReopenClosedTabAsync();
        Assert.Equal("orders", Assert.IsType<TableTabViewModel>(main.ActiveTab).Table.Name);   // first closed, first back
        await main.ReopenClosedTabAsync();
        Assert.Equal("customers", Assert.IsType<TableTabViewModel>(main.ActiveTab).Table.Name);
        Assert.Equal(2, main.Tabs.Count);
        Assert.Equal(0, main.ClosedTabCount);
    }

    [Fact]
    public async Task Reopen_KeepsTheColumnFiltersOfATable()
    {
        var main = NewTableMain();
        var orders = await OpenAsync(main, "orders");
        var column = orders.Columns[1].Name;
        orders.SetFilter(column, "abc");
        await main.CloseTabCommand.ExecuteAsync(orders);

        await main.ReopenClosedTabAsync();

        var back = Assert.IsType<TableTabViewModel>(main.ActiveTab);
        Assert.Equal("abc", back.GetFilter(column));
    }

    [Fact]
    public async Task Reopen_BringsBackAScriptWithItsText()
    {
        var main = NewMain();
        Dialogs.Choice = SaveChoice.Discard;
        var script = AddScript(main, "SELECT 42", persisted: false);
        await main.CloseTabCommand.ExecuteAsync(script);
        Assert.Empty(main.Tabs);

        await main.ReopenClosedTabAsync();

        var back = Assert.IsType<ScriptTabViewModel>(main.ActiveTab);
        Assert.Equal("SELECT 42", back.Text);
        back.Close();
    }

    [Fact]
    public async Task Reopen_IgnoresAnEmptyScript_AndSaysThereIsNothing()
    {
        var main = NewMain();
        Dialogs.Choice = SaveChoice.Discard;
        var script = main.NewScript()!;
        await main.CloseTabCommand.ExecuteAsync(script);
        Assert.Equal(0, main.ClosedTabCount);

        await main.ReopenClosedTabAsync();

        Assert.Empty(main.Tabs);
        Assert.Contains("No closed tab", T.Fx.Status.Message);
    }

    [Fact]
    public async Task Reopen_SkipsAClosedTabWhoseConnectionWasDeleted()
    {
        var main = NewTableMain();
        var orders = await OpenAsync(main, "orders");
        await main.CloseTabCommand.ExecuteAsync(orders);
        main.Tree.Config.Connections.Clear();   // the connection no longer exists

        await main.ReopenClosedTabAsync();

        Assert.Empty(main.Tabs);
        Assert.Equal(0, main.ClosedTabCount);
        Assert.Contains("No closed tab", T.Fx.Status.Message);
    }

    [Fact]
    public async Task Reopen_RemembersAtMostFiftyTabs_ForgettingTheOldest()
    {
        var main = NewMain();
        Dialogs.Choice = SaveChoice.Discard;
        for (var i = 0; i < MainViewModel.MaxClosedTabs + 5; i++)
            await main.CloseTabCommand.ExecuteAsync(AddScript(main, $"SELECT {i}", persisted: false));
        Assert.Equal(MainViewModel.MaxClosedTabs, main.ClosedTabCount);

        await main.ReopenClosedTabAsync();

        Assert.Equal("SELECT 5", Assert.IsType<ScriptTabViewModel>(main.ActiveTab).Text);   // 0..4 were forgotten
    }

    [Fact]
    public async Task CtrlShiftT_ReopensATab_AndCtrlShiftP_IsTheAllTablesPicker()
    {
        var main = NewMain();
        Dialogs.Choice = SaveChoice.Discard;
        var script = AddScript(main, "SELECT 1", persisted: false);
        await main.CloseTabCommand.ExecuteAsync(script);

        Assert.True(MainShortcuts.Handle(main, Key.T, KeyModifiers.Control | KeyModifiers.Shift));
        await Task.Delay(100);
        Assert.IsType<ScriptTabViewModel>(main.ActiveTab);

        Assert.True(MainShortcuts.Handle(main, Key.P, KeyModifiers.Control | KeyModifiers.Shift));
        await Task.Delay(100);
        Assert.Contains("metadata cache", T.Fx.Status.Message, StringComparison.OrdinalIgnoreCase);   // no cache in this shell: the picker says so
    }

    // ---- Ctrl+T text memory ----

    [Fact]
    public async Task CtrlT_StartsFromTheTextTypedLastTime_EvenAfterEscape()
    {
        var main = NewTableMain();
        await OpenAsync(main, "orders");
        main.ShowTablePickerCommand.Execute(null);
        var first = main.QuickOpen!;
        Assert.Equal("", first.Query);
        first.Query = "cust";
        first.Cancel();   // Esc clears the box but not the memory
        Assert.Null(main.QuickOpen);

        main.ShowTablePickerCommand.Execute(null);

        var second = main.QuickOpen!;
        Assert.Equal("cust", second.Query);
        Assert.Contains(second.Results, r => r.Label == "customers");   // already ranked for the remembered text
    }

    [Fact]
    public async Task CtrlT_Memory_FollowsTheLatestText_IncludingAnEmptiedBox()
    {
        var main = NewTableMain();
        await OpenAsync(main, "orders");
        main.ShowTablePickerCommand.Execute(null);
        main.QuickOpen!.Query = "cust";
        main.QuickOpen!.Cancel();
        main.ShowTablePickerCommand.Execute(null);
        main.QuickOpen!.Query = "";   // the user wiped the remembered text
        main.QuickOpen!.Cancel();

        main.ShowTablePickerCommand.Execute(null);

        Assert.Equal("", main.QuickOpen!.Query);
    }

    // ---- typo tolerance ----

    [Theory]
    [InlineData("custmers", "customers")]      // a letter missing
    [InlineData("custommers", "customers")]    // an extra letter
    [InlineData("custimers", "customers")]     // a wrong letter
    [InlineData("cutsomers", "customers")]     // two letters swapped
    [InlineData("ordrs", "orders")]
    public void Tolerant_FindsTheTableDespiteATypo(string typed, string table) =>
        Assert.NotNull(FuzzyMatcher.ScoreTolerant(typed, table));

    [Theory]
    [InlineData("xyzxyz", "customers")]
    [InlineData("cat", "status")]              // up to 3 letters stays strict
    [InlineData("custxxxxers", "customers")]   // too many differences
    public void Tolerant_StillRejectsRealMismatches(string typed, string table) =>
        Assert.Null(FuzzyMatcher.ScoreTolerant(typed, table));

    [Fact]
    public void Tolerant_ExactMatchesAlwaysRankAboveTypoMatches()
    {
        var exact = FuzzyMatcher.ScoreTolerant("users", "users_archive_2020_backup_table")!.Value;
        var typo = FuzzyMatcher.ScoreTolerant("users", "usrs")!.Value;
        Assert.True(exact > typo);
        Assert.Equal(FuzzyMatcher.Score("users", "users"), FuzzyMatcher.ScoreTolerant("users", "users"));   // strict score untouched
    }

    [Fact]
    public async Task CtrlT_ListsTheTypoMatch()
    {
        var main = NewTableMain();
        await OpenAsync(main, "orders");
        main.ShowTablePickerCommand.Execute(null);

        main.QuickOpen!.Query = "custmers";

        Assert.Equal("customers", main.QuickOpen!.Results.First().Label);
    }
}
