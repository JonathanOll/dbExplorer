using DbExplorer.Core.Drivers;
using DbExplorer.Core.Metadata;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace DbExplorer.Core.Tests;

public sealed class ObjectDefinitionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-objects-" + Guid.NewGuid().ToString("N"));

    public ObjectDefinitionTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private Session SqliteSession(string sql)
    {
        var file = Path.Combine(_dir, "views.db");
        using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        var config = new ConnectionConfig(Guid.NewGuid(), "t", DbKind.Sqlite, null, null, null, file, null, null, false, false, true);
        return new Session(config, new SqliteDriver(), []);
    }

    [Fact]
    public async Task Sqlite_ViewDefinition_IsTheTextAsWritten_OnAReadOnlyConnection()
    {
        using var session = SqliteSession(
            "CREATE TABLE t (id INTEGER); CREATE VIEW \"My view\" AS\n  -- why\n  SELECT id FROM t");

        var sql = await session.LoadViewDefinitionAsync(new TableRef(null, "My view"), CancellationToken.None);

        Assert.Equal("CREATE VIEW \"My view\" AS\n  -- why\n  SELECT id FROM t", sql);
    }

    [Fact]
    public async Task Sqlite_MissingView_SaysSo_AndATableIsNotAView()
    {
        using var session = SqliteSession("CREATE TABLE t (id INTEGER)");

        var e = await Assert.ThrowsAsync<DefinitionUnavailableException>(
            () => session.LoadViewDefinitionAsync(new TableRef(null, "t"), CancellationToken.None));

        Assert.Contains("not found", e.Message);
    }

    [Fact]
    public async Task Sqlite_HasNoRoutines()
    {
        using var session = SqliteSession("CREATE TABLE t (id INTEGER)");

        Assert.False(session.Driver.HasRoutines);
        Assert.Empty(await session.LoadRoutinesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Definition_HonoursCancellation_AndAClosedSession()
    {
        var session = SqliteSession("CREATE VIEW v AS SELECT 1");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.LoadViewDefinitionAsync(new TableRef(null, "v"), new CancellationToken(true)));

        session.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.LoadViewDefinitionAsync(new TableRef(null, "v"), CancellationToken.None));
    }

    [Fact]
    public void RoutineCache_RoundTrips_AndIsDeletedWithTheConnection()
    {
        var cache = new MetadataCache(_dir);
        var id = Guid.NewGuid();
        RoutineInfo[] routines =
        [
            new(new TableRef("public", "f"), RoutineKind.Function, "integer", 12),
            new(new TableRef("dbo", "p"), RoutineKind.Procedure),
        ];

        Assert.Empty(cache.LoadRoutines(id));
        cache.SaveRoutines(id, routines);
        Assert.Equal(routines, cache.LoadRoutines(id));
        cache.Delete(id);
        Assert.Empty(cache.LoadRoutines(id));
    }

    [Fact]
    public void SqlServer_MissingDefinition_TellsWhy()
    {
        var name = new TableRef("dbo", "p");

        Assert.Contains("encrypted", SqlServerDriver.MissingDefinition(name, true, "P"));
        Assert.Contains("CLR", SqlServerDriver.MissingDefinition(name, false, "PC"));
        Assert.Contains("VIEW DEFINITION", SqlServerDriver.MissingDefinition(name, false, "P"));
    }

    [Fact]
    public void Postgres_ViewStatement_AddsTheCreateLine()
    {
        var driver = new PostgresDriver();

        Assert.Equal("CREATE OR REPLACE VIEW \"s\".\"v\" AS\n SELECT 1;", driver.ViewStatement(new TableRef("s", "v"), false, " SELECT 1;"));
        Assert.StartsWith("CREATE MATERIALIZED VIEW ", driver.ViewStatement(new TableRef("s", "m"), true, " SELECT 1;"));
    }

    // ---- opt-in integration tests (docs/development.md) ----

    [SkippableFact]
    public async Task SqlServer_ListsRoutines_AndReadsDefinitions_EncryptedOnesSayWhy()
    {
        var cs = Environment.GetEnvironmentVariable("DBX_MSSQL");
        Skip.If(string.IsNullOrWhiteSpace(cs), "Set DBX_MSSQL to a SQL Server connection string to run this test.");
        IDbDriver driver = new SqlServerDriver();
        var schema = "dbx_o_" + Guid.NewGuid().ToString("N")[..8];
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        async Task Run(string sql)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            await Run($"EXEC('CREATE SCHEMA [{schema}]')");
            await Run($"EXEC('CREATE VIEW [{schema}].[v] AS SELECT 1 AS one')");
            await Run($"EXEC('CREATE PROCEDURE [{schema}].[p] AS SELECT 1')");
            await Run($"EXEC('CREATE FUNCTION [{schema}].[f]() RETURNS int AS BEGIN RETURN 1 END')");
            await Run($"EXEC('CREATE PROCEDURE [{schema}].[secret] WITH ENCRYPTION AS SELECT 1')");

            var routines = (await driver.LoadRoutinesAsync(c, CancellationToken.None)).Where(r => r.Ref.Schema == schema).ToList();
            Assert.Equal(["f", "p", "secret"], routines.Select(r => r.Ref.Name).Order());
            Assert.Equal(RoutineKind.Function, routines.Single(r => r.Ref.Name == "f").Kind);
            Assert.Contains("CREATE VIEW", await driver.LoadViewDefinitionAsync(c, new TableRef(schema, "v"), CancellationToken.None));
            Assert.Contains("CREATE PROCEDURE", await driver.LoadRoutineDefinitionAsync(c, routines.Single(r => r.Ref.Name == "p"), CancellationToken.None));
            var e = await Assert.ThrowsAsync<DefinitionUnavailableException>(
                () => driver.LoadRoutineDefinitionAsync(c, routines.Single(r => r.Ref.Name == "secret"), CancellationToken.None));
            Assert.Contains("encrypted", e.Message);
        }
        finally
        {
            await Run($"""
                DROP VIEW IF EXISTS [{schema}].[v];
                DROP PROCEDURE IF EXISTS [{schema}].[p];
                DROP PROCEDURE IF EXISTS [{schema}].[secret];
                DROP FUNCTION IF EXISTS [{schema}].[f];
                DROP SCHEMA IF EXISTS [{schema}];
                """);
        }
    }

    [SkippableFact]
    public async Task Postgres_ListsOverloads_AndReadsViewAndFunctionDefinitions()
    {
        var cs = Environment.GetEnvironmentVariable("DBX_PG");
        Skip.If(string.IsNullOrWhiteSpace(cs), "Set DBX_PG to a PostgreSQL connection string to run this test.");
        IDbDriver driver = new PostgresDriver();
        var schema = "dbx_o_" + Guid.NewGuid().ToString("N")[..8];
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync();
        async Task Run(string sql)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            await Run($"""
                CREATE SCHEMA {schema};
                CREATE VIEW {schema}.v AS SELECT 1 AS one;
                CREATE MATERIALIZED VIEW {schema}.m AS SELECT 1 AS one;
                CREATE FUNCTION {schema}.f(a integer) RETURNS integer LANGUAGE sql AS 'SELECT a';
                CREATE FUNCTION {schema}.f(a text) RETURNS text LANGUAGE sql AS 'SELECT a';
                CREATE PROCEDURE {schema}.p() LANGUAGE sql AS 'SELECT 1';
                """);

            var routines = (await driver.LoadRoutinesAsync(c, CancellationToken.None)).Where(r => r.Ref.Schema == schema).ToList();
            Assert.Equal(3, routines.Count);
            Assert.Equal(RoutineKind.Procedure, routines.Single(r => r.Ref.Name == "p").Kind);
            var text = routines.Single(r => r.Ref.Name == "f" && r.Arguments!.Contains("text"));
            Assert.Contains("RETURNS text", await driver.LoadRoutineDefinitionAsync(c, text, CancellationToken.None));
            Assert.StartsWith("CREATE OR REPLACE VIEW", await driver.LoadViewDefinitionAsync(c, new TableRef(schema, "v"), CancellationToken.None));
            Assert.StartsWith("CREATE MATERIALIZED VIEW", await driver.LoadViewDefinitionAsync(c, new TableRef(schema, "m"), CancellationToken.None));
        }
        finally
        {
            await Run($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }
}
