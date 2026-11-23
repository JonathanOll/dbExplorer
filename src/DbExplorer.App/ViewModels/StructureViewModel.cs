using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>Read-only description of a table for the Structure sub-tab: columns, foreign keys and indexes.</summary>
public sealed class StructureViewModel
{
    public static StructureViewModel Empty { get; } = new(null, _ => Task.CompletedTask);

    /// <param name="openTable">Opens a table in a new tab (used by the foreign-key links).</param>
    public StructureViewModel(TableInfo? table, Func<TableRef, Task> openTable)
    {
        if (table is null)
        {
            Columns = [];
            ForeignKeys = [];
            Indexes = [];
            return;
        }

        Columns = table.Columns.OrderBy(c => c.Ordinal).Select(c =>
        {
            var fk = table.ForeignKeys.FirstOrDefault(f => f.Columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase));
            return new StructureColumn(c, fk, openTable);
        }).ToList();
        ForeignKeys = table.ForeignKeys.Select(f => new StructureForeignKey(f, openTable)).ToList();
        Indexes = table.Indexes.Select(i => new StructureIndex(i.Name, i.Unique, string.Join(", ", i.Columns))).ToList();
    }

    public IReadOnlyList<StructureColumn> Columns { get; }

    public IReadOnlyList<StructureForeignKey> ForeignKeys { get; }

    public IReadOnlyList<StructureIndex> Indexes { get; }

    internal static string Describe(ForeignKeyInfo fk) => $"{fk.RefTable} ({string.Join(", ", fk.RefColumns)})";
}

public sealed class StructureColumn
{
    private readonly ForeignKeyInfo? _foreignKey;
    private readonly Func<TableRef, Task> _openTable;

    internal StructureColumn(ColumnInfo column, ForeignKeyInfo? foreignKey, Func<TableRef, Task> openTable)
    {
        Column = column;
        _foreignKey = foreignKey;
        _openTable = openTable;
        Key = (column.IsPrimaryKey, foreignKey is not null) switch
        {
            (true, true) => "PK, FK",
            (true, false) => "PK",
            (false, true) => "FK",
            _ => "",
        };
        References = foreignKey is null ? "" : StructureViewModel.Describe(foreignKey);
        OpenCommand = new AsyncRelayCommand(OpenReferenceAsync);
    }

    public ColumnInfo Column { get; }
    public int Ordinal => Column.Ordinal + 1;
    public string Name => Column.Name;
    public string DataType => Column.DataType;
    public bool Nullable => Column.Nullable;
    public string NullText => Column.Nullable ? "NULL" : "NOT NULL";
    public bool IsPrimaryKey => Column.IsPrimaryKey;

    /// <summary>"PK", "FK", "PK, FK" or empty.</summary>
    public string Key { get; }

    /// <summary>Referenced table and columns, e.g. "customers (id)"; empty when the column is not part of a foreign key.</summary>
    public string References { get; }

    public bool HasReference => _foreignKey is not null;

    public ICommand OpenCommand { get; }

    /// <summary>Opens the referenced table; does nothing for a column without a foreign key.</summary>
    public Task OpenReferenceAsync() => _foreignKey is null ? Task.CompletedTask : _openTable(_foreignKey.RefTable);
}

public sealed class StructureForeignKey
{
    private readonly ForeignKeyInfo _foreignKey;
    private readonly Func<TableRef, Task> _openTable;

    internal StructureForeignKey(ForeignKeyInfo foreignKey, Func<TableRef, Task> openTable)
    {
        _foreignKey = foreignKey;
        _openTable = openTable;
        Columns = string.Join(", ", foreignKey.Columns);
        Target = StructureViewModel.Describe(foreignKey);
        OpenCommand = new AsyncRelayCommand(OpenAsync);
    }

    public string Name => _foreignKey.Name;
    public string Columns { get; }
    public string Target { get; }
    public ICommand OpenCommand { get; }

    public Task OpenAsync() => _openTable(_foreignKey.RefTable);
}

public sealed record StructureIndex(string Name, bool Unique, string Columns);
