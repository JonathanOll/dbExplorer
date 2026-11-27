using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Comparing;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests;

/// <summary>The comparison tab, the "select / compare with selected" flow and its grid.</summary>
public class CompareTabTests : IDisposable
{
    private readonly ScriptTabFixture _t = new();

    public void Dispose() => _t.Dispose();

    private CompareSnapshot Side(string name, string columns, string? partial = null, params object?[][] rows) =>
        new(name, name + " (Lite)", _t.Config,
            new ComparedData(columns.Split(',').Select(c => new ComparedColumn(c)).ToList(), rows), partial);

    private async Task<CompareTabViewModel> Sample()
    {
        var tab = new CompareTabViewModel(
            Side("before", "id,name,old", null, [1, "a", 0], [2, "b", 0], [3, "c", 0]),
            Side("after", "id,name", "the result stopped at the row limit (3 rows).", [1, "a"], [2, "B"], [4, "d"]));
        await tab.Pending;
        return tab;
    }

    [Fact]
    public async Task Opens_WithAGuessedKey_Counts_AndNotices()
    {
        var tab = await Sample();

        Assert.True(tab.IsByKey);
        Assert.Equal(["id"], tab.Choices.Where(c => c.IsKey).Select(c => c.Name));
        Assert.Contains("id", tab.KeyNote);
        Assert.Equal("≠ 1 changed", tab.ChangedText);
        Assert.Equal("+ 1 only in B", tab.AddedText);
        Assert.Equal("− 1 only in A", tab.RemovedText);
        Assert.Equal("= 1 same", tab.SameText);
        Assert.Equal("3 differences", tab.PositionText);
        Assert.Contains("B: the result stopped", tab.PartialText);
        Assert.Contains("Only in A: old", tab.ColumnsText);
        Assert.Equal(["Difference", "id", "name"], tab.GridColumns.Select(c => c.Name));
        Assert.Equal("Compare · before / after", tab.Title);
    }

    [Fact]
    public async Task Rows_SayWhatHappened_AndMarkTheChangedCells()
    {
        var tab = await Sample();

        Assert.Equal(["= Same", "≠ Changed (1)", "+ Only in B", "− Only in A"], tab.Rows.Select(r => (string)r.Values[0]!));
        var changed = tab.Rows[1];
        Assert.Equal("b → B", changed.Values[2]!.ToString());
        Assert.Equal(CellMark.Changed, changed.MarkOf(2));
        Assert.Equal(CellMark.None, changed.MarkOf(1));
        Assert.Equal("A: b\nB: B", changed.TipOf(2));
        Assert.Equal(CellMark.Added, tab.Rows[2].MarkOf(1));
        Assert.Equal(CellMark.Removed, tab.Rows[3].MarkOf(1));
    }

    [Fact]
    public async Task OnlyDifferences_AndNavigation_WrapAround()
    {
        var tab = await Sample();
        var revealed = new List<(int, int)>();
        tab.RevealRequested += (r, c) => revealed.Add((r, c));

        tab.OnlyDifferences = true;
        Assert.Equal(3, tab.Rows.Count);
        tab.NextDifference();
        tab.NextDifference();
        tab.NextDifference();
        tab.NextDifference();
        tab.PreviousDifference();

        Assert.Equal([(0, 2), (1, 0), (2, 0), (0, 2), (2, 0)], revealed);
        Assert.Equal("3 of 3", tab.PositionText);
    }

    [Fact]
    public async Task Options_CompareAgain()
    {
        var tab = await Sample();

        tab.IgnoreCase = true;
        await tab.Pending;
        Assert.Equal(0, tab.Result!.Changed);

        tab.IgnoreCase = false;
        tab.Choices.Single(c => c.Name == "name").IsCompared = false;
        await tab.Pending;
        Assert.Equal(0, tab.Result!.Changed);
        Assert.Equal(["Difference", "id"], tab.GridColumns.Select(c => c.Name));

        tab.IsByKey = false;
        await tab.Pending;
        Assert.Equal("Rows are matched by position.", tab.MatchText);
        Assert.Equal(1, tab.Result!.Changed);

        tab.IsByKey = true;
        tab.Choices.Single(c => c.Name == "id").IsKey = false;
        await tab.Pending;
        Assert.Contains("Choose at least one key column", tab.Problem);
        Assert.False(tab.ShowGrid);
    }

    [Fact]
    public async Task Swap_ExchangesTheSides()
    {
        var tab = await Sample();

        tab.Swap();
        await tab.Pending;

        Assert.StartsWith("after", tab.LeftText);
        Assert.Equal("B → b", tab.Rows.Single(r => r.Row.Change == RowChange.Changed).Values[2]!.ToString());
        Assert.Contains("Only in B: old", tab.ColumnsText);
    }

    [Fact]
    public async Task DuplicateKeys_AreSaid()
    {
        var tab = new CompareTabViewModel(Side("a", "id", null, [1], [1]), Side("b", "id", null, [1]));
        await tab.Pending;
        tab.IsByKey = true;
        tab.Choices[0].IsKey = true;
        await tab.Pending;

        Assert.True(tab.HasDuplicates);
        Assert.Contains("2 rows in A", tab.DuplicateText);
    }

    [Fact]
    public async Task NoCommonColumn_IsAProblem()
    {
        var tab = new CompareTabViewModel(Side("a", "x", null, [1]), Side("b", "y", null, [1]));
        await tab.Pending;

        Assert.Contains("no column name in common", tab.Problem);
        Assert.False(tab.CanNavigate);
    }

    [Fact]
    public async Task Cancel_StopsTheComparison_AndItCanRunAgain()
    {
        var rows = Enumerable.Range(0, 200_000).Select(i => new object?[] { i }).ToArray();
        var tab = new CompareTabViewModel(Side("a", "id", null, rows), Side("b", "id", null, rows));

        tab.Cancel();
        await tab.Pending;
        Assert.False(tab.IsBusy);
        Assert.Contains("cancelled", tab.Problem);

        tab.Recompare();
        await tab.Pending;
        Assert.Null(tab.Problem);
        Assert.True(tab.HasResult);
    }

    [Fact]
    public async Task ScriptResults_CompareAcrossTheSameScript_WithTruncationSaid()
    {
        var script = await _t.NewTab("SELECT * FROM t; SELECT * FROM big");
        script.RowLimit = 2;
        await script.RunScriptAsync();
        var first = script.Results[0].Sets[0];
        var second = script.Results[1].Sets[0];

        var a = script.CreateCompareSnapshot(first.Columns, first.Rows);
        var b = script.CreateCompareSnapshot(second.Columns, second.Rows);

        Assert.EndsWith("· 1", a.Name);
        Assert.EndsWith("· 2", b.Name);
        Assert.Contains("row limit", a.Partial);
        Assert.NotSame(first.Rows, a.Data.Rows);
    }

    [AvaloniaFact]
    public async Task TheGrid_SelectsThenCompares_FromThePalette_AndShowsTheDifferences()
    {
        var script = await _t.NewTab("SELECT id, name FROM t ORDER BY id");
        await script.RunScriptAsync();
        var main = new MainViewModel(_t.Fx.CreateTree(new AppConfig([], [_t.Config], new())), _t.Status);
        var window = new MainWindow { DataContext = main, Width = 1200, Height = 700 };
        window.Show();
        main.Tabs.Add(script);
        main.ActiveTab = script;
        Assert.Same(main.Compare, script.Compare);

        AppCommand Command(string id) => main.Registry.Snapshot().Single(c => c.Id == id);
        await Wait(() => Command("compare.select").IsEnabled);
        Assert.Equal(CompareService.NothingSelectedReason, Command("compare.with-selected").DisabledReason);
        Assert.Equal("Open a comparison first", Command("compare.next").DisabledReason);

        await Command("compare.select").Run!();
        await Command("compare.with-selected").Run!();

        var compare = Assert.IsType<CompareTabViewModel>(main.ActiveTab);
        await compare.Pending;
        Assert.True(compare.IsIdentical);
        Assert.False(Command("compare.next").IsEnabled);

        compare.IsByKey = false;
        await compare.Pending;
        await Wait(() => window.GetVisualDescendants().OfType<CompareView>().Any()
                         && window.GetVisualDescendants().OfType<GridCell>().Any(c => c.Text == "= Same"));
        await main.CloseTabCommand.ExecuteAsync(compare);
        window.Close();
    }

    private static async Task Wait(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
        Assert.True(done());
    }
}
