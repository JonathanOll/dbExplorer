using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Tests;

public class GridColumnSwitchTests : IDisposable
{
    private readonly TableTabFixture _t = new();

    public void Dispose() => _t.Dispose();

    // ---- RowViewModel / grid: a grid still showing rows of another table must not crash ----

    [Fact]
    public void IsDirty_OutOfRangeColumn_IsFalse()
    {
        var row = new RowViewModel([1, "a"]);
        Assert.False(row.IsDirty(5));
        Assert.False(new RowViewModel([1, "a"], isNew: true).IsDirty(2));
        row.Values[1] = "b";
        Assert.True(row.IsDirty(1));
    }

    [AvaloniaFact]
    public async Task SwitchingToATableWithMoreColumns_KeepsTheGridColumns()
    {
        // The content host reuses the view: Columns changes before ItemsSource. Rows of the narrower table under the wider
        // table's columns used to throw inside the binding and leave the grid without columns.
        var narrow = await _t.OpenTab("customers");
        var wide = await _t.OpenTab("orders");
        var host = new ContentControl();
        var window = new Window { Content = host, Width = 1200, Height = 600 };
        window.Show();
        host.Content = narrow;
        Dispatcher.UIThread.RunJobs();
        host.Content = wide;
        Dispatcher.UIThread.RunJobs();
        for (var i = 0; i < 5; i++) await Task.Delay(10);
        Dispatcher.UIThread.RunJobs();

        var grid = host.GetVisualDescendants().OfType<DataGrid>().First(g => g.Columns.Count > 0 || g.ItemsSource is not null);
        Assert.Equal(wide.GridColumns.Count, grid.Columns.Count);
        window.Close();
    }

}