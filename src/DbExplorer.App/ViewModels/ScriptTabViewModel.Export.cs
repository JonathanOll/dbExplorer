using DbExplorer.Core.Export;

namespace DbExplorer.App.ViewModels;

public sealed partial class ScriptTabViewModel : IGridExportHost
{
    private ExportServices? _exportServices;

    /// <summary>Copy and export of the result grids; the shell sets it (without it, copies still report in the status bar).</summary>
    public ExportServices? ExportServices
    {
        get => _exportServices ??= new ExportServices(_status);
        set => _exportServices = value;
    }

    public ExportContext CreateExportContext(IReadOnlyList<ResultColumn> columns, IReadOnlyList<object?[]> rows, GridSlice? selection)
    {
        var set = Results.SelectMany(r => r.Sets).FirstOrDefault(s => ReferenceEquals(s.Columns, columns));
        var source = set?.Source;
        var name = FilePath is { } path ? Path.GetFileNameWithoutExtension(path) : BaseName;
        var safe = source is not null && QueryRows.CanRunAgain(source.Statement, source.Kind);
        return new ExportContext
        {
            Columns = columns.Select(c => new ExportColumn(c.Name, c.TypeName, c.ClrType)).ToList(),
            Rows = rows,
            Selection = selection,
            Kind = source?.Kind ?? Connection.Kind,
            BaseName = $"{name} result{(set is { Index: > 1 } ? " " + set.Index : "")}",
            OpenAllRows = safe ? ct => OpenAgainAsync(source!, ct) : null,
            AllRowsUnavailable = safe ? null
                : source is null ? "There is no query to run again for this grid: export the loaded rows."
                : QueryRows.NotSafeMessage + " Export the loaded rows instead.",
            AllRowsNote = "The statement runs again on a new read-only connection: temp tables, variables and uncommitted "
                          + "changes of this tab are not visible to it.",
        };
    }

    // The connection the result came from, even when the tab has moved to another database of its group since.
    private async Task<RowSource> OpenAgainAsync(ResultSource source, CancellationToken ct)
    {
        var session = await ResolveHook(source.ConnectionId, ct);
        if (session is null || session.Config.Id != source.ConnectionId)
            throw new ExportException("The connection this result came from is no longer available.");
        return QueryRows.FromStatement(session, source.Statement, source.SetIndex);
    }
}
