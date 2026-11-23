using DbExplorer.Core.Model;

namespace DbExplorer.Core.Drivers;

public static class DriverFactory
{
    private static readonly SqliteDriver Sqlite = new();

    private static readonly SqlServerDriver SqlServer = new();

    private static readonly PostgresDriver Postgres = new();

    public static IDbDriver For(DbKind kind) => kind switch
    {
        DbKind.Sqlite => Sqlite,
        DbKind.SqlServer => SqlServer,
        DbKind.Postgres => Postgres,
        _ => throw new NotSupportedException($"No driver available for {kind}."),
    };
}
