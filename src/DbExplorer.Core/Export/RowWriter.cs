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

/// <summary>Passes on only the chosen columns, in the chosen order (the selection of a grid).</summary>
public sealed class ProjectingWriter(RowWriter inner, IReadOnlyList<int> indices) : RowWriter
{
    public override string? Warning => inner.Warning;

    public override void Begin(IReadOnlyList<ExportColumn> columns) => inner.Begin(indices.Select(i => columns[i]).ToList());

    public override void Write(object?[] row)
    {
        var picked = new object?[indices.Count];
        for (var i = 0; i < picked.Length; i++) picked[i] = Value(row, indices[i]);
        inner.Write(picked);
    }

    public override void End() => inner.End();
}
