using System.Data.Common;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

public class FilterParserTests
{
    [Fact]
    public void Parse_PlainText_IsContains()
    {
        var f = FilterParser.Parse("  john ");
        Assert.NotNull(f);
        Assert.Equal(FilterOp.Contains, f!.Op);
        Assert.Equal(new[] { "john" }, f.Values);
    }

    [Theory]
    [InlineData("=5", FilterOp.Eq, "5")]
    [InlineData("= 5", FilterOp.Eq, "5")]
    [InlineData("!=x", FilterOp.Ne, "x")]
    [InlineData("<10", FilterOp.Lt, "10")]
    [InlineData(">=3", FilterOp.Ge, "3")]
    [InlineData("<=3", FilterOp.Le, "3")]
    [InlineData(">1", FilterOp.Gt, "1")]
    public void Parse_Operators(string text, FilterOp op, string value)
    {
        var f = FilterParser.Parse(text);
        Assert.NotNull(f);
        Assert.Equal(op, f!.Op);
        Assert.Equal(new[] { value }, f.Values);
    }

    [Theory]
    [InlineData("null", FilterOp.IsNull)]
    [InlineData("NULL", FilterOp.IsNull)]
    [InlineData("!null", FilterOp.NotNull)]
    [InlineData(" !NULL ", FilterOp.NotNull)]
    public void Parse_NullAndNotNull(string text, FilterOp op)
    {
        var f = FilterParser.Parse(text);
        Assert.NotNull(f);
        Assert.Equal(op, f!.Op);
        Assert.Empty(f.Values);
    }

    [Fact]
    public void Parse_In()
    {
        var f = FilterParser.Parse("in (a, b ,c)");
        Assert.NotNull(f);
        Assert.Equal(FilterOp.In, f!.Op);
        Assert.Equal(new[] { "a", "b", "c" }, f.Values);
    }

    [Theory]
    [InlineData("in (a")]
    [InlineData("in ()")]
    [InlineData("in")]
    public void Parse_MalformedIn_FallsBackToContains(string text)
    {
        var f = FilterParser.Parse(text);
        Assert.Equal(FilterOp.Contains, f!.Op);
        Assert.Equal(new[] { text }, f.Values);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_Empty_ReturnsNull(string? text) => Assert.Null(FilterParser.Parse(text!));

    [Fact]
    public void Parse_EqualsWithEmptyValue_IsEqualsEmptyString()
    {
        var f = FilterParser.Parse("=");
        Assert.Equal(FilterOp.Eq, f!.Op);
        Assert.Equal(new[] { "" }, f.Values);
    }
}

public class TableQueryBuilderTests
{
    private static ColumnInfo Col(string name, Type clr, bool pk = false, int ordinal = 0) =>
        new(name, clr.Name, clr, true, pk, ordinal);

    private static TableInfo Table(string? schema, string name, params ColumnInfo[] cols) =>
        new(new TableRef(schema, name), false, cols, [], []);

    private static TableQuery Query(TableInfo t, params ColumnFilter[] filters) =>
        new(t, filters, null, [], 0, 200);

    private static readonly TableInfo Customers = Table(null, "customers",
        Col("id", typeof(int), pk: true, ordinal: 0),
        Col("name", typeof(string), ordinal: 1),
        Col("ref", typeof(Guid), ordinal: 2),
        Col("active", typeof(bool), ordinal: 3),
        Col("created", typeof(DateTime), ordinal: 4),
        Col("balance", typeof(decimal), ordinal: 5));

    private static readonly IDbDriver Sqlite = new SqliteDriver();

    [Fact]
    public void Build_ConvertsValueToColumnClrType()
    {
        var spec = TableQueryBuilder.Build(Sqlite, Query(Customers, new ColumnFilter("id", "=5")));

        Assert.Contains("\"id\" = @p0", spec.Sql);
        var p = Assert.Single(spec.Params);
        Assert.Equal("@p0", p.Name);
        Assert.IsType<int>(p.Value);
        Assert.Equal(5, p.Value);
    }

    [Fact]
    public void Build_ConvertsGuidBoolDateAndDecimal()
    {
        var guid = Guid.NewGuid();
        var spec = TableQueryBuilder.Build(Sqlite, Query(Customers,
            new ColumnFilter("ref", "=" + guid),
            new ColumnFilter("active", "=true"),
            new ColumnFilter("created", ">=2024-03-05"),
            new ColumnFilter("balance", "<1.5")));

        Assert.Equal(guid, spec.Params[0].Value);
        Assert.Equal(true, spec.Params[1].Value);
        Assert.Equal(new DateTime(2024, 3, 5), spec.Params[2].Value);
        Assert.Equal(1.5m, spec.Params[3].Value);
    }

    [Theory]
    [InlineData("=true", true)]
    [InlineData("=TRUE", true)]
    [InlineData("=1", true)]
    [InlineData("=false", false)]
    [InlineData("=0", false)]
    public void Build_ParsesBooleans(string text, bool expected)
    {
        var spec = TableQueryBuilder.Build(Sqlite, Query(Customers, new ColumnFilter("active", text)));
        Assert.Equal(expected, spec.Params[0].Value);
    }

    [Fact]
    public void Build_UnparseableValue_ThrowsFilterExceptionNamingColumn()
    {
        var ex = Assert.Throws<FilterException>(() =>
            TableQueryBuilder.Build(Sqlite, Query(Customers, new ColumnFilter("id", "=abc"))));
        Assert.Equal("id", ex.Column);
        Assert.Contains("id", ex.Message);
    }

    [Fact]
    public void Build_UnknownColumn_ThrowsFilterException()
    {
        var ex = Assert.Throws<FilterException>(() =>
            TableQueryBuilder.Build(Sqlite, Query(Customers, new ColumnFilter("nope", "=1"))));
        Assert.Equal("nope", ex.Column);
    }

    [Fact]
    public void Build_ContainsEscapesLikeWildcards()
    {
        var spec = TableQueryBuilder.Build(Sqlite, Query(Customers, new ColumnFilter("name", "50%_")));

        Assert.Contains("CAST(\"name\" AS TEXT) LIKE @p0 ESCAPE '\\'", spec.Sql);
        Assert.Equal("%50\\%\\_%", spec.Params[0].Value);
    }

    [Fact]
    public void Build_ContainsOnNonTextColumn_UsesTextParameter()
    {
        var spec = TableQueryBuilder.Build(Sqlite, Query(Customers, new ColumnFilter("id", "12")));
        Assert.Equal("%12%", spec.Params[0].Value);
    }

    [Fact]
    public void Build_NeverInlinesValues()
    {
        var spec = TableQueryBuilder.Build(Sqlite, Query(Customers,
            new ColumnFilter("name", "='; DROP TABLE customers; --"),
            new ColumnFilter("name", "in (x', y)")));

        Assert.DoesNotContain("DROP", spec.Sql);
        Assert.DoesNotContain("x'", spec.Sql);
        Assert.Equal("'; DROP TABLE customers; --", spec.Params[0].Value);
    }

    [Fact]
    public void Build_QuotesIdentifiersWithSpaces()
    {
        var t = Table("dbo", "Order Details", Col("Unit Price", typeof(decimal)));
        var spec = TableQueryBuilder.Build(new FakeSqlServerDriver(),
            Query(t, new ColumnFilter("Unit Price", ">3")));

        Assert.Contains("FROM [dbo].[Order Details]", spec.Sql);
        Assert.Contains("[Unit Price] > @p0", spec.Sql);

        var sqlite = TableQueryBuilder.Build(Sqlite, Query(Table(null, "Order Details",
            Col("Unit Price", typeof(decimal))), new ColumnFilter("Unit Price", ">3")));
        Assert.Contains("FROM \"Order Details\"", sqlite.Sql);
        Assert.Contains("\"Unit Price\" > @p0", sqlite.Sql);
    }

    [Fact]
    public void Build_QuotingEscapesEmbeddedQuotesInColumnNames()
    {
        var t = Table(null, "t", Col("we\"ird", typeof(string)));
        var spec = TableQueryBuilder.Build(Sqlite, Query(t, new ColumnFilter("we\"ird", "=x")));
        Assert.Contains("\"we\"\"ird\" = @p0", spec.Sql);
    }

    [Fact]
    public void Build_EmptyStringFilterIsNotNullFilter()
    {
        var eqEmpty = TableQueryBuilder.Build(Sqlite, Query(Customers, new ColumnFilter("name", "=")));
        Assert.Contains("\"name\" = @p0", eqEmpty.Sql);
        Assert.DoesNotContain("IS NULL", eqEmpty.Sql);
        Assert.Equal("", eqEmpty.Params[0].Value);

        var isNull = TableQueryBuilder.Build(Sqlite, Query(Customers, new ColumnFilter("name", "null")));
        Assert.Contains("\"name\" IS NULL", isNull.Sql);
        Assert.Empty(isNull.Params);

        var notNull = TableQueryBuilder.Build(Sqlite, Query(Customers, new ColumnFilter("name", "!null")));
        Assert.Contains("\"name\" IS NOT NULL", notNull.Sql);
    }

    [Fact]
    public void Build_InExpandsToOneParameterPerValue()
    {
        var spec = TableQueryBuilder.Build(Sqlite, Query(Customers, new ColumnFilter("id", "in (1, 2,3)")));

        Assert.Contains("\"id\" IN (@p0, @p1, @p2)", spec.Sql);
        Assert.Equal(new object?[] { 1, 2, 3 }, spec.Params.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void Build_BlankFiltersAreIgnored()
    {
        var spec = TableQueryBuilder.Build(Sqlite, Query(Customers, new ColumnFilter("id", "  ")));
        Assert.DoesNotContain("WHERE", spec.Sql);
        Assert.Empty(spec.Params);
    }

    [Fact]
    public void Build_CombinesFiltersAndRawWhereWithAnd()
    {
        var q = Query(Customers, new ColumnFilter("id", ">2"), new ColumnFilter("name", "=bob")) with
        {
            RawWhere = "a = 1 OR b = 2",
        };
        var spec = TableQueryBuilder.Build(Sqlite, q);

        Assert.Contains("WHERE \"id\" > @p0 AND \"name\" = @p1 AND (a = 1 OR b = 2\n)", spec.Sql.ReplaceLineEndings("\n"));
        Assert.Equal(2, spec.Params.Count);
    }

    [Fact]
    public void Build_RawWhereAlone_IsWrappedInParentheses()
    {
        var spec = TableQueryBuilder.Build(Sqlite, Query(Customers) with { RawWhere = "x > 1" });
        Assert.Contains("WHERE (x > 1\n)", spec.Sql.ReplaceLineEndings("\n"));
        Assert.Empty(spec.Params);
    }

    [Fact]
    public void Build_BlankRawWhere_IsIgnored()
    {
        var spec = TableQueryBuilder.Build(Sqlite, Query(Customers) with { RawWhere = "   " });
        Assert.DoesNotContain("WHERE", spec.Sql);
    }

    [Theory]
    [InlineData("=1,5")]
    [InlineData(">1,5")]
    [InlineData("=1,000.5")]
    [InlineData("=NaN")]
    [InlineData("=Infinity")]
    public void Build_DecimalRejectsCommaThousandsAndNonFinite(string text)
    {
        var ex = Assert.Throws<FilterException>(() =>
            TableQueryBuilder.Build(Sqlite, Query(Customers, new ColumnFilter("balance", text))));
        Assert.Equal("balance", ex.Column);
    }

    [Fact]
    public void Build_DecimalAcceptsDotDecimalSeparator()
    {
        var spec = TableQueryBuilder.Build(Sqlite, Query(Customers, new ColumnFilter("balance", ">1.5")));
        Assert.Equal(1.5m, spec.Params[0].Value);
    }

    [Theory]
    [InlineData(typeof(double))]
    [InlineData(typeof(float))]
    public void Build_FloatingPointRejectsCommaAndNaN(Type clr)
    {
        var t = Table(null, "t", Col("v", clr));
        foreach (var text in new[] { "=1,5", "=NaN", "=-Infinity" })
            Assert.Throws<FilterException>(() => TableQueryBuilder.Build(Sqlite, Query(t, new ColumnFilter("v", text))));
        Assert.Equal(1.5, Convert.ToDouble(TableQueryBuilder.Build(Sqlite, Query(t, new ColumnFilter("v", "=1.5"))).Params[0].Value));
    }

    [Fact]
    public void Build_IntegerRejectsThousandsSeparator()
    {
        Assert.Throws<FilterException>(() =>
            TableQueryBuilder.Build(Sqlite, Query(Customers, new ColumnFilter("id", "=1,000"))));
    }

    [Fact]
    public void Build_SortAppendsPrimaryKeyTiebreaker()
    {
        var t = Table(null, "lines",
            Col("order_id", typeof(int), pk: true, ordinal: 0),
            Col("line_no", typeof(int), pk: true, ordinal: 1),
            Col("note", typeof(string), ordinal: 2));

        var spec = TableQueryBuilder.Build(Sqlite, Query(t) with { Sort = [new SortSpec("note", true)] });
        Assert.Contains("ORDER BY \"note\" DESC, \"order_id\", \"line_no\" LIMIT", spec.Sql);

        // A pk column already in the sort is not repeated.
        var spec2 = TableQueryBuilder.Build(Sqlite, Query(t) with { Sort = [new SortSpec("line_no", false)] });
        Assert.Contains("ORDER BY \"line_no\" ASC, \"order_id\" LIMIT", spec2.Sql);
    }

    [Fact]
    public void Build_RawWhereClosingParenthesisIsOnItsOwnLine()
    {
        var spec = TableQueryBuilder.Build(Sqlite, Query(Customers) with { RawWhere = "x > 1 -- note" });
        Assert.Contains("WHERE (x > 1 -- note\n)", spec.Sql.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Build_Sqlite_PagesAndOrdersByPrimaryKey()
    {
        var spec = TableQueryBuilder.Build(Sqlite, Query(Customers) with { Offset = 400 });
        Assert.Equal("SELECT * FROM \"customers\" ORDER BY \"id\" LIMIT 200 OFFSET 400", spec.Sql);
    }

    [Fact]
    public void Build_ExplicitSortWinsOverPrimaryKey()
    {
        var q = Query(Customers) with
        {
            Sort = [new SortSpec("name", false), new SortSpec("balance", true)],
        };
        var spec = TableQueryBuilder.Build(Sqlite, q);
        Assert.Contains("ORDER BY \"name\" ASC, \"balance\" DESC, \"id\" LIMIT", spec.Sql);
    }

    [Fact]
    public void Build_CompositePrimaryKey_OrdersByOrdinal()
    {
        var t = Table(null, "lines",
            Col("line_no", typeof(int), pk: true, ordinal: 1),
            Col("order_id", typeof(int), pk: true, ordinal: 0),
            Col("note", typeof(string), ordinal: 2));
        var spec = TableQueryBuilder.Build(Sqlite, Query(t));
        Assert.Contains("ORDER BY \"order_id\", \"line_no\" LIMIT", spec.Sql);
    }

    [Fact]
    public void Build_SqlServerWithoutSort_UsesPkThenSelectNull()
    {
        var sqlServer = new FakeSqlServerDriver();

        var withPk = TableQueryBuilder.Build(sqlServer, Query(Customers));
        Assert.Equal(
            "SELECT * FROM [customers] ORDER BY [id] OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY", withPk.Sql);

        var noKey = Table(null, "log", Col("msg", typeof(string)));
        var withoutPk = TableQueryBuilder.Build(sqlServer, Query(noKey));
        Assert.Equal(
            "SELECT * FROM [log] ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY", withoutPk.Sql);
    }

    [Fact]
    public void Build_Postgres_UsesTextCastAndLimitOffset()
    {
        var t = Table("public", "log", Col("msg", typeof(string)));
        var spec = TableQueryBuilder.Build(new FakePostgresDriver(),
            Query(t, new ColumnFilter("msg", "hi")) with { Offset = 10, Limit = 5 });

        Assert.Equal(
            "SELECT * FROM \"public\".\"log\" WHERE \"msg\"::text LIKE @p0 ESCAPE '\\' LIMIT 5 OFFSET 10",
            spec.Sql);
    }

    [Fact]
    public void BuildCount_HasNoPaging()
    {
        var q = Query(Customers, new ColumnFilter("id", ">2")) with
        {
            Sort = [new SortSpec("name", false)],
            Offset = 200,
        };
        var spec = TableQueryBuilder.BuildCount(Sqlite, q);

        Assert.Equal("SELECT COUNT(*) FROM \"customers\" WHERE \"id\" > @p0", spec.Sql);
        Assert.Single(spec.Params);
    }

    [Fact]
    public void BuildCount_SqlServer_HasNoOrderByOrOffset()
    {
        var spec = TableQueryBuilder.BuildCount(new FakeSqlServerDriver(), Query(Customers));
        Assert.Equal("SELECT COUNT(*) FROM [customers]", spec.Sql);
    }

    [Fact]
    public async Task Build_ExecutesAgainstRealSqlite()
    {
        var path = Path.Combine(Path.GetTempPath(), "dbExplorer-qb-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await c.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT, active BOOLEAN, created DATETIME);
                    INSERT INTO items VALUES (1, '50% off', 1, '2024-03-05 00:00:00');
                    INSERT INTO items VALUES (2, '500 units', 0, '2024-03-06 00:00:00');
                    INSERT INTO items VALUES (3, NULL, 1, '2023-01-01 00:00:00');
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            var driver = new SqliteDriver();
            IReadOnlyList<TableInfo> schema;
            await using (var c = driver.CreateConnection(
                Cfg(path), null))
            {
                schema = await driver.LoadSchemaAsync(c, CancellationToken.None);
            }
            var items = schema.Single();

            async Task<List<long>> Ids(params ColumnFilter[] filters)
            {
                var spec = TableQueryBuilder.Build(driver, new TableQuery(items, filters, null, [], 0, 200));
                await using var c = driver.CreateConnection(
                    Cfg(path), null);
                await c.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = spec.Sql;
                foreach (var p in spec.Params)
                {
                    var dbp = cmd.CreateParameter();
                    dbp.ParameterName = p.Name;
                    dbp.Value = p.Value ?? DBNull.Value;
                    cmd.Parameters.Add(dbp);
                }
                var ids = new List<long>();
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) ids.Add(r.GetInt64(0));
                return ids;
            }

            Assert.Equal([1L], await Ids(new ColumnFilter("name", "50%")));
            Assert.Equal([1L, 3L], await Ids(new ColumnFilter("active", "=true")));
            Assert.Equal([2L], await Ids(new ColumnFilter("active", "=0")));
            Assert.Equal([3L], await Ids(new ColumnFilter("name", "null")));
            Assert.Equal([1L, 2L], await Ids(new ColumnFilter("created", ">=2024-03-05")));
            Assert.Equal([1L, 3L], await Ids(new ColumnFilter("id", "in (1, 3)")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static ConnectionConfig Cfg(string path) =>
        new(Guid.NewGuid(), "t", DbKind.Sqlite, null, null, null, path, null, null, false, false, false);

    // Test doubles mimicking the dialect rules of the engines whose drivers are built elsewhere.
    private sealed class FakeSqlServerDriver : IDbDriver
    {
        public DbKind Kind => DbKind.SqlServer;
        public string ParamPrefix => "@";
        public string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";
        public string QuoteTable(TableRef t) => t.Schema is null ? Quote(t.Name) : Quote(t.Schema) + "." + Quote(t.Name);
        public string CastToText(string quotedColumn) => $"CAST({quotedColumn} AS NVARCHAR(MAX))";
        public string EscapeLike(string text) =>
            text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
        public string PageSql(string selectSql, string orderBy, int offset, int limit) =>
            $"{selectSql} ORDER BY {orderBy} OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY";
        public DbConnection CreateConnection(ConnectionConfig cfg, string? plainPassword) => throw new NotSupportedException();
        public Task<IReadOnlyList<TableInfo>> LoadSchemaAsync(DbConnection c, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakePostgresDriver : IDbDriver
    {
        public DbKind Kind => DbKind.Postgres;
        public string ParamPrefix => "@";
        public string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
        public string QuoteTable(TableRef t) => t.Schema is null ? Quote(t.Name) : Quote(t.Schema) + "." + Quote(t.Name);
        public string CastToText(string quotedColumn) => $"{quotedColumn}::text";
        public string EscapeLike(string text) =>
            text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        public string PageSql(string selectSql, string orderBy, int offset, int limit) =>
            $"{selectSql}{(string.IsNullOrWhiteSpace(orderBy) ? "" : " ORDER BY " + orderBy)} LIMIT {limit} OFFSET {offset}";
        public DbConnection CreateConnection(ConnectionConfig cfg, string? plainPassword) => throw new NotSupportedException();
        public Task<IReadOnlyList<TableInfo>> LoadSchemaAsync(DbConnection c, CancellationToken ct) => throw new NotSupportedException();
    }
}
