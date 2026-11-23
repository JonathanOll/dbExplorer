namespace DbExplorer.Core.Model;

/// <summary>Reference to a table or view. <see cref="Schema"/> is null for engines without schemas (SQLite).</summary>
public record TableRef(string? Schema, string Name)
{
    public override string ToString() => Schema is null ? Name : $"{Schema}.{Name}";
}

public record ColumnInfo(string Name, string DataType, Type ClrType, bool Nullable, bool IsPrimaryKey, int Ordinal);

public record ForeignKeyInfo(
    string Name,
    IReadOnlyList<string> Columns,
    TableRef RefTable,
    IReadOnlyList<string> RefColumns);

public record IndexInfo(string Name, bool Unique, IReadOnlyList<string> Columns);

public record TableInfo(
    TableRef Ref,
    bool IsView,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<ForeignKeyInfo> ForeignKeys,
    IReadOnlyList<IndexInfo> Indexes);
