using DbExplorer.Core.Comparing;

namespace DbExplorer.App.ViewModels;

/// <summary>How a cell of a comparison is marked (always with text or a text style too, never by colour alone).</summary>
public enum CellMark { None, Changed, Added, Removed }

/// <summary>A grid row whose cells carry a comparison mark.</summary>
public interface IMarkedRow
{
    object?[] Values { get; }

    CellMark MarkOf(int column);

    /// <summary>What the tooltip of the cell says about its mark, or null.</summary>
    string? TipOf(int column);
}

/// <summary>A cell that differs: the grid shows and copies "left → right".</summary>
public sealed record CellChange(object? Left, object? Right)
{
    public override string ToString() => $"{CellFormatter.Format(Left).Text} → {CellFormatter.Format(Right).Text}";
}

/// <summary>Where a column of the comparison grid takes its values from, shared by all rows.</summary>
/// <param name="Compared">Position in the compared columns, or -1 for a key column that is not compared.</param>
public sealed record DiffColumn(ColumnPair Pair, int Compared);

/// <summary>
/// One row of the comparison grid. The first cell says what happened in words; the values are built on first use, so a
/// large comparison only builds the rows that are shown or copied.
/// </summary>
public sealed class DiffRow(ComparedRow row, IReadOnlyList<DiffColumn> columns, ComparedData left, ComparedData right) : IMarkedRow
{
    private object?[]? _values;

    public ComparedRow Row => row;

    public bool IsDifference => row.Change != RowChange.Same;

    /// <summary>Grid column of the first changed cell, or 0.</summary>
    public int FirstMarkedColumn
    {
        get
        {
            for (var i = 0; i < columns.Count; i++)
                if (MarkOf(i + 1) == CellMark.Changed) return i + 1;
            return 0;
        }
    }

    public object?[] Values => _values ??= Build();

    public static string Label(ComparedRow row) => row.Change switch
    {
        RowChange.Changed => $"≠ Changed ({row.Changed.Length})",
        RowChange.Added => "+ Only in B",
        RowChange.Removed => "− Only in A",
        _ => "= Same",
    };

    public CellMark MarkOf(int column) => row.Change switch
    {
        RowChange.Added => CellMark.Added,
        RowChange.Removed => CellMark.Removed,
        RowChange.Changed when column == 0 => CellMark.Changed,
        RowChange.Changed when column <= columns.Count && Array.IndexOf(row.Changed, columns[column - 1].Compared) >= 0 => CellMark.Changed,
        _ => CellMark.None,
    };

    public string? TipOf(int column)
    {
        if (row.Change == RowChange.Added) return $"Only in B (row {row.Right + 1})";
        if (row.Change == RowChange.Removed) return $"Only in A (row {row.Left + 1})";
        if (column > 0 && column < Values.Length && Values[column] is CellChange change)
            return $"A: {Full(change.Left)}\nB: {Full(change.Right)}";
        return column == 0 ? $"Row {row.Left + 1} of A, row {row.Right + 1} of B" : null;
    }

    private static string Full(object? value) => CellFormatter.Format(value) is var cell && cell.Tooltip is { } tip && !cell.IsBinary ? tip : cell.Text;

    private object?[] Build()
    {
        var values = new object?[columns.Count + 1];
        values[0] = Label(row);
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var a = row.Left >= 0 ? At(left.Rows[row.Left], column.Pair.Left) : null;
            var b = row.Right >= 0 ? At(right.Rows[row.Right], column.Pair.Right) : null;
            values[i + 1] = row.Left < 0 ? b
                : row.Change == RowChange.Changed && Array.IndexOf(row.Changed, column.Compared) >= 0 ? new CellChange(a, b)
                : a;
        }
        return values;
    }

    private static object? At(object?[] values, int column) => column < values.Length ? values[column] : null;
}
