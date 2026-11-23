using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace DbExplorer.Core.Sessions;

/// <summary>Closes the pooled physical connections behind a connection string (provider types stay in Core).</summary>
internal static class ConnectionPools
{
    public static void Clear(DbConnection connection)
    {
        switch (connection)
        {
            case NpgsqlConnection pg: NpgsqlConnection.ClearPool(pg); break;
            case SqlConnection sql: SqlConnection.ClearPool(sql); break;
            case SqliteConnection lite: SqliteConnection.ClearPool(lite); break;
        }
    }
}
