using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using Npgsql;

namespace DbExplorer.Core.Tests;

/// <summary>
/// Creates two throw-away schemas in the database named by env var DBX_PG (a full Npgsql connection string).
/// Does nothing when the variable is not set, so the integration tests can be skipped.
/// </summary>
public sealed class PostgresDbFixture : IAsyncLifetime
{
    public static string? ConnectionString => Environment.GetEnvironmentVariable("DBX_PG");

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    public string SchemaA => "dbx_a_" + _suffix;
    public string SchemaB => "dbx_b_" + _suffix;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await using var c = new NpgsqlConnection(ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            CREATE SCHEMA "{SchemaA}";
            CREATE SCHEMA "{SchemaB}";
            CREATE TABLE "{SchemaA}".parents (
                id integer PRIMARY KEY,
                name varchar(50) NOT NULL,
                note text
            );
            CREATE TABLE "{SchemaA}".order_lines (
                order_id integer NOT NULL,
                line_no integer NOT NULL,
                product text,
                PRIMARY KEY (line_no, order_id)
            );
            CREATE TABLE "{SchemaB}".children (
                id integer PRIMARY KEY,
                parent_id integer REFERENCES "{SchemaA}".parents (id),
                order_id integer,
                line_no integer,
                FOREIGN KEY (order_id, line_no) REFERENCES "{SchemaA}".order_lines (order_id, line_no)
            );
            CREATE TABLE "{SchemaA}".included_pk (
                id integer,
                extra text,
                PRIMARY KEY (id) INCLUDE (extra)
            );
            CREATE TABLE "{SchemaA}".part_parent (
                id integer PRIMARY KEY,
                label text
            ) PARTITION BY RANGE (id);
            CREATE TABLE "{SchemaA}".part_p1 PARTITION OF "{SchemaA}".part_parent FOR VALUES FROM (0) TO (100);
            CREATE TABLE "{SchemaA}".part_p2 PARTITION OF "{SchemaA}".part_parent FOR VALUES FROM (100) TO (200);
            CREATE TABLE "{SchemaB}".refs_part (
                id integer PRIMARY KEY,
                part_id integer REFERENCES "{SchemaA}".part_parent (id)
            );
            CREATE UNIQUE INDEX ux_parents_name ON "{SchemaA}".parents (name);
            CREATE INDEX ix_children_lines ON "{SchemaB}".children (order_id, line_no);
            CREATE VIEW "{SchemaA}".v_parent_names AS SELECT id, name FROM "{SchemaA}".parents;
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await using var c = new NpgsqlConnection(ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"DROP SCHEMA IF EXISTS \"{SchemaB}\" CASCADE; DROP SCHEMA IF EXISTS \"{SchemaA}\" CASCADE;";
        await cmd.ExecuteNonQueryAsync();
    }
}

public class PostgresDriverTests : IClassFixture<PostgresDbFixture>
{
    private readonly PostgresDbFixture _db;
    private readonly IDbDriver _driver = new PostgresDriver();

    public PostgresDriverTests(PostgresDbFixture db) => _db = db;

    [Fact]
    public void Quote_DoublesQuotes()
    {
        Assert.Equal("\"plain\"", _driver.Quote("plain"));
        Assert.Equal("\"we\"\"ird\"", _driver.Quote("we\"ird"));
    }

    [Fact]
    public void QuoteTable_QualifiesWithSchema()
    {
        Assert.Equal("\"public\".\"users\"", _driver.QuoteTable(new TableRef("public", "users")));
        Assert.Equal("\"users\"", _driver.QuoteTable(new TableRef(null, "users")));
    }

    [Fact]
    public void PageSql_UsesLimitOffset()
    {
        Assert.Equal(
            "SELECT * FROM t ORDER BY \"id\" LIMIT 200 OFFSET 400",
            _driver.PageSql("SELECT * FROM t", "\"id\"", 400, 200));
        Assert.Equal(
            "SELECT * FROM t LIMIT 200 OFFSET 0",
            _driver.PageSql("SELECT * FROM t", "", 0, 200));
    }

    [Fact]
    public void PageSql_RejectsInvalidOffsetOrLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _driver.PageSql("SELECT 1", "", -1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => _driver.PageSql("SELECT 1", "", 0, 0));
    }

    [Fact]
    public void CastToText_UsesDoubleColon()
    {
        Assert.Equal("\"c\"::text", _driver.CastToText("\"c\""));
    }

    [Fact]
    public void EscapeLike_EscapesWildcardsAndBackslash()
    {
        Assert.Equal("100\\%\\_\\\\", _driver.EscapeLike("100%_\\"));
    }

    [Fact]
    public void CreateConnection_BuildsConnectionString()
    {
        var cfg = new ConnectionConfig(
            Guid.NewGuid(), "pg", DbKind.Postgres, null,
            "db.example.com", 5433, "backoffice", "alice", null, false, false, false);

        using var conn = _driver.CreateConnection(cfg, "s3cret;pwd");

        var b = new NpgsqlConnectionStringBuilder(conn.ConnectionString);
        Assert.Equal("db.example.com", b.Host);
        Assert.Equal(5433, b.Port);
        Assert.Equal("backoffice", b.Database);
        Assert.Equal("alice", b.Username);
        Assert.Equal("s3cret;pwd", b.Password);
        Assert.Equal(10, b.Timeout);
        Assert.Equal(System.Data.ConnectionState.Closed, conn.State);
    }

    [Fact]
    public void CreateConnection_DefaultsPortTo5432()
    {
        var cfg = new ConnectionConfig(
            Guid.NewGuid(), "pg", DbKind.Postgres, null,
            "localhost", null, "backoffice", "alice", null, false, false, false);

        using var conn = _driver.CreateConnection(cfg, null);

        Assert.Equal(5432, new NpgsqlConnectionStringBuilder(conn.ConnectionString).Port);
    }

    [Fact]
    public void DriverFactory_ReturnsPostgresDriver()
    {
        Assert.IsType<PostgresDriver>(DriverFactory.For(DbKind.Postgres));
    }

    private async Task<IReadOnlyList<TableInfo>> LoadAsync()
    {
        var env = new NpgsqlConnectionStringBuilder(PostgresDbFixture.ConnectionString);
        using var conn = _driver.CreateConnection(ConfigFromEnv(env), env.Password);
        await conn.OpenAsync();
        return await _driver.LoadSchemaAsync(conn, CancellationToken.None);
    }

    private static ConnectionConfig ConfigFromEnv(NpgsqlConnectionStringBuilder b)
    {
        // DBX_PG is a full connection string; the driver only needs host/port/db/user (+ password) from it.
        return new ConnectionConfig(
            Guid.NewGuid(), "it", DbKind.Postgres, null,
            b.Host, b.Port, b.Database!, b.Username, null, false, false, false);
    }

    [SkippableFact]
    public async Task LoadSchema_ReturnsCrossSchemaForeignKey()
    {
        Skip.If(string.IsNullOrWhiteSpace(PostgresDbFixture.ConnectionString), "DBX_PG not set");

        var tables = await LoadAsync();

        var children = tables.Single(t => t.Ref == new TableRef(_db.SchemaB, "children"));
        Assert.False(children.IsView);
        var single = children.ForeignKeys.Single(f => f.Columns.SequenceEqual(new[] { "parent_id" }));
        Assert.Equal(new TableRef(_db.SchemaA, "parents"), single.RefTable);
        Assert.Equal(new[] { "id" }, single.RefColumns);
        var composite = children.ForeignKeys.Single(f => f.Columns.Count == 2);
        Assert.Equal(new[] { "order_id", "line_no" }, composite.Columns);
        Assert.Equal(new TableRef(_db.SchemaA, "order_lines"), composite.RefTable);
        Assert.Equal(new[] { "order_id", "line_no" }, composite.RefColumns);

        Assert.DoesNotContain(tables, t => t.Ref.Schema is "pg_catalog" or "information_schema");
    }

    [SkippableFact]
    public async Task LoadSchema_ReturnsCompositePk()
    {
        Skip.If(string.IsNullOrWhiteSpace(PostgresDbFixture.ConnectionString), "DBX_PG not set");

        var tables = await LoadAsync();

        var lines = tables.Single(t => t.Ref == new TableRef(_db.SchemaA, "order_lines"));
        Assert.Equal(new[] { "order_id", "line_no", "product" }, lines.Columns.Select(c => c.Name));
        Assert.Equal(new[] { 0, 1, 2 }, lines.Columns.Select(c => c.Ordinal));
        Assert.Equal(new[] { "order_id", "line_no" }, lines.Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name));
        Assert.All(lines.Columns.Where(c => c.IsPrimaryKey), c => Assert.False(c.Nullable));
        Assert.Equal(typeof(int), lines.Columns[0].ClrType);
        Assert.Equal("integer", lines.Columns[0].DataType);
        Assert.True(lines.Columns[2].Nullable);
        Assert.Equal(typeof(string), lines.Columns[2].ClrType);
    }

    [SkippableFact]
    public async Task LoadSchema_PkIncludeColumnsAreNotPrimaryKey()
    {
        Skip.If(string.IsNullOrWhiteSpace(PostgresDbFixture.ConnectionString), "DBX_PG not set");

        var tables = await LoadAsync();

        var t = tables.Single(x => x.Ref == new TableRef(_db.SchemaA, "included_pk"));
        Assert.Equal(new[] { "id" }, t.Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name));
    }

    [SkippableFact]
    public async Task LoadSchema_ForeignKeyToPartitionedTable_IsReportedOnce()
    {
        Skip.If(string.IsNullOrWhiteSpace(PostgresDbFixture.ConnectionString), "DBX_PG not set");

        var tables = await LoadAsync();

        var refs = tables.Single(x => x.Ref == new TableRef(_db.SchemaB, "refs_part"));
        var fk = Assert.Single(refs.ForeignKeys);
        Assert.Equal(new[] { "part_id" }, fk.Columns);
        Assert.Equal(new TableRef(_db.SchemaA, "part_parent"), fk.RefTable);
        Assert.Equal(new[] { "id" }, fk.RefColumns);
    }

    [SkippableFact]
    public async Task LoadSchema_ReturnsViewsAndIndexes()
    {
        Skip.If(string.IsNullOrWhiteSpace(PostgresDbFixture.ConnectionString), "DBX_PG not set");

        var tables = await LoadAsync();

        var view = tables.Single(t => t.Ref == new TableRef(_db.SchemaA, "v_parent_names"));
        Assert.True(view.IsView);
        Assert.Equal(new[] { "id", "name" }, view.Columns.Select(c => c.Name));

        var parents = tables.Single(t => t.Ref == new TableRef(_db.SchemaA, "parents"));
        var unique = Assert.Single(parents.Indexes);   // the primary key's index is not listed
        Assert.Equal("ux_parents_name", unique.Name);
        Assert.True(unique.Unique);
        Assert.Equal(new[] { "name" }, unique.Columns);
        Assert.Equal("character varying(50)", parents.Columns.Single(c => c.Name == "name").DataType);

        var children = tables.Single(t => t.Ref == new TableRef(_db.SchemaB, "children"));
        var composite = Assert.Single(children.Indexes);
        Assert.False(composite.Unique);
        Assert.Equal(new[] { "order_id", "line_no" }, composite.Columns);
    }
}
