using DbExplorer.Core.Model;

namespace DbExplorer.Core.Diagrams;

/// <summary>Which columns the cards of a diagram show: all, the primary and foreign keys, or none (table names only).</summary>
public enum DiagramColumnMode { All, Keys, Names }

public sealed record DiagramColumn(string Name, string DataType, bool Nullable, bool IsPrimaryKey, bool IsForeignKey)
{
    public bool IsKey => IsPrimaryKey || IsForeignKey;
}

public sealed record DiagramTable(TableRef Ref, IReadOnlyList<DiagramColumn> Columns);

/// <summary>A foreign key between two tables of the diagram.</summary>
/// <param name="Child">Index (in <see cref="DiagramModel.Tables"/>) of the table that declares the key.</param>
/// <param name="Parent">Index of the referenced table; equal to <paramref name="Child"/> for a self reference.</param>
/// <param name="Optional">A key column accepts NULL: a child row may have no parent.</param>
/// <param name="Unique">The key columns are unique in the child: at most one child per parent.</param>
public sealed record DiagramRelation(
    string Name, int Child, int Parent,
    IReadOnlyList<string> Columns, IReadOnlyList<string> RefColumns,
    bool Optional, bool Unique)
{
    public bool IsSelf => Child == Parent;
}

/// <summary>The tables of a diagram and the foreign keys between them.</summary>
public sealed record DiagramModel(IReadOnlyList<DiagramTable> Tables, IReadOnlyList<DiagramRelation> Relations);
