using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

// Cells and columns: a rectangle of cells (drag across cells, Alt+click, Shift+arrows) or whole columns (Ctrl+click on a
// header, Ctrl+Space), on top of the row selection of the DataGrid. The state is in GridSelection; here it meets the visuals.
public partial class ResultGrid
{
    private readonly GridSelection _selection = new();
    private readonly HashSet<object> _pickedRows = new(ReferenceEqualityComparer.Instance);
    private (int Row, int Column)? _dragStart;

    /// <summary>The selected cells or columns beyond the row selection.</summary>
    public GridSelection CellSelection => _selection;

    /// <summary>The rows as the grid shows them (client sort included).</summary>
    private IReadOnlyList<object> ShownRows => (IReadOnlyList<object>?)_mirror ?? [];

    /// <summary>The cells a cell-range or column selection covers, or the selected rows; null when nothing is selected.</summary>
    public GridSlice? CurrentSlice()
    {
        var width = Columns?.Count ?? 0;
        if (_selection.Slice(ShownRows.Count, width) is { } picked) return picked;
        if (Grid.SelectedItems.Count == 0) return null;
        var selected = new HashSet<object>(Grid.SelectedItems.Cast<object>(), ReferenceEqualityComparer.Instance);
        var rows = ShownRows.Select((row, i) => (row, i)).Where(p => selected.Contains(p.row)).Select(p => p.i).ToList();
        return rows.Count == 0 ? null : new GridSlice(rows, Enumerable.Range(0, width).ToList());
    }

    /// <summary>Selects the rectangle between two cells (row and column indices as shown).</summary>
    public void SelectBlock((int Row, int Column) anchor, (int Row, int Column) extent)
    {
        _selection.SetBlock(anchor, extent);
        Grid.SelectedItems.Clear();   // one selection at a time: the row highlight would hide where the block ends
        RefreshPicked();
    }

    /// <summary>Selects the whole column, or takes it out of the selected columns.</summary>
    public void ToggleColumnSelection(int column)
    {
        _selection.ToggleColumn(column);
        Grid.SelectedItems.Clear();
        RefreshPicked();
    }

    public void ClearCellSelection()
    {
        if (!_selection.IsActive) return;
        _selection.Clear();
        RefreshPicked();
    }

    private bool IsPicked(object? row, int column) =>
        _selection.IsActive && _selection.ContainsColumn(column) && (_selection.HasColumns || (row is not null && _pickedRows.Contains(row)));

    private void RefreshPicked()
    {
        _pickedRows.Clear();
        if (_selection.IsBlock)
            for (var i = 0; i < ShownRows.Count; i++)
                if (_selection.ContainsRow(i)) _pickedRows.Add(ShownRows[i]);
        foreach (var cell in Grid.GetVisualDescendants().OfType<GridCell>()) cell.ApplyPicked();
        SelectedRowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>How many rows have a selected cell or are selected.</summary>
    public int SelectedRowCount =>
        _selection.IsBlock ? _pickedRows.Count : _selection.HasColumns ? ShownRows.Count : Grid.SelectedItems.Count;

    private (int Row, int Column)? PositionOf(GridCell? cell)
    {
        if (cell?.Row is not { } row) return null;
        for (var i = 0; i < ShownRows.Count; i++)
            if (ReferenceEquals(ShownRows[i], row)) return (i, cell.Index);
        return null;
    }

    private (int Row, int Column)? CurrentPosition()
    {
        if (Grid.SelectedItem is not { } row || Grid.CurrentColumn is not GridCellColumn column) return null;
        for (var i = 0; i < ShownRows.Count; i++)
            if (ReferenceEquals(ShownRows[i], row)) return (i, column.Index);
        return null;
    }

    // The cell under the pointer, wherever the press that captured it started.
    private GridCell? CellUnder(PointerEventArgs e)
    {
        var hit = Grid.InputHitTest(e.GetPosition(Grid)) as Visual;
        return hit?.FindAncestorOfType<GridCell>(includeSelf: true)
               ?? hit?.FindAncestorOfType<DataGridCell>(includeSelf: true)?.Content as GridCell;
    }

    private void OnSelectionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStart = null;
        var properties = e.GetCurrentPoint(Grid).Properties;
        var cell = CellUnder(e);
        if (cell is null || (e.Source as Visual)?.FindAncestorOfType<CellEditor>(includeSelf: true) is not null) return;
        if (properties.IsRightButtonPressed)
        {
            if (IsPicked(cell.Row, cell.Index)) e.Handled = true;   // the menu is about the selection: keep it
            return;
        }
        if (!properties.IsLeftButtonPressed) return;
        if (e.KeyModifiers == KeyModifiers.Alt)
        {
            if (PositionOf(cell) is not { } to) return;
            var from = _selection.IsBlock ? _anchorOfBlock : CurrentPosition() ?? to;
            e.Handled = true;
            Grid.Focus();
            _anchorOfBlock = from;
            SelectBlock(from, to);
            return;
        }
        ClearCellSelection();
        if (e.KeyModifiers == KeyModifiers.None) _dragStart = PositionOf(cell);
    }

    private (int Row, int Column) _anchorOfBlock;

    private void OnSelectionPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start) return;
        if (!e.GetCurrentPoint(Grid).Properties.IsLeftButtonPressed)
        {
            _dragStart = null;
            return;
        }
        if (PositionOf(CellUnder(e)) is not { } at) return;
        if (!_selection.IsBlock && at == start) return;   // still inside the first cell: an ordinary click
        if (_selection.IsBlock && at == _selection.Extent) return;
        _anchorOfBlock = start;
        SelectBlock(start, at);
    }

    /// <summary>Ctrl+Space selects the current column, Shift+arrows grow a rectangle, Esc drops it. True when handled.</summary>
    private bool HandleSelectionKey(KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control && Grid.CurrentColumn is GridCellColumn current)
        {
            ToggleColumnSelection(current.Index);
            return true;
        }
        if (e.Key == Key.Escape && _selection.IsActive)
        {
            ClearCellSelection();
            return true;
        }
        if (e.KeyModifiers != KeyModifiers.Shift) return false;
        var (down, right) = e.Key switch { Key.Up => (-1, 0), Key.Down => (1, 0), Key.Left => (0, -1), Key.Right => (0, 1), _ => (0, 0) };
        if (down == 0 && right == 0) return false;
        if (!_selection.IsBlock)
        {
            // Shift+Up / Down alone keep extending the row selection of the grid.
            if (right == 0 || CurrentPosition() is not { } here) return false;
            _anchorOfBlock = here;
            _selection.SetBlock(here, here);
        }
        var to = (Row: Math.Clamp(_selection.Extent.Row + down, 0, Math.Max(0, ShownRows.Count - 1)),
            Column: Math.Clamp(_selection.Extent.Column + right, 0, Math.Max(0, (Columns?.Count ?? 1) - 1)));
        SelectBlock(_anchorOfBlock, to);
        if (to.Row < ShownRows.Count && to.Column < Grid.Columns.Count) Grid.ScrollIntoView(ShownRows[to.Row], Grid.Columns[to.Column]);
        return true;
    }
}
