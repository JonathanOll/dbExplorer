using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using Microsoft.Data.Sqlite;

namespace DbExplorer.App.Tests;

/// <summary>The Structure sub-tab with hundreds of columns, foreign keys and indexes: every row reachable, no jump.</summary>
public class StructureScrollTests : IDisposable
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

    private async Task<(TableTabViewModel Tab, TabControl Tabs, Window Window, List<DataGrid> Grids, ScrollViewer Scroll)> Show(
        int columns, int foreignKeys = 0, int indexes = 0, double height = 700)
    {
        var parts = Enumerable.Range(1, columns).Select(i => $"c{i} INTEGER" + (i <= foreignKeys ? " REFERENCES customers(id)" : ""));
        var sql = $"CREATE TABLE wide ({string.Join(", ", parts)});"
                  + string.Concat(Enumerable.Range(1, indexes).Select(i => $"CREATE INDEX ix_{i:000} ON wide(c{i});"));
        using (var c = new SqliteConnection($"Data Source={_t.DbFile};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        var tab = await _t.OpenTab("wide");
        var view = new TableTabView { DataContext = tab };
        var window = new Window { Content = view, Width = 1000, Height = height };
        window.Show();
        var tabs = view.FindControl<TabControl>("SubTabs")!;
        tabs.SelectedIndex = 1;
        await Pump();
        var grids = view.GetVisualDescendants().OfType<DataGrid>().Where(g => g.Classes.Contains("structure")).ToList();
        return (tab, tabs, window, grids, view.FindControl<ScrollViewer>("StructureScroll")!);
    }

    // Rows actually on screen: recycled rows stay in the tree, hidden or outside the rows area.
    private static List<T> OnScreen<T>(DataGrid grid)
    {
        var area = grid.GetVisualDescendants().OfType<DataGridRowsPresenter>().Single();
        return grid.GetVisualDescendants().OfType<DataGridRow>()
            .Where(r => r.IsVisible && r.Bounds.Bottom > 1 && r.Bounds.Top < area.Bounds.Height - 1)
            .OrderBy(r => r.Bounds.Top).Select(r => (T)r.DataContext!).ToList();
    }

    private static async Task<List<T>> WheelToEnd<T>(Window window, DataGrid grid, int count)
    {
        var point = grid.TranslatePoint(new Point(200, grid.Bounds.Height / 2), window)!.Value;
        var seen = new List<T>(OnScreen<T>(grid));
        var previousTop = seen[0];
        var all = seen.Distinct().ToList();
        for (var i = 0; i < count; i++)
        {
            window.MouseWheel(point, new Vector(0, -1));
            await Pump(2);
            var now = OnScreen<T>(grid);
            // No jump: what is on top after one notch was already on screen before it.
            Assert.Contains(now[0], seen);
            seen = now;
            all.AddRange(now.Except(all));
            if (all.Count == count) break;
        }
        return all;
    }

    [AvaloniaFact]
    public async Task ManyColumns_TheWheelReachesEveryRow_WithoutJumping()
    {
        var (_, _, window, grids, scroll) = await Show(columns: 320);
        var columns = grids[0];

        Assert.True(columns.Bounds.Height < scroll.Viewport.Height, $"grid {columns.Bounds.Height}, viewport {scroll.Viewport.Height}");
        Assert.Equal("c1", OnScreen<StructureColumn>(columns)[0].Name);
        // The page itself does not scroll: the other sections stay on screen.
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1, $"extent {scroll.Extent.Height}, viewport {scroll.Viewport.Height}");

        var seen = await WheelToEnd<StructureColumn>(window, columns, 320);

        Assert.Equal(Enumerable.Range(1, 320).Select(i => $"c{i}"), seen.Select(c => c.Name));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ManyForeignKeysAndIndexes_EachSectionScrollsItsOwnRows()
    {
        var (_, _, window, grids, scroll) = await Show(columns: 120, foreignKeys: 80, indexes: 90);

        Assert.Equal(3, grids.Count);
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1);
        Assert.All(grids, g => Assert.InRange(g.Bounds.Height, 80, scroll.Viewport.Height / 2));
        Assert.Equal(80, (await WheelToEnd<StructureForeignKey>(window, grids[1], 80)).Count);
        Assert.Equal(90, (await WheelToEnd<StructureIndex>(window, grids[2], 90)).Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FewRows_TheGridsTakeOnlyWhatTheyNeed_AndTheColumnsGetTheRest()
    {
        var (_, _, window, grids, _) = await Show(columns: 200, foreignKeys: 1, indexes: 2);

        Assert.InRange(grids[1].Bounds.Height, 40, 90);
        Assert.InRange(grids[2].Bounds.Height, 60, 120);
        Assert.True(grids[0].Bounds.Height > 300, $"grids {string.Join(" / ", grids.Select(g => g.Bounds.Height))}");
        Assert.Equal(2, OnScreen<StructureIndex>(grids[2]).Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ShortWindow_SectionsKeepAMinimumHeight_AndThePageScrolls()
    {
        var (_, _, window, grids, scroll) = await Show(columns: 50, foreignKeys: 20, indexes: 20, height: 320);

        Assert.All(grids, g => Assert.True(g.Bounds.Height >= 80, $"grid {g.Bounds.Height}"));
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DoubleClick_JumpsToData_OnARowOnly()
    {
        var (_, tabs, window, grids, _) = await Show(columns: 100);
        var grid = grids[0];
        grid.SelectedIndex = 0;

        void DoubleClick(Visual target)
        {
            var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
            for (var i = 0; i < 2; i++)
            {
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
            }
        }

        DoubleClick(grid.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical && b.IsVisible));
        await Pump();
        Assert.Equal(1, tabs.SelectedIndex);

        DoubleClick(grid.GetVisualDescendants().OfType<DataGridRow>()
            .Where(r => r.IsVisible && r.Bounds.Top >= 0).OrderBy(r => r.Bounds.Top).First());
        await Pump();
        Assert.Equal(0, tabs.SelectedIndex);
        window.Close();
    }
}
