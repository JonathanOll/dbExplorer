using DbExplorer.Core.Model;

namespace DbExplorer.Core.Drivers;

public static class DriverFactory
{
    private static readonly SqliteDriver Sqlite = new();

    private static readonly SqlServerDriver SqlServer = new();

    private static readonly PostgresDriver Postgres = new();

    private static readonly MySqlDriver MySql = new();

    public static IDbDriver For(DbKind kind) => kind switch
    {
        DbKind.Sqlite => Sqlite,
        DbKind.SqlServer => SqlServer,
        DbKind.Postgres => Postgres,
        DbKind.MySql => MySql,
        _ => throw new NotSupportedException($"No driver available for {kind}."),
    };
}
