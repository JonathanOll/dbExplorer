using System.Data.Common;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Querying;

/// <summary>
/// Creates command parameters from <see cref="SqlParam"/>s. A date/time value is bound with the exact database type of its
/// column (SQL Server <c>datetime2(n)</c>, PostgreSQL <c>timestamptz</c>...) because the providers' inference is wrong for
/// them: SqlClient binds every DateTime as the legacy <c>datetime</c> (3 ms resolution), Npgsql binds an unspecified-kind
/// DateTime as <c>timestamp</c>, which the server then reads in its session time zone.
/// </summary>
public static class ParameterTyping
{
    public static DbParameter Create(DbCommand cmd, SqlParam spec)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = spec.Name;
        parameter.Value = spec.Value ?? DBNull.Value;
        if (spec.Column is { } column) Configure(parameter, column);
        return parameter;
    }

    /// <summary>
    /// Gives <paramref name="parameter"/> the database type of <paramref name="column"/>. Parameters of other providers
    /// (SQLite) are left alone. Provider types are matched by name so this method never loads a provider assembly that is
    /// not in use; the provider-specific code lives in the drivers.
    /// </summary>
    public static void Configure(DbParameter parameter, ColumnInfo column)
    {
        switch (parameter.GetType().FullName)
        {
            case "Microsoft.Data.SqlClient.SqlParameter":
                SqlServerDriver.ConfigureParameter(parameter, column);
                break;
            case "Npgsql.NpgsqlParameter":
                PostgresDriver.ConfigureParameter(parameter, column);
                break;
        }
    }
}
