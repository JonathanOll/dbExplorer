using System.Data.Common;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Drivers;

// SQLite has views but no stored routines: only the view definition is implemented.
public sealed partial class SqliteDriver
{
    public async Task<string> LoadViewDefinitionAsync(DbConnection c, TableRef view, CancellationToken ct)
    {
        var rows = await DriverQueries.ReadAsync(c,
            "SELECT sql FROM sqlite_master WHERE type = 'view' AND name = $name",
            r => r.IsDBNull(0) ? null : r.GetString(0), ct, ("$name", view.Name));
        return rows.FirstOrDefault()
            ?? throw new DefinitionUnavailableException($"View {view} was not found: it was dropped or renamed. Refresh the connection.");
    }
}
