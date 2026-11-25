using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;

namespace DbExplorer.App.ViewModels;

/// <summary>Cancel is first on purpose: closing the dialog window with the X yields the default value, which must never save or discard.</summary>
public enum SaveChoice { Cancel, Save, Discard }

/// <summary>Modal interactions of the staged-editing flow; implemented by the window layer, faked in tests.</summary>
public interface IEditDialogs
{
    /// <summary>Pending edits are about to be lost: Save (goes through the SQL preview), Discard or Cancel.</summary>
    Task<SaveChoice> AskSaveDiscardAsync(string title, string message);

    /// <summary>
    /// Shows the statements that would run (display only, never executed); true when the user chose Apply.
    /// <paramref name="target"/> names where they would run (connection, server and database) and is shown with them.
    /// </summary>
    Task<bool> ShowSqlPreviewAsync(IReadOnlyList<string> statements, string target);

    /// <summary>Shows the virtual key dialog; the chosen key columns (<c>["*"]</c> = all), or null when cancelled.</summary>
    Task<string[]?> ShowVirtualKeyDialogAsync(VirtualKeyDialogViewModel viewModel);

    /// <summary>
    /// Shows the "Save script" dialog (name and library folder); true when the user confirmed or chose another location (the view
    /// model then says which). Implementations without the dialog keep the default: the save falls back to the file picker.
    /// </summary>
    Task<bool> ShowSaveScriptAsync(SaveScriptViewModel viewModel) => Task.FromResult(false);
}

/// <summary>One selectable column of the virtual key dialog.</summary>
public sealed class VirtualKeyColumn(string name, string dataType, bool isKeyable, bool isSelected, bool inUniqueIndex = false)
{
    public string Name { get; } = name;
    public string DataType { get; } = dataType;

    /// <summary>False for types the engine cannot compare with "=" (e.g. SQL Server text/ntext/image/xml).</summary>
    public bool IsKeyable { get; } = isKeyable;

    public bool IsSelected { get; set; } = isSelected;

    /// <summary>The column is part of a unique index: a hint that it identifies rows safely.</summary>
    public bool InUniqueIndex { get; } = inUniqueIndex;

    public string Label => IsKeyable ? $"{Name}  ({DataType})" : $"{Name}  ({DataType}) - cannot be used in a key";
}

/// <summary>
/// State of the "Define key" dialog: which columns identify a row of a table that has no primary key. Pure logic,
/// the window only binds to it. <see cref="Result"/> is what gets persisted as the virtual key.
/// </summary>
public sealed class VirtualKeyDialogViewModel
{
    public const string Warning =
        "A key that is not unique can match several rows. Saves are rolled back when an edited or deleted row " +
        "matches more than one row, so choose columns that are unique per row.";

    public VirtualKeyDialogViewModel(TableInfo table, DbKind kind, IReadOnlyList<string>? current)
    {
        Table = table.Ref;
        var selected = current is null ? [] : current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unique = table.Indexes.Where(i => i.Unique).SelectMany(i => i.Columns).ToHashSet(StringComparer.OrdinalIgnoreCase);
        UseAllColumns = current is not null && selected.Contains("*");
        Columns = table.Columns.OrderBy(c => c.Ordinal)
            .Select(c =>
            {
                var keyable = IsKeyable(c, kind);
                return new VirtualKeyColumn(c.Name, c.DataType, keyable, keyable && !UseAllColumns && selected.Contains(c.Name), unique.Contains(c.Name));
            })
            .ToList();
    }

    public TableRef Table { get; }

    public IReadOnlyList<VirtualKeyColumn> Columns { get; }

    /// <summary>"All columns": every column that can be compared; unusable ones are left out.</summary>
    public bool UseAllColumns { get; set; }

    /// <summary>Some column is part of a unique index (those columns are marked in the dialog).</summary>
    public bool HasUniqueIndexColumns => Columns.Any(c => c.InUniqueIndex);

    public bool HasExcludedColumns => Columns.Any(c => !c.IsKeyable);

    public string? ExcludedNote => HasExcludedColumns
        ? "Columns that cannot be compared with \"=\" on this engine (" +
          string.Join(", ", Columns.Where(c => !c.IsKeyable).Select(c => c.Name)) + ") are never part of a key."
        : null;

    /// <summary>Why <see cref="Result"/> cannot be built (nothing chosen), or null.</summary>
    public string? Error
    {
        get
        {
            var keyable = Columns.Where(c => c.IsKeyable).ToList();
            if (UseAllColumns) return keyable.Count == 0 ? "No column of this table can be used in a key." : null;
            return keyable.Any(c => c.IsSelected) ? null : "Select at least one column.";
        }
    }

    /// <summary>
    /// <c>["*"]</c> when "all columns" is chosen and every column can be compared; with "all columns" and some
    /// unusable columns, the explicit list of the usable ones (the "*" shortcut would include them); otherwise the
    /// selected columns. Null while <see cref="Error"/> is set.
    /// </summary>
    public string[]? Result
    {
        get
        {
            if (Error is not null) return null;
            if (UseAllColumns)
                return HasExcludedColumns ? Columns.Where(c => c.IsKeyable).Select(c => c.Name).ToArray() : ["*"];
            return Columns.Where(c => c.IsKeyable && c.IsSelected).Select(c => c.Name).ToArray();
        }
    }

    /// <summary>Types the engine has no usable equality for (see <see cref="KeyResolver.IsKeyable"/>).</summary>
    public static bool IsKeyable(ColumnInfo column, DbKind kind) => KeyResolver.IsKeyable(column, kind);
}
