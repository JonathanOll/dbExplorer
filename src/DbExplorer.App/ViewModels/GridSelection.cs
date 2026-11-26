namespace DbExplorer.App.ViewModels;

/// <summary>Rows and columns of a grid, by index in the order shown.</summary>
public sealed record GridSlice(IReadOnlyList<int> Rows, IReadOnlyList<int> Columns);

/// <summary>
/// The selection a grid has on top of its row selection: a rectangle of cells (drag, Alt+click, Shift+arrows) or whole
/// columns (Ctrl+click on a header, Ctrl+Space). One of the two at a time.
/// </summary>
public sealed class GridSelection
{
    private (int Row, int Column) _anchor, _extent;
    private readonly SortedSet<int> _columns = [];

    public bool IsBlock { get; private set; }

    public bool HasColumns => _columns.Count > 0;

    public bool IsActive => IsBlock || HasColumns;

    /// <summary>The corner of the rectangle that moves.</summary>
    public (int Row, int Column) Extent => _extent;

    public void SetBlock((int Row, int Column) anchor, (int Row, int Column) extent)
    {
        _columns.Clear();
        _anchor = anchor;
        _extent = extent;
        IsBlock = true;
    }

    /// <summary>Adds the column to the selected columns, or takes it off.</summary>
    public void ToggleColumn(int column)
    {
        IsBlock = false;
        if (!_columns.Remove(column)) _columns.Add(column);
    }

    public void Clear()
    {
        IsBlock = false;
        _columns.Clear();
    }

    public bool ContainsColumn(int column) =>
        IsBlock ? column >= Math.Min(_anchor.Column, _extent.Column) && column <= Math.Max(_anchor.Column, _extent.Column)
        : _columns.Contains(column);

    public bool ContainsRow(int row) =>
        IsBlock ? row >= Math.Min(_anchor.Row, _extent.Row) && row <= Math.Max(_anchor.Row, _extent.Row) : HasColumns;

    /// <summary>The selected cells of a grid of this size; null when nothing of the selection is inside it.</summary>
    public GridSlice? Slice(int rowCount, int columnCount)
    {
        if (!IsActive) return null;
        var rows = Enumerable.Range(0, rowCount).Where(ContainsRow).ToList();
        var columns = Enumerable.Range(0, columnCount).Where(ContainsColumn).ToList();
        return columns.Count == 0 || (IsBlock && rows.Count == 0) ? null : new GridSlice(rows, columns);
    }
}
