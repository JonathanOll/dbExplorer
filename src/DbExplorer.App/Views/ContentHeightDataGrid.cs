using Avalonia;
using Avalonia.Controls;

namespace DbExplorer.App.Views;

/// <summary>
/// A read-only grid that asks for exactly the height of its content, up to what it is offered. The stock DataGrid asks for the
/// height of the rows it happens to display, which lags behind the space offered, so a container cannot size it from that.
/// Needs a fixed RowHeight and ColumnHeaderHeight.
/// </summary>
public sealed class ContentHeightDataGrid : DataGrid
{
    // Each row is followed by its 1 px horizontal grid line.
    private const double GridLine = 1;

    protected override Type StyleKeyOverride => typeof(DataGrid);

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = ItemsSource is System.Collections.ICollection items ? items.Count : 0;
        var content = ColumnHeaderHeight + GridLine + rows * (RowHeight + GridLine);
        if (double.IsNaN(content)) return base.MeasureOverride(availableSize);
        var height = Math.Min(content, availableSize.Height);
        var size = base.MeasureOverride(availableSize.WithHeight(height));
        return size.WithHeight(height);
    }
}
