using DbExplorer.Core.Export;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>Which rows an export writes.</summary>
public enum ExportScope { Selection, Loaded, All }

/// <summary>What a grid offers to copy and export: its columns and rows, what is selected, and how to get all rows.</summary>
public sealed class ExportContext
{
    public required IReadOnlyList<ExportColumn> Columns { get; init; }

    /// <summary>The rows the grid shows, in its order.</summary>
    public required IReadOnlyList<object?[]> Rows { get; init; }

    /// <summary>The selected cells; null when nothing is selected.</summary>
    public GridSlice? Selection { get; init; }

    /// <summary>Dialect of the generated SQL.</summary>
    public DbKind Kind { get; init; } = DbKind.Sqlite;

    /// <summary>Table of the generated SQL; null when the rows do not come from one known table.</summary>
    public TableRef? Table { get; init; }

    /// <summary>Columns that identify a row (for UPDATE).</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>Start of the suggested file name.</summary>
    public string BaseName { get; init; } = "result";

    /// <summary>Opens the source of every row, with no row limit; null when that is not possible.</summary>
    public Func<CancellationToken, Task<RowSource>>? OpenAllRows { get; init; }

    /// <summary>Why <see cref="OpenAllRows"/> is null.</summary>
    public string? AllRowsUnavailable { get; init; }

    /// <summary>What the user should know about reading all rows again.</summary>
    public string? AllRowsNote { get; init; }

    /// <summary>
    /// The columns and rows of the selection or of the loaded rows. An UPDATE of a selection also gets the key columns,
    /// selected or not: without them it cannot say which row to change.
    /// </summary>
    public (IReadOnlyList<ExportColumn> Columns, IReadOnlyList<object?[]> Rows) Data(ExportScope scope, ExportKind kind)
    {
        if (scope != ExportScope.Selection || Selection is not { } slice) return (Columns, Rows);
        IEnumerable<int> picked = slice.Columns;
        if (kind == ExportKind.SqlUpdate)
            picked = picked.Union(Enumerable.Range(0, Columns.Count)
                .Where(i => KeyColumns.Contains(Columns[i].Name, StringComparer.OrdinalIgnoreCase))).Order();
        var columns = picked.ToList();
        var rows = slice.Rows.Where(r => r < Rows.Count)
            .Select(r => columns.Select(c => c < Rows[r].Length ? Rows[r][c] : null).ToArray())
            .ToList();
        return (columns.Select(c => Columns[c]).ToList(), rows);
    }
}

/// <summary>A tab whose grids can be copied and exported: it knows where their rows come from.</summary>
public interface IGridExportHost
{
    ExportServices? ExportServices { get; }

    ExportContext CreateExportContext(IReadOnlyList<ResultColumn> columns, IReadOnlyList<object?[]> rows, GridSlice? selection);
}

/// <summary>The dialogs of copy and export; implemented by the window layer, faked in tests.</summary>
public interface IExportDialogs
{
    /// <summary>"Advanced copy": the text to put on the clipboard, or null when cancelled.</summary>
    Task<string?> ShowAdvancedCopyAsync(AdvancedCopyViewModel viewModel);

    Task ShowExportAsync(ExportDialogViewModel viewModel);

    /// <summary>Asks where to write the file (<paramref name="extension"/> without the dot); null when cancelled.</summary>
    Task<string?> PickFileAsync(string suggestedName, string extension);
}

/// <summary>What copy and export need from the shell.</summary>
/// <param name="Remember">Saves the dialog choices; without it they last as long as this object.</param>
public sealed class ExportServices(
    StatusModel status, IExportDialogs? dialogs = null, Func<ExportPreferences>? preferences = null, Action<ExportPreferences>? remember = null)
{
    private ExportPreferences _own = new();

    public StatusModel Status { get; } = status;

    public IExportDialogs? Dialogs { get; } = dialogs;

    public ExportPreferences Preferences => preferences?.Invoke() ?? _own;

    public void Remember(ExportPreferences value)
    {
        if (value == Preferences) return;
        _own = value;
        remember?.Invoke(value);
    }
}
