using System.Data.Common;

namespace DbExplorer.Core.Drivers;

/// <summary>Parameterised catalog queries shared by the drivers' object browsing.</summary>
internal static class DriverQueries
{
    /// <summary>
    /// Opens the connection when needed and runs a catalog query: cancellable, no automatic timeout (a busy server must
    /// not fail the browse).
    /// </summary>
    public static async Task<List<T>> ReadAsync<T>(
        DbConnection c, string sql, Func<DbDataReader, T> map, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        if (c.State != System.Data.ConnectionState.Open) await c.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        var rows = new List<T>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(map(reader));
        return rows;
    }
}
