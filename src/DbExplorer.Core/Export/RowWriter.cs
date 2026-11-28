namespace DbExplorer.Core.Export;

/// <summary>
/// Writes rows one at a time in one format, so an export of any size uses constant memory.
/// Call <see cref="Begin"/> once, <see cref="Write"/> per row, then <see cref="End"/>.
/// </summary>
public abstract class RowWriter
{
    public abstract void Begin(IReadOnlyList<ExportColumn> columns);

    public abstract void Write(object?[] row);

    public virtual void End() { }

    /// <summary>Something the user should know about what was written (e.g. cut cells), or null.</summary>
    public virtual string? Warning => null;

    protected static object? Value(object?[] row, int i) => i < row.Length && row[i] is not DBNull ? row[i] : null;
}
