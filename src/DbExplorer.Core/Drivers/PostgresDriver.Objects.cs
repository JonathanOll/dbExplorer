using System.Data.Common;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Drivers;

public sealed partial class PostgresDriver
{
    // prokind: f function, p procedure, w window function. Aggregates (a) are left out: pg_get_functiondef refuses them.
    // Functions installed by an extension (deptype 'e') are not the database's own code and can number in thousands.
    private const string UserRoutines =
        "pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
        "WHERE p.prokind IN ('f', 'p', 'w') AND " + UserSchemaFilter + " " +
        "AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_catalog.pg_proc'::regclass " +
        "AND d.objid = p.oid AND d.deptype = 'e')";

    public bool HasRoutines => true;

    public async Task<IReadOnlyList<RoutineInfo>> LoadRoutinesAsync(DbConnection c, CancellationToken ct) =>
        await DriverQueries.ReadAsync(c,
            "SELECT n.nspname, p.proname, p.prokind = 'p', pg_get_function_identity_arguments(p.oid), p.oid::bigint " +
            $"FROM {UserRoutines} ORDER BY 1, 2, 4",
            r => new RoutineInfo(new TableRef(r.GetString(0), r.GetString(1)),
                r.GetBoolean(2) ? RoutineKind.Procedure : RoutineKind.Function, r.GetString(3), r.GetInt64(4)), ct);

    public async Task<string> LoadViewDefinitionAsync(DbConnection c, TableRef view, CancellationToken ct)
    {
        var rows = await DriverQueries.ReadAsync(c,
            "SELECT c.relkind = 'm', pg_get_viewdef(c.oid, true) FROM pg_class c " +
            "JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = @schema AND c.relname = @name AND c.relkind IN ('v', 'm')",
            r => (Materialized: r.GetBoolean(0), Body: r.IsDBNull(1) ? null : r.GetString(1)), ct,
            ("@schema", view.Schema ?? "public"), ("@name", view.Name));
        if (rows.Count == 0 || rows[0].Body is not { } body)
            throw new DefinitionUnavailableException($"View {view} was not found: it was dropped or renamed. Refresh the connection.");
        return ViewStatement(view, rows[0].Materialized, body);
    }

    /// <summary>pg_get_viewdef gives only the query: the CREATE line is added so the text reads as a statement.</summary>
    internal string ViewStatement(TableRef view, bool materialized, string body) =>
        (materialized ? "CREATE MATERIALIZED VIEW " : "CREATE OR REPLACE VIEW ") + QuoteTable(view) + " AS\n" + body;

    public async Task<string> LoadRoutineDefinitionAsync(DbConnection c, RoutineInfo routine, CancellationToken ct)
    {
        // By oid: the name alone does not tell overloads apart.
        var rows = await DriverQueries.ReadAsync(c,
            "SELECT pg_get_functiondef(p.oid) FROM pg_proc p WHERE p.oid = CAST(@id AS oid)",
            r => r.IsDBNull(0) ? null : r.GetString(0), ct, ("@id", routine.Id));
        return rows.FirstOrDefault() ?? throw new DefinitionUnavailableException(
            $"{routine.Ref}({routine.Arguments}) was not found: it was dropped or replaced. Refresh the connection.");
    }
}
