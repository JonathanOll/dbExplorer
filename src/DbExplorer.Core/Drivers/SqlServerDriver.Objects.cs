using System.Data.Common;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Drivers;

public sealed partial class SqlServerDriver
{
    // sys.objects.type: P procedure, PC CLR procedure; FN scalar, IF inline table, TF table, FS / FT CLR functions.
    private const string RoutineTypes = "'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT'";

    public bool HasRoutines => true;

    public async Task<IReadOnlyList<RoutineInfo>> LoadRoutinesAsync(DbConnection c, CancellationToken ct) =>
        await DriverQueries.ReadAsync(c,
            "SELECT s.name, o.name, RTRIM(o.type) FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id " +
            $"WHERE o.is_ms_shipped = 0 AND o.type IN ({RoutineTypes}) ORDER BY s.name, o.name",
            r => new RoutineInfo(new TableRef(r.GetString(0), r.GetString(1)),
                r.GetString(2) is "P" or "PC" ? RoutineKind.Procedure : RoutineKind.Function), ct);

    public Task<string> LoadViewDefinitionAsync(DbConnection c, TableRef view, CancellationToken ct) =>
        LoadModuleAsync(c, view, "'V'", ct);

    public Task<string> LoadRoutineDefinitionAsync(DbConnection c, RoutineInfo routine, CancellationToken ct) =>
        LoadModuleAsync(c, routine.Ref, RoutineTypes, ct);

    private static async Task<string> LoadModuleAsync(DbConnection c, TableRef name, string types, CancellationToken ct)
    {
        var rows = await DriverQueries.ReadAsync(c,
            "SELECT m.definition, CAST(OBJECTPROPERTY(o.object_id, 'IsEncrypted') AS int), RTRIM(o.type) " +
            "FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id " +
            "LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id " +
            $"WHERE s.name = @schema AND o.name = @name AND o.type IN ({types})",
            r => (Definition: r.IsDBNull(0) ? null : r.GetString(0),
                Encrypted: !r.IsDBNull(1) && r.GetInt32(1) == 1,
                Type: r.GetString(2)), ct, ("@schema", name.Schema ?? "dbo"), ("@name", name.Name));
        // SQL Server hides objects one has no right on, so "gone" and "not allowed" look the same.
        if (rows.Count == 0)
            throw new DefinitionUnavailableException(
                $"{name} was not found: it was dropped or renamed, or you have no permission to see it. Refresh the connection.");
        var (definition, encrypted, type) = rows[0];
        return definition ?? throw new DefinitionUnavailableException(MissingDefinition(name, encrypted, type));
    }

    /// <summary>Why sys.sql_modules has no text for an object that exists.</summary>
    internal static string MissingDefinition(TableRef name, bool encrypted, string type) =>
        encrypted ? $"{name} is encrypted (WITH ENCRYPTION): SQL Server does not give its definition."
        : type is "PC" or "FS" or "FT" ? $"{name} is a CLR routine: it has no SQL definition."
        : $"No permission to read the definition of {name} (VIEW DEFINITION is required).";
}
