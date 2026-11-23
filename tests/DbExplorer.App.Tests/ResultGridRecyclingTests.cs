using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App.Tests;

/// <summary>
/// The DataGrid recycles row containers when the page is scrolled and does not rebuild their cells: every cell must
/// follow the row it currently shows (text, background, and the row it acts on), not the one it was first created for.
/// </summary>
public class ResultGridRecyclingTests : IDisposable
{
    private readonly TableTabFixture _t = new();
    public void Dispose() => _t.Dispose();

    private static async Task Pump(int rounds = 20)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    private static DataGrid InnerGrid(Control root) => root.GetVisualDescendants().OfType<DataGrid>().First(g => g.Columns.Count > 0);

    /// <summary>Every realised cell must show the value of the row container it sits in.</summary>
    private static List<string> Mismatches(DataGrid grid)
    {
        var bad = new List<string>();
        var rows = grid.GetVisualDescendants().OfType<DataGridRow>().ToList();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            var values = GridCellColumn.ValuesOf(row.DataContext)!;
            foreach (var cell in row.GetVisualDescendants().OfType<GridCell>())
            {
                var expected = CellFormatter.Format(values[cell.Index]).Text;
                if (!ReferenceEquals(cell.Row, row.DataContext) || cell.Text != expected)
                    bad.Add($"row {row.Index} col {cell.Index}: shows '{cell.Text}' (acts on {Describe(cell.Row)}), expected '{expected}'");
            }
        }
        return bad;

        static string Describe(object? row) => GridCellColumn.ValuesOf(row) is { } v ? string.Join("|", v) : "null";
    }

    [AvaloniaFact]
    public async Task ScrollingATableGrid_KeepsEveryCellInSyncWithItsRow()
    {
        var tab = await _t.OpenTab("big");
        var grid = new ResultGrid { Columns = tab.GridColumns, ItemsSource = tab.Rows, Features = tab, Editing = tab };
        var window = new Window { Content = grid, Width = 1000, Height = 400 };
        window.Show();
        await Pump();
        Assert.Empty(Mismatches(InnerGrid(grid)));

        grid.SelectRow(150);   // scrolls far: the realised rows are recycled containers
        await Pump();

        Assert.Empty(Mismatches(InnerGrid(grid)));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ScrollingAPlainResultGrid_KeepsEveryCellInSyncWithItsRow()
    {
        var rows = Enumerable.Range(1, 300).Select(i => new object?[] { i, "row " + i }).ToList();
        var columns = new List<ResultColumn> { new("id", "int", typeof(int), false, null), new("label", "text", typeof(string), false, null) };
        var grid = new ResultGrid { Columns = columns, ItemsSource = rows };
        var window = new Window { Content = grid, Width = 1000, Height = 400 };
        window.Show();
        await Pump();

        window.MouseWheel(new Point(300, 200), new Vector(0, -30));
        await Pump();

        var inner = InnerGrid(grid);
        Assert.True(inner.GetVisualDescendants().OfType<DataGridRow>().Min(r => r.Index) > 0, "the grid should have scrolled");
        Assert.Empty(Mismatches(inner));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ClickingAddRow_OnALongPage_ShowsTheNewRowAsAnEmptyHighlightedRow()
    {
        var tab = await _t.OpenTab("big");   // a full page of 200 rows: the new row is far below the visible ones
        var view = new TableTabView { DataContext = tab };
        var window = new Window { Content = view, Width = 1100, Height = 500 };
        window.Show();
        await Pump();

        var button = view.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "+ Row");
        var p = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        await Pump();

        var inner = InnerGrid(view);
        Assert.Equal(200, inner.SelectedIndex);
        var newRow = inner.GetVisualDescendants().OfType<DataGridRow>().Single(r => r.Index == 200);
        var texts = newRow.GetVisualDescendants().OfType<GridCell>().Select(c => c.Text).ToList();
        Assert.NotEmpty(texts);   // (the first editable cell is already in edit mode: it is a text box, not a GridCell)
        Assert.All(texts, t => Assert.Equal("NULL", t));
        Assert.All(newRow.GetVisualDescendants().OfType<GridCell>(), c => Assert.True(c.Row is RowViewModel { IsNew: true }));
        Assert.Empty(Mismatches(inner));
        window.Close();
    }
}
