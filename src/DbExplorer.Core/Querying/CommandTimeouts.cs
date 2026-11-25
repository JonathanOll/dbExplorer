using System.Data.Common;

namespace DbExplorer.Core.Querying;

internal static class CommandTimeouts
{
    /// <summary>
    /// No automatic timeout on a user-facing command: it ends, or the user cancels it (SqlClient and Npgsql default to 30 s).
    /// SQLite is left alone: there <see cref="DbCommand.CommandTimeout"/> is the lock wait, and 0 would fail at once with "database is locked".
    /// </summary>
    public static void Unbounded(DbCommand cmd)
    {
        if (cmd.Connection is Microsoft.Data.Sqlite.SqliteConnection) return;
        cmd.CommandTimeout = 0;
    }
}
