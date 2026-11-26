using DbExplorer.Core.Export;

namespace DbExplorer.App.ViewModels;

public sealed partial class TableTabViewModel : IGridExportHost
{
    /// <summary>Copy and export of the grid; set by the shell.</summary>
    public ExportServices? ExportServices { get; set; }

    public ExportContext CreateExportContext(IReadOnlyList<ResultColumn> columns, IReadOnlyList<object?[]> rows, GridSlice? selection)
    {
        var applied = _applied;
        var info = Info;
        var session = _session;
        var query = applied is null || info is null ? null : ToQuery(info, applied, 0);
        var key = KeyColumns?.Where(k => k != "*").ToList();
        return new ExportContext
        {
            Columns = columns.Select(c => new ExportColumn(c.Name, c.TypeName, c.ClrType)).ToList(),
            Rows = rows,
            Selection = selection,
            Kind = session.Driver.Kind,
            Table = info?.Ref ?? Table,
            KeyColumns = key is { Count: > 0 } ? key : columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList(),
            BaseName = Table.Name,
            // The filters, WHERE and sort of the rows on screen, not what is typed and not applied yet.
            OpenAllRows = query is null ? null : _ => Task.FromResult(QueryRows.FromTable(session, query)),
            AllRowsUnavailable = query is null ? "Load the table first." : null,
            AllRowsNote = "All rows are read from the database with the applied filters and sort"
                          + (HasPendingEdits ? ": pending changes are not included." : "."),
        };
    }
}
