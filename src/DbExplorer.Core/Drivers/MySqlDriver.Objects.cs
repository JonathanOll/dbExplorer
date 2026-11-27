using System.Data.Common;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Drivers;

public sealed partial class MySqlDriver
{
    public bool HasRoutines => true;

    public async Task<IReadOnlyList<RoutineInfo>> LoadRoutinesAsync(DbConnection c, CancellationToken ct) =>
        await DriverQueries.ReadAsync(c,
            "SELECT ROUTINE_NAME, ROUTINE_TYPE FROM information_schema.ROUTINES " +
            "WHERE ROUTINE_SCHEMA = DATABASE() AND ROUTINE_TYPE IN ('PROCEDURE', 'FUNCTION') ORDER BY ROUTINE_NAME, ROUTINE_TYPE",
            r => new RoutineInfo(new TableRef(null, Text(r, 0)!),
                Text(r, 1) == "PROCEDURE" ? RoutineKind.Procedure : RoutineKind.Function), ct);

    public Task<string> LoadViewDefinitionAsync(DbConnection c, TableRef view, CancellationToken ct) =>
        ShowCreateAsync(c, "VIEW", view, "Create View", ct);

    public Task<string> LoadRoutineDefinitionAsync(DbConnection c, RoutineInfo routine, CancellationToken ct) =>
        routine.Kind == RoutineKind.Procedure
            ? ShowCreateAsync(c, "PROCEDURE", routine.Ref, "Create Procedure", ct)
            : ShowCreateAsync(c, "FUNCTION", routine.Ref, "Create Function", ct);

    // SHOW takes no parameters: the name is quoted by the driver.
    private async Task<string> ShowCreateAsync(DbConnection c, string what, TableRef name, string column, CancellationToken ct)
    {
        List<string?> rows;
        try
        {
            rows = await DriverQueries.ReadAsync(c, $"SHOW CREATE {what} {QuoteTable(name)}",
                r => Text(r, r.GetOrdinal(column)), ct);
        }
        catch (DbException e) when (!ct.IsCancellationRequested)
        {
            throw new DefinitionUnavailableException($"The definition of {name} cannot be read: {e.Message}");
        }
        // NULL: the routine exists but the user has no right on its body.
        return rows.FirstOrDefault() ?? throw new DefinitionUnavailableException(
            $"No permission to read the definition of {name} (SHOW_ROUTINE, or being its definer, is required).");
    }
}
