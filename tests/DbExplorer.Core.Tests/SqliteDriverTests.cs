using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

public sealed class SqliteDbFixture : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "dbExplorer-tests-" + Guid.NewGuid().ToString("N") + ".db");

    public SqliteDbFixture()
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Pooling = false,
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE customers (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                email VARCHAR(100),
                balance DECIMAL(10,2),
                created DATETIME,
                avatar BLOB,
                score REAL,
                active BOOLEAN
            );
            CREATE TABLE order_lines (
                order_id INTEGER NOT NULL,
                line_no INTEGER NOT NULL,
                product TEXT,
                PRIMARY KEY (order_id, line_no)
            );
            CREATE TABLE shipments (
                id INTEGER PRIMARY KEY,
                order_id INT,
                line_no INT,
                parent_id INTEGER REFERENCES shipments(id),
                FOREIGN KEY (order_id, line_no) REFERENCES order_lines(order_id, line_no)
            );
            CREATE INDEX ix_customers_name ON customers(name);
            CREATE UNIQUE INDEX ux_customers_email ON customers(email);
            CREATE INDEX ix_shipments_order ON shipments(order_id, line_no);
            CREATE VIEW v_customer_names AS SELECT id, name FROM customers;
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}

public class SqliteDriverTests : IClassFixture<SqliteDbFixture>
{
    private readonly SqliteDbFixture _db;
    private readonly IDbDriver _driver = new SqliteDriver();

    public SqliteDriverTests(SqliteDbFixture db) => _db = db;

    private ConnectionConfig Config(string? database = null) => new(
        Guid.NewGuid(), "test", DbKind.Sqlite, null, null, null,
        database ?? _db.Path, null, null, false, false, false);

    private async Task<IReadOnlyList<TableInfo>> LoadAsync()
    {
        using var conn = _driver.CreateConnection(Config(), null);
        await conn.OpenAsync();
        return await _driver.LoadSchemaAsync(conn, CancellationToken.None);
    }

    [Fact]
    public async Task LoadSchema_ReturnsTablesViewsColumnsPk()
    {
        var tables = await LoadAsync();

        Assert.Equal(new[] { "customers", "order_lines", "shipments", "v_customer_names" },
            tables.Select(t => t.Ref.Name).ToArray());
        Assert.All(tables, t => Assert.Null(t.Ref.Schema));
        Assert.Equal(new[] { false, false, false, true }, tables.Select(t => t.IsView).ToArray());

        var customers = tables.Single(t => t.Ref.Name == "customers");
        Assert.Equal(
            new[] { "id", "name", "email", "balance", "created", "avatar", "score", "active" },
            customers.Columns.Select(c => c.Name).ToArray());
        Assert.Equal(Enumerable.Range(0, 8), customers.Columns.Select(c => c.Ordinal));

        var id = customers.Columns[0];
        Assert.True(id.IsPrimaryKey);
        Assert.False(id.Nullable);
        Assert.Equal("INTEGER", id.DataType);
        Assert.Equal(typeof(long), id.ClrType);

        var name = customers.Columns[1];
        Assert.False(name.IsPrimaryKey);
        Assert.False(name.Nullable);
        Assert.Equal(typeof(string), name.ClrType);

        var email = customers.Columns[2];
        Assert.True(email.Nullable);
        Assert.Equal("VARCHAR(100)", email.DataType);
        Assert.Equal(typeof(string), email.ClrType);

        Assert.Equal(typeof(decimal), customers.Columns[3].ClrType);
        Assert.Equal(typeof(DateTime), customers.Columns[4].ClrType);
        Assert.Equal(typeof(byte[]), customers.Columns[5].ClrType);
        Assert.Equal(typeof(double), customers.Columns[6].ClrType);
        Assert.Equal(typeof(bool), customers.Columns[7].ClrType);

        var lines = tables.Single(t => t.Ref.Name == "order_lines");
        Assert.Equal(new[] { true, true, false }, lines.Columns.Select(c => c.IsPrimaryKey).ToArray());

        var view = tables.Single(t => t.Ref.Name == "v_customer_names");
        Assert.Equal(new[] { "id", "name" }, view.Columns.Select(c => c.Name).ToArray());
        Assert.Empty(view.ForeignKeys);
        Assert.Empty(view.Indexes);
    }

    [Fact]
    public async Task LoadSchema_ReturnsForeignKeys()
    {
        var shipments = (await LoadAsync()).Single(t => t.Ref.Name == "shipments");

        Assert.Equal(2, shipments.ForeignKeys.Count);

        var composite = shipments.ForeignKeys.Single(f => f.Columns.Count == 2);
        Assert.Equal(new[] { "order_id", "line_no" }, composite.Columns);
        Assert.Equal(new TableRef(null, "order_lines"), composite.RefTable);
        Assert.Equal(new[] { "order_id", "line_no" }, composite.RefColumns);

        var self = shipments.ForeignKeys.Single(f => f.Columns.Count == 1);
        Assert.Equal(new[] { "parent_id" }, self.Columns);
        Assert.Equal(shipments.Ref, self.RefTable);
        Assert.Equal(new[] { "id" }, self.RefColumns);

        Assert.NotEqual(composite.Name, self.Name);
    }

    [Fact]
    public async Task LoadSchema_ForeignKeys_ResolveCanonicalTableNameAndImplicitPk()
    {
        var path = Path.Combine(Path.GetTempPath(), "dbExplorer-tests-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var setup = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                setup.Open();
                using var cmd = setup.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE parent (id INTEGER PRIMARY KEY, code TEXT);
                    CREATE TABLE pk_order (
                        order_id INTEGER NOT NULL,
                        line_no INTEGER NOT NULL,
                        PRIMARY KEY (line_no, order_id)
                    );
                    CREATE TABLE child_bare (id INTEGER PRIMARY KEY, pid INTEGER REFERENCES Parent);
                    CREATE TABLE child_explicit (id INTEGER PRIMARY KEY, pid INTEGER REFERENCES PARENT(id));
                    CREATE TABLE child_composite_implicit (
                        a INTEGER, b INTEGER,
                        FOREIGN KEY (b, a) REFERENCES Pk_Order
                    );
                    CREATE TABLE child_composite_explicit (
                        a INTEGER, b INTEGER,
                        FOREIGN KEY (b, a) REFERENCES pk_order (order_id, line_no)
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            using var conn = _driver.CreateConnection(Config(path), null);
            await conn.OpenAsync();
            var tables = await _driver.LoadSchemaAsync(conn, CancellationToken.None);
            ForeignKeyInfo Fk(string table) => tables.Single(t => t.Ref.Name == table).ForeignKeys.Single();

            // Name written as `Parent`, implicit PK target: canonical TableRef and resolved columns.
            var bare = Fk("child_bare");
            Assert.Equal(new TableRef(null, "parent"), bare.RefTable);
            Assert.Contains(bare.RefTable, tables.Select(t => t.Ref));
            Assert.Equal(new[] { "id" }, bare.RefColumns);

            var explicitFk = Fk("child_explicit");
            Assert.Equal(new TableRef(null, "parent"), explicitFk.RefTable);
            Assert.Equal(new[] { "id" }, explicitFk.RefColumns);

            // PK is (line_no, order_id) while table columns are (order_id, line_no): PK position order wins.
            var pkOrder = tables.Single(t => t.Ref.Name == "pk_order");
            Assert.Equal(new[] { "order_id", "line_no" }, pkOrder.Columns.Select(c => c.Name).ToArray());
            var implicitComposite = Fk("child_composite_implicit");
            Assert.Equal(new TableRef(null, "pk_order"), implicitComposite.RefTable);
            Assert.Equal(new[] { "b", "a" }, implicitComposite.Columns);
            Assert.Equal(new[] { "line_no", "order_id" }, implicitComposite.RefColumns);

            // Explicit composite FK: columns follow the FK sequence, not the table column order.
            var explicitComposite = Fk("child_composite_explicit");
            Assert.Equal(new[] { "b", "a" }, explicitComposite.Columns);
            Assert.Equal(new[] { "order_id", "line_no" }, explicitComposite.RefColumns);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadSchema_ReturnsIndexes()
    {
        var tables = await LoadAsync();

        var customers = tables.Single(t => t.Ref.Name == "customers");
        var byName = customers.Indexes.Single(i => i.Name == "ix_customers_name");
        Assert.False(byName.Unique);
        Assert.Equal(new[] { "name" }, byName.Columns);
        var byEmail = customers.Indexes.Single(i => i.Name == "ux_customers_email");
        Assert.True(byEmail.Unique);
        Assert.Equal(new[] { "email" }, byEmail.Columns);

        var shipments = tables.Single(t => t.Ref.Name == "shipments");
        var composite = Assert.Single(shipments.Indexes);
        Assert.Equal("ix_shipments_order", composite.Name);
        Assert.Equal(new[] { "order_id", "line_no" }, composite.Columns);

        // PK auto-indexes are not reported (the PK is already on the columns).
        var lines = tables.Single(t => t.Ref.Name == "order_lines");
        Assert.Empty(lines.Indexes);
    }

    [Fact]
    public void Quote_EscapesEmbeddedQuotes()
    {
        Assert.Equal("\"a\"\"b\"", _driver.Quote("a\"b"));
        Assert.Equal("\"plain\"", _driver.Quote("plain"));
    }

    [Fact]
    public void QuoteTable_WithSpaces()
    {
        Assert.Equal("\"Order Details\"", _driver.QuoteTable(new TableRef(null, "Order Details")));
        Assert.Equal("\"my schema\".\"Order Details\"",
            _driver.QuoteTable(new TableRef("my schema", "Order Details")));
    }

    [Fact]
    public void TableRef_ToString_IsSchemaDotNameOrName()
    {
        Assert.Equal("dbo.orders", new TableRef("dbo", "orders").ToString());
        Assert.Equal("orders", new TableRef(null, "orders").ToString());
    }

    [Fact]
    public void PageSql_UsesLimitOffset()
    {
        Assert.Equal("SELECT * FROM t ORDER BY id, name LIMIT 200 OFFSET 400",
            _driver.PageSql("SELECT * FROM t", "id, name", 400, 200));
        Assert.Equal("SELECT * FROM t LIMIT 200 OFFSET 0",
            _driver.PageSql("SELECT * FROM t", "", 0, 200));
    }

    [Theory]
    [InlineData(-1, 200)]
    [InlineData(0, 0)]
    [InlineData(0, -5)]
    public void PageSql_RejectsInvalidPaging(int offset, int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _driver.PageSql("SELECT 1", "", offset, limit));
    }

    [Fact]
    public void CastToText_UsesCastAsText()
    {
        Assert.Equal("CAST(\"c\" AS TEXT)", _driver.CastToText("\"c\""));
    }

    [Fact]
    public void EscapeLike_EscapesWildcardsAndEscapeChar()
    {
        Assert.Equal(@"50\%\_a\\b", _driver.EscapeLike(@"50%_a\b"));
    }

    [Fact]
    public void Metadata_IsSqliteWithAtPrefix()
    {
        Assert.Equal(DbKind.Sqlite, _driver.Kind);
        Assert.Equal("@", _driver.ParamPrefix);
    }

    [Fact]
    public async Task CreateConnection_UsesFileDatabase()
    {
        using var conn = _driver.CreateConnection(Config(), null);
        Assert.Equal(_db.Path, conn.DataSource);

        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'customers'";
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CreateConnection_MissingFile_FailsInsteadOfCreatingIt()
    {
        var missing = Path.Combine(Path.GetTempPath(), "dbExplorer-missing-" + Guid.NewGuid().ToString("N") + ".db");
        using var conn = _driver.CreateConnection(Config(missing), null);

        await Assert.ThrowsAsync<SqliteException>(() => conn.OpenAsync());
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void DriverFactory_ReturnsSqliteDriver()
    {
        Assert.IsType<SqliteDriver>(DriverFactory.For(DbKind.Sqlite));
    }
}
