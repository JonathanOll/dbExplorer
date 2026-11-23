using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using Microsoft.Data.SqlClient;

namespace DbExplorer.Core.Tests;

public class SqlServerDriverTests
{
    private readonly IDbDriver _driver = new SqlServerDriver();

    private static ConnectionConfig Config(
        string? host = "db.example.com", int? port = 1433, bool encrypt = true, bool trust = false) => new(
        Guid.NewGuid(), "test", DbKind.SqlServer, null, host, port,
        "n2f", "app_user", null, encrypt, trust, false);

    [Fact]
    public void Kind_And_ParamPrefix()
    {
        Assert.Equal(DbKind.SqlServer, _driver.Kind);
        Assert.Equal("@", _driver.ParamPrefix);
    }

    [Fact]
    public void Factory_ReturnsSqlServerDriver() =>
        Assert.IsType<SqlServerDriver>(DriverFactory.For(DbKind.SqlServer));

    [Fact]
    public void Quote_BracketsAndEscapesClosingBracket()
    {
        Assert.Equal("[abc]", _driver.Quote("abc"));
        Assert.Equal("[a]]b]", _driver.Quote("a]b"));
        Assert.Equal("[a[b]", _driver.Quote("a[b"));
    }

    [Fact]
    public void QuoteTable_QualifiesWithSchemaWhenPresent()
    {
        Assert.Equal("[dbo].[Users]", _driver.QuoteTable(new TableRef("dbo", "Users")));
        Assert.Equal("[Users]", _driver.QuoteTable(new TableRef(null, "Users")));
    }

    [Fact]
    public void PageSql_UsesOffsetFetch()
    {
        var sql = _driver.PageSql("SELECT * FROM [dbo].[t]", "[id]", 200, 200);
        Assert.Equal("SELECT * FROM [dbo].[t] ORDER BY [id] OFFSET 200 ROWS FETCH NEXT 200 ROWS ONLY", sql);
    }

    [Fact]
    public void PageSql_WithoutOrderBy_StillEmitsOrderBy()
    {
        // OFFSET/FETCH is a syntax error without ORDER BY in SQL Server.
        var sql = _driver.PageSql("SELECT * FROM [t]", "", 0, 50);
        Assert.Equal("SELECT * FROM [t] ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT 50 ROWS ONLY", sql);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    public void PageSql_RejectsInvalidOffsetOrLimit(int offset, int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _driver.PageSql("SELECT 1", "[a]", offset, limit));

    [Fact]
    public void CastToText_UsesNvarcharMax() =>
        Assert.Equal("CAST([c] AS NVARCHAR(MAX))", _driver.CastToText("[c]"));

    [Fact]
    public void EscapeLike_EscapesWildcardsIncludingBracket() =>
        Assert.Equal("a\\%b\\_c\\[d\\\\", _driver.EscapeLike("a%b_c[d\\"));

    [Fact]
    public void CreateConnection_AppliesEncryptAndTrustServerCertificate()
    {
        using var conn = _driver.CreateConnection(Config(encrypt: true, trust: true), "s3cret");
        var b = new SqlConnectionStringBuilder(conn.ConnectionString);

        Assert.Equal(SqlConnectionEncryptOption.Mandatory, b.Encrypt);
        Assert.True(b.TrustServerCertificate);
        Assert.Equal("db.example.com,1433", b.DataSource);
        Assert.Equal("n2f", b.InitialCatalog);
        Assert.Equal("app_user", b.UserID);
        Assert.Equal("s3cret", b.Password);
        Assert.Equal(10, b.ConnectTimeout);
    }

    [Fact]
    public void CreateConnection_EncryptOffAndNoTrust()
    {
        using var conn = _driver.CreateConnection(Config(encrypt: false, trust: false), "x");
        var b = new SqlConnectionStringBuilder(conn.ConnectionString);

        Assert.Equal(SqlConnectionEncryptOption.Optional, b.Encrypt);
        Assert.False(b.TrustServerCertificate);
    }

    [Fact]
    public void CreateConnection_OmitsPortWhenNull()
    {
        using var conn = _driver.CreateConnection(Config(port: null), "x");
        Assert.Equal("db.example.com", new SqlConnectionStringBuilder(conn.ConnectionString).DataSource);
    }

    [Fact]
    public void CreateConnection_IsNotOpened()
    {
        using var conn = _driver.CreateConnection(Config(), "x");
        Assert.Equal(System.Data.ConnectionState.Closed, conn.State);
    }

    [Theory]
    [InlineData("uniqueidentifier", typeof(Guid))]
    [InlineData("int", typeof(int))]
    [InlineData("bigint", typeof(long))]
    [InlineData("smallint", typeof(short))]
    [InlineData("tinyint", typeof(byte))]
    [InlineData("bit", typeof(bool))]
    [InlineData("decimal", typeof(decimal))]
    [InlineData("money", typeof(decimal))]
    [InlineData("float", typeof(double))]
    [InlineData("real", typeof(float))]
    [InlineData("datetime2", typeof(DateTime))]
    [InlineData("date", typeof(DateTime))]
    [InlineData("datetimeoffset", typeof(DateTimeOffset))]
    [InlineData("time", typeof(TimeSpan))]
    [InlineData("varbinary", typeof(byte[]))]
    [InlineData("nvarchar", typeof(string))]
    [InlineData("xml", typeof(string))]
    [InlineData("sql_variant", typeof(object))]
    [InlineData("geography", typeof(object))]
    public void MapClrType_MapsSqlServerTypes(string sqlType, Type expected) =>
        Assert.Equal(expected, SqlServerDriver.MapClrType(sqlType));

    [Theory]
    [InlineData("nvarchar", 100, 0, 0, "nvarchar(50)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("varchar", 30, 0, 0, "varchar(30)")]
    [InlineData("decimal", 9, 10, 2, "decimal(10,2)")]
    [InlineData("datetime2", 8, 27, 7, "datetime2(7)")]
    [InlineData("int", 4, 10, 0, "int")]
    [InlineData("uniqueidentifier", 16, 0, 0, "uniqueidentifier")]
    public void FormatDataType_ShowsLengthPrecisionScale(string name, int maxLength, int precision, int scale, string expected) =>
        Assert.Equal(expected, SqlServerDriver.FormatDataType(name, maxLength, precision, scale));
}

/// <summary>
/// Integration tests, skipped unless env var DBX_MSSQL holds an ADO.NET connection string to a scratch database.
/// They create (and drop) their own uniquely named schemas.
/// </summary>
public class SqlServerDriverIntegrationTests
{
    private const string EnvVar = "DBX_MSSQL";

    private readonly IDbDriver _driver = new SqlServerDriver();

    private static string RequireConnectionString()
    {
        var cs = Environment.GetEnvironmentVariable(EnvVar);
        Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {EnvVar} to a SQL Server connection string to run this test.");
        return cs!;
    }

    private static async Task ExecuteAsync(SqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<IReadOnlyList<TableInfo>> WithFixtureAsync(
        string connectionString, string schemaA, string schemaB, string setupSql)
    {
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync();
        try
        {
            await ExecuteAsync(c, setupSql);
            return await _driver.LoadSchemaAsync(c, CancellationToken.None);
        }
        finally
        {
            await ExecuteAsync(c, $"""
                DROP VIEW IF EXISTS [{schemaB}].[v_items];
                DROP TABLE IF EXISTS [{schemaB}].[child];
                DROP TABLE IF EXISTS [{schemaA}].[parent];
                DROP SCHEMA IF EXISTS [{schemaB}];
                DROP SCHEMA IF EXISTS [{schemaA}];
                """);
        }
    }

    [SkippableFact]
    public async Task LoadSchema_ReturnsFkAcrossSchemas()
    {
        var cs = RequireConnectionString();
        var sa = "dbx_a_" + Guid.NewGuid().ToString("N")[..8];
        var sb = "dbx_b_" + Guid.NewGuid().ToString("N")[..8];

        var tables = await WithFixtureAsync(cs, sa, sb, $"""
            EXEC('CREATE SCHEMA [{sa}]');
            EXEC('CREATE SCHEMA [{sb}]');
            CREATE TABLE [{sa}].[parent] (id INT NOT NULL CONSTRAINT [pk_{sa}] PRIMARY KEY, label NVARCHAR(50) NULL);
            CREATE TABLE [{sb}].[child] (
                id INT NOT NULL PRIMARY KEY,
                parent_id INT NOT NULL,
                CONSTRAINT [fk_{sb}_parent] FOREIGN KEY (parent_id) REFERENCES [{sa}].[parent](id));
            CREATE UNIQUE INDEX [ux_{sa}_label] ON [{sa}].[parent](label);
            EXEC('CREATE VIEW [{sb}].[v_items] AS SELECT id FROM [{sb}].[child]');
            """);

        var child = Assert.Single(tables, t => t.Ref == new TableRef(sb, "child"));
        var fk = Assert.Single(child.ForeignKeys);
        Assert.Equal($"fk_{sb}_parent", fk.Name);
        Assert.Equal(new[] { "parent_id" }, fk.Columns);
        Assert.Equal(new TableRef(sa, "parent"), fk.RefTable);
        Assert.Equal(new[] { "id" }, fk.RefColumns);

        var parent = Assert.Single(tables, t => t.Ref == new TableRef(sa, "parent"));
        Assert.False(parent.IsView);
        Assert.True(parent.Columns.Single(c => c.Name == "id").IsPrimaryKey);
        Assert.False(parent.Columns.Single(c => c.Name == "id").Nullable);
        Assert.True(parent.Columns.Single(c => c.Name == "label").Nullable);
        Assert.Equal("nvarchar(50)", parent.Columns.Single(c => c.Name == "label").DataType);
        var index = Assert.Single(parent.Indexes);
        Assert.True(index.Unique);
        Assert.Equal(new[] { "label" }, index.Columns);

        var view = Assert.Single(tables, t => t.Ref == new TableRef(sb, "v_items"));
        Assert.True(view.IsView);
    }

    [SkippableFact]
    public async Task LoadSchema_MapsUniqueidentifierToGuid()
    {
        var cs = RequireConnectionString();
        var sa = "dbx_a_" + Guid.NewGuid().ToString("N")[..8];
        var sb = "dbx_b_" + Guid.NewGuid().ToString("N")[..8];

        var tables = await WithFixtureAsync(cs, sa, sb, $"""
            EXEC('CREATE SCHEMA [{sa}]');
            EXEC('CREATE SCHEMA [{sb}]');
            CREATE TABLE [{sa}].[parent] (id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY, n BIGINT NULL);
            CREATE TABLE [{sb}].[child] (id INT NOT NULL PRIMARY KEY);
            EXEC('CREATE VIEW [{sb}].[v_items] AS SELECT id FROM [{sb}].[child]');
            """);

        var parent = Assert.Single(tables, t => t.Ref == new TableRef(sa, "parent"));
        var id = parent.Columns.Single(c => c.Name == "id");
        Assert.Equal(typeof(Guid), id.ClrType);
        Assert.Equal("uniqueidentifier", id.DataType);
        Assert.Equal(typeof(long), parent.Columns.Single(c => c.Name == "n").ClrType);
    }
}
