using DbExplorer.Core.Export;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// Copy and export of the grid on screen, for callers outside it (the command palette). The grid implements it: only it knows
/// which cells are selected.
/// </summary>
public interface IGridCommands
{
    /// <summary>There is something "Copy" would take: a current cell, rows, cells or columns.</summary>
    bool HasCurrentCellOrSelection { get; }

    /// <summary>"Advanced copy" and "Export" can open their dialogs.</summary>
    bool HasExportDialogs { get; }

    /// <summary>The rows have a known key, so they can be written as UPDATE statements.</summary>
    bool HasKeyColumns { get; }

    /// <summary>Plain copy of the selection; with <paramref name="header"/>, every loaded row when nothing is selected.</summary>
    Task CopyAsync(bool header);

    Task CopyAsAsync(ExportKind kind);

    Task AdvancedCopyAsync();

    Task ExportAsync();
}

/// <summary>What the cell menu and the command palette both offer.</summary>
public static class ResultFormats
{
    public const string NoKeyReason = "No known key columns: UPDATE is offered on tables with a key";

    public static readonly IReadOnlyList<ExportKind> CopyAs =
    [
        ExportKind.Csv, ExportKind.Tsv, ExportKind.Json, ExportKind.Markdown, ExportKind.SqlInsert, ExportKind.SqlUpdate,
        ExportKind.InList, ExportKind.ColumnNames,
    ];
}
