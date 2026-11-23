using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;
using DbExplorer.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

public class RawWhereGuardTests
{
    [Fact]
    public void Prepare_BlankOrCommentOnly_MeansNoCondition()
    {
        Assert.Null(TableDataLoader.PrepareRawWhere(null, DbKind.Sqlite));
        Assert.Null(TableDataLoader.PrepareRawWhere("   ", DbKind.Sqlite));
        Assert.Null(TableDataLoader.PrepareRawWhere("-- nothing", DbKind.Postgres));
    }

    [Theory]
    [InlineData(DbKind.Sqlite)]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.SqlServer)]
    public void Prepare_SingleCondition_PassesThrough(DbKind kind)
    {
        Assert.Equal("id > 5 AND name = 'a;b'", TableDataLoader.PrepareRawWhere("id > 5 AND name = 'a;b'", kind));
    }

    [Fact]
    public void Prepare_TrailingSemicolonAndComment_AreStripped()
    {
        // A trailing ';' or '-- comment' would otherwise break the closing parenthesis of the generated WHERE.
        Assert.Equal("id > 5", TableDataLoader.PrepareRawWhere("id > 5;", DbKind.Sqlite));
        Assert.Equal("id > 5", TableDataLoader.PrepareRawWhere("id > 5 -- recent", DbKind.Sqlite));
    }

    [Theory]
    [InlineData(DbKind.Sqlite, "1=1; DELETE FROM customers")]
    [InlineData(DbKind.Postgres, "1=1; DROP TABLE customers")]
    [InlineData(DbKind.SqlServer, "1=1; DELETE FROM customers")]
    [InlineData(DbKind.SqlServer, "1=1\nGO\nDELETE FROM customers")]
    public void Prepare_MoreThanOneStatement_IsRejected(DbKind kind, string where)
    {
        var e = Assert.Throws<RawWhereException>(() => TableDataLoader.PrepareRawWhere(where, kind));
        Assert.Contains("single", e.Message);
    }

    [Theory]
    [InlineData(DbKind.SqlServer, "1=1) DELETE FROM customers WHERE (1=1")] // T-SQL needs no separator between statements
    [InlineData(DbKind.Postgres, "1=1) AND EXISTS (WITH d AS (DELETE FROM customers RETURNING 1) SELECT 1 FROM d")]
    [InlineData(DbKind.Sqlite, "id IN (SELECT 1) UPDATE customers SET name = 'x'")]
    public void Prepare_WriteStatementWithoutSeparator_IsRejected(DbKind kind, string where)
    {
        var e = Assert.Throws<RawWhereException>(() => TableDataLoader.PrepareRawWhere(where, kind));
        Assert.Contains("write", e.Message);
    }

    [Fact]
    public void Prepare_WriteWordsInsideStringsAndQuotedIdentifiers_AreFine()
    {
        Assert.NotNull(TableDataLoader.PrepareRawWhere("name = 'delete from x' AND [update] = 1", DbKind.SqlServer));
        Assert.NotNull(TableDataLoader.PrepareRawWhere("name = 'drop' AND \"insert\" = 1", DbKind.Postgres));
    }
}

public class TableDataLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-loader-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private readonly Session _session;
    private readonly TableInfo _customers;

    public TableDataLoaderTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "t.db");
        using (var c = new SqliteConnection($"Data Source={_file};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT);" +
                              "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 450) " +
                              "INSERT INTO customers SELECT i, 'c' || i FROM n;";
            cmd.ExecuteNonQuery();
        }

        var config = new ConnectionConfig(Guid.NewGuid(), "lite", DbKind.Sqlite, null, null, null, _file, null, null, false, false, false);
        var driver = new SqliteDriver();
        using var conn = driver.CreateConnection(config, null);
        var schema = driver.LoadSchemaAsync(conn, CancellationToken.None).GetAwaiter().GetResult();
        _session = new Session(config, driver, schema);
        _customers = schema.Single(t => t.Ref.Name == "customers");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private long Count()
    {
        using var c = new SqliteConnection($"Data Source={_file};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM customers";
        return (long)cmd.ExecuteScalar()!;
    }

    private TableQuery Query(string? raw = null, int offset = 0) => new(_customers, [], raw, [], offset, 0);

    [Fact]
    public async Task LoadPage_ReturnsPageSizeRows_AndHasMore()
    {
        var page = await TableDataLoader.LoadPageAsync(_session, Query(), 200, CancellationToken.None);

        Assert.Equal(200, page.Rows.Count);
        Assert.True(page.HasMore);
        Assert.Equal(["id", "name"], page.ColumnNames);
        Assert.Equal(1L, page.Rows[0][0]);
    }

    [Fact]
    public async Task LoadPage_LastPage_HasNoMore()
    {
        var page = await TableDataLoader.LoadPageAsync(_session, Query(offset: 400), 200, CancellationToken.None);

        Assert.Equal(50, page.Rows.Count);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task LoadPage_MultiStatementRawWhere_IsRejected_AndNothingRuns()
    {
        await Assert.ThrowsAsync<RawWhereException>(() =>
            TableDataLoader.LoadPageAsync(_session, Query("1=1; DELETE FROM customers"), 200, CancellationToken.None));

        Assert.Equal(450, Count());
    }

    [Fact]
    public async Task Count_MultiStatementRawWhere_IsRejected_AndNothingRuns()
    {
        await Assert.ThrowsAsync<RawWhereException>(() =>
            TableDataLoader.CountAsync(_session, Query("1=1; DELETE FROM customers"), CancellationToken.None));

        Assert.Equal(450, Count());
    }

    [Fact]
    public async Task LoadPage_WriteInRawWhere_OnReadOnlyConnection_IsRejected()
    {
        _session.UpdateConfig(_session.Config with { ReadOnly = true });

        await Assert.ThrowsAsync<RawWhereException>(() =>
            TableDataLoader.LoadPageAsync(_session, Query("id IN (SELECT 1) DELETE FROM customers"), 200, CancellationToken.None));

        Assert.Equal(450, Count());
    }

    [Fact]
    public async Task Count_ReturnsFilteredTotal()
    {
        var n = await TableDataLoader.CountAsync(_session, Query("id > 100"), CancellationToken.None);

        Assert.Equal(350, n);
    }

    [Fact]
    public async Task ReadOnlyContext_Sqlite_RefusesWrites()
    {
        await using var c = await _session.OpenConnectionAsync(CancellationToken.None);
        await using var tx = await TableDataLoader.BeginReadOnlyContextAsync(c, DbKind.Sqlite, CancellationToken.None);

        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM customers";
        var e = await Assert.ThrowsAnyAsync<Exception>(() => cmd.ExecuteNonQueryAsync());
        Assert.Contains("readonly", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(450, Count());
    }
}
