namespace DbExplorer.Core.Comparing;

/// <summary>A column of one side of a comparison; the type is what the grid knows, often nothing for a script result.</summary>
public sealed record ComparedColumn(string Name, string? TypeName = null, Type? ClrType = null, bool IsPrimaryKey = false);

/// <summary>One side of a comparison: the loaded rows of a result, in its column order.</summary>
public sealed record ComparedData(IReadOnlyList<ComparedColumn> Columns, IReadOnlyList<object?[]> Rows);

/// <summary>A column present on both sides: its position on the left and on the right.</summary>
public sealed record ColumnPair(string Name, int Left, int Right);

/// <param name="Keys">The columns that identify a row; empty matches the rows by position.</param>
/// <param name="Compared">The columns whose values are compared (the key columns need not be among them).</param>
/// <param name="IgnoreCase">Text differing only in case is the same (key columns included).</param>
/// <param name="IgnoreSpaces">Leading, trailing and repeated white space in text does not count (key columns included).</param>
public sealed record ComparisonOptions(
    IReadOnlyList<ColumnPair> Keys,
    IReadOnlyList<ColumnPair> Compared,
    bool IgnoreCase = false,
    bool IgnoreSpaces = false);

/// <summary><see cref="Removed"/> is a row only the left side has, <see cref="Added"/> one only the right side has.</summary>
public enum RowChange { Same, Changed, Added, Removed }

/// <param name="Left">Row index on the left, -1 for an added row.</param>
/// <param name="Right">Row index on the right, -1 for a removed row.</param>
/// <param name="Changed">Positions in <see cref="ComparisonOptions.Compared"/> of the cells that differ; empty unless changed.</param>
public readonly record struct ComparedRow(RowChange Change, int Left, int Right, int[] Changed);

/// <summary>Keys that occur more than once on a side: their rows are paired in order of occurrence.</summary>
/// <param name="LeftRows">Rows of the left side whose key another left row has too.</param>
/// <param name="RightRows">The same on the right.</param>
/// <param name="Examples">A few of those keys, readable ("id = 3").</param>
public sealed record DuplicateKeys(int LeftRows, int RightRows, IReadOnlyList<string> Examples)
{
    public static readonly DuplicateKeys None = new(0, 0, []);

    public bool Any => LeftRows > 0 || RightRows > 0;
}

/// <summary>
/// The rows in the order of the left side, each added row after the matched row it follows on the right.
/// </summary>
public sealed record ComparisonResult(
    IReadOnlyList<ComparedRow> Rows, int Same, int Changed, int Added, int Removed, DuplicateKeys Duplicates)
{
    public int Differences => Changed + Added + Removed;
}
