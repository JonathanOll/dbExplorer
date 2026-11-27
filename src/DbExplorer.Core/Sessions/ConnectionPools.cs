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
            // By name first: a connection of another engine must not load the MySQL provider.
            default:
                if (connection.GetType().Namespace == "MySqlConnector") ClearMySql(connection);
                break;
        }
    }

    private static void ClearMySql(DbConnection connection) =>
        MySqlConnector.MySqlConnection.ClearPool((MySqlConnector.MySqlConnection)connection);
}
