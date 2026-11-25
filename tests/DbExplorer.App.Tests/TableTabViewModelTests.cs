using System.Collections;
using System.Data;
using System.Data.Common;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;
using DbExplorer.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace DbExplorer.App.Tests;

// ---- a driver whose connections log every executed SQL text ----

internal sealed class SqlLog
{
    private readonly List<string> _items = [];
    public void Add(string sql) { lock (_items) _items.Add(sql); }
    public void Clear() { lock (_items) _items.Clear(); }

    /// <summary>When set, a scalar "SELECT 1" (the script connection's health probe) fails like an aborted PostgreSQL transaction.</summary>
    public volatile bool FailProbe;
    public IReadOnlyList<string> Items { get { lock (_items) return _items.ToList(); } }
}

internal sealed class RecordingDriver(IDbDriver inner, SqlLog log) : IDbDriver
{
    public DbKind Kind => inner.Kind;
    public string ParamPrefix => inner.ParamPrefix;
    public string Quote(string identifier) => inner.Quote(identifier);
    public string QuoteTable(TableRef t) => inner.QuoteTable(t);
    public string CastToText(string quotedColumn) => inner.CastToText(quotedColumn);
    public string EscapeLike(string text) => inner.EscapeLike(text);
    public string PageSql(string selectSql, string orderBy, int offset, int limit) => inner.PageSql(selectSql, orderBy, offset, limit);
    public DbConnection CreateConnection(ConnectionConfig cfg, string? plainPassword) =>
        new RecordingConnection(inner.CreateConnection(cfg, plainPassword), log);
    public Task<IReadOnlyList<TableInfo>> LoadSchemaAsync(DbConnection c, CancellationToken ct) =>
        inner.LoadSchemaAsync(((RecordingConnection)c).Inner, ct);
}

internal sealed class RecordingConnection(DbConnection inner, SqlLog log) : DbConnection
{
    public DbConnection Inner { get; } = inner;
    public SqlLog Log { get; } = log;
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString { get => Inner.ConnectionString; set => Inner.ConnectionString = value; }
    public override string Database => Inner.Database;
    public override string DataSource => Inner.DataSource;
    public override string ServerVersion => Inner.ServerVersion;
    public override System.Data.ConnectionState State => Inner.State;
    public override void ChangeDatabase(string databaseName) => Inner.ChangeDatabase(databaseName);
    public override void Close() => Inner.Close();
    public override void Open() => Inner.Open();
    public override Task OpenAsync(CancellationToken cancellationToken) => Inner.OpenAsync(cancellationToken);
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => Inner.BeginTransaction(isolationLevel);
    protected override DbCommand CreateDbCommand() => new RecordingCommand(Inner.CreateCommand(), this);
    protected override void Dispose(bool disposing)
    {
        if (disposing) Inner.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class RecordingCommand(DbCommand inner, RecordingConnection connection) : DbCommand
{
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText { get => inner.CommandText; set => inner.CommandText = value; }
    public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
    public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
    protected override DbConnection? DbConnection { get => connection; set { } }
    protected override DbParameterCollection DbParameterCollection => inner.Parameters;
    protected override DbTransaction? DbTransaction { get => inner.Transaction; set => inner.Transaction = value; }
    public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
    public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
    public override void Cancel() => inner.Cancel();
    public override void Prepare() => inner.Prepare();
    protected override DbParameter CreateDbParameter() => inner.CreateParameter();
    public override int ExecuteNonQuery() { connection.Log.Add(inner.CommandText); return inner.ExecuteNonQuery(); }
    public override object? ExecuteScalar()
    {
        connection.Log.Add(inner.CommandText);
        if (connection.Log.FailProbe && inner.CommandText == "SELECT 1")
            throw new InvalidOperationException("current transaction is aborted, commands ignored until end of transaction block");
        return inner.ExecuteScalar();
    }
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) { connection.Log.Add(inner.CommandText); return inner.ExecuteReader(behavior); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A synthetic SQLite database (customers, orders, a 500-row table, composite and self FKs) plus helpers.</summary>
internal sealed class TableTabFixture : IDisposable
{
    public TreeFixture Fx { get; } = new();
    public SqlLog Log { get; } = new();
    public SessionManager Sessions { get; }
    public ConnectionConfig Config { get; }
    public string DbFile { get; }
    public List<(Session Session, TableRef Table, IReadOnlyDictionary<string, string>? Filters)> Opened { get; } = [];

    public TableTabFixture(bool readOnly = false)
    {
        DbFile = Path.Combine(Fx.Dir, "data.db");
        using (var c = new SqliteConnection($"Data Source={DbFile};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, boss_id INTEGER REFERENCES customers(id));
                INSERT INTO customers VALUES (1, 'Alice', NULL), (2, 'Bob', 1), (3, 'Carol', 1), (4, 'Dave', 2);
                CREATE INDEX ix_customers_name ON customers(name);
                CREATE TABLE orders (
                    id INTEGER PRIMARY KEY, customer_id INTEGER REFERENCES customers(id),
                    total REAL, created DATETIME, paid BOOLEAN, note TEXT, payload BLOB);
                INSERT INTO orders VALUES
                    (10, 1, 12.5, '2024-01-05 10:00:00', 1, 'first', X'0102030405'),
                    (11, 2, 99.99, '2024-02-06 11:30:00', 0, NULL, NULL),
                    (12, NULL, 5, '2024-03-07 00:00:00', 1, 'no customer', NULL);
                CREATE TABLE big (id INTEGER PRIMARY KEY, label TEXT NOT NULL);
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 500)
                INSERT INTO big SELECT i, 'row ' || i FROM n;
                CREATE TABLE pairs (a INTEGER, b INTEGER, label TEXT, PRIMARY KEY (a, b));
                INSERT INTO pairs VALUES (1, 1, 'one-one'), (1, 2, 'one-two'), (2, 1, 'two-one');
                CREATE TABLE links (id INTEGER PRIMARY KEY, a INTEGER, b INTEGER, FOREIGN KEY (a, b) REFERENCES pairs(a, b));
                INSERT INTO links VALUES (1, 1, 2), (2, 2, 1), (3, NULL, 1), (4, 1, NULL);
                CREATE TABLE nopk (x INTEGER, y TEXT);
                CREATE VIEW v_big AS SELECT * FROM big;
                """;
            cmd.ExecuteNonQuery();
        }

        Config = TreeFixture.Conn("Lite", database: DbFile) with { ReadOnly = readOnly };
        Sessions = new SessionManager(Fx.Store, Fx.Cache, Fx.Protector, _ => new RecordingDriver(new SqliteDriver(), Log));
    }

    public Task<Session> OpenSession() => Sessions.GetOrOpenAsync(Config, CancellationToken.None);

    public TableTabHooks Hooks => new(
        ct => Sessions.GetOrOpenAsync(Config, ct).ContinueWith(t => (Session?)t.Result, ct),
        (s, t, f) =>
        {
            Opened.Add((s, t, f));
            return Task.CompletedTask;
        });

    public async Task<TableTabViewModel> OpenTab(string table, bool load = true, bool hooks = true)
    {
        var session = await OpenSession();
        var tab = new TableTabViewModel(session, new TableRef(null, table), hooks ? Hooks : null);
        if (load) await tab.LoadAsync();
        return tab;
    }

    public long Scalar(string sql)
    {
        using var c = new SqliteConnection($"Data Source={DbFile};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Dispose()
    {
        Sessions.Close(Config.Id);
        Fx.Dispose();
    }
}

public class TableTabViewModelTests : IDisposable
{
    private readonly TableTabFixture _t = new();

    public void Dispose() => _t.Dispose();

    private static object? Cell(TableTabViewModel tab, int row, string column) => tab.Rows[row].Values[tab.FindColumn(column)!.Value];

    private static List<long> Ids(TableTabViewModel tab, string column = "id") =>
        tab.Rows.Select(r => (long)r.Values[tab.FindColumn(column)!.Value]!).ToList();

    [Fact]
    public async Task Title_IsConnectionAndTable()
    {
        var tab = await _t.OpenTab("customers", load: false);
        Assert.Equal("Lite · customers", tab.Title);
    }

    [Fact]
    public async Task Load_ReturnsFirst200Rows()
    {
        var tab = await _t.OpenTab("big");

        Assert.Equal(200, tab.Rows.Count);
        Assert.Equal(1L, Ids(tab).First());
        Assert.Equal(200L, Ids(tab).Last());
        Assert.True(tab.HasNextPage);
        Assert.Equal(0, tab.PageIndex);
        Assert.Null(tab.ErrorMessage);
        Assert.Equal(["id", "label"], tab.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task Load_SmallTable_HasNoNextPage()
    {
        var tab = await _t.OpenTab("customers");

        Assert.Equal(4, tab.Rows.Count);
        Assert.False(tab.HasNextPage);
    }

    [Fact]
    public async Task NextPage_ReturnsRows200To399()
    {
        var tab = await _t.OpenTab("big");

        await tab.NextPage();

        Assert.Equal(1, tab.PageIndex);
        Assert.Equal(200, tab.Rows.Count);
        Assert.Equal(201L, Ids(tab).First());
        Assert.Equal(400L, Ids(tab).Last());
    }

    [Fact]
    public async Task NextPage_PastTheEnd_DoesNothing_AndPrevPageGoesBack()
    {
        var tab = await _t.OpenTab("big");
        await tab.NextPage();
        await tab.NextPage();
        Assert.Equal(100, tab.Rows.Count);
        Assert.False(tab.HasNextPage);

        await tab.NextPage();
        Assert.Equal(2, tab.PageIndex);

        await tab.PrevPage();
        Assert.Equal(1, tab.PageIndex);
        Assert.Equal(201L, Ids(tab).First());

        await tab.PrevPage();
        await tab.PrevPage();
        Assert.Equal(0, tab.PageIndex);
    }

    [Fact]
    public async Task ColumnFilter_AppliesServerSide()
    {
        var tab = await _t.OpenTab("big");

        tab.ColumnFilters["label"] = "row 47";   // matches only rows beyond the first 200 (470-479) and 47
        await tab.ApplyFiltersAsync();

        Assert.Equal(new long[] { 47, 470, 471, 472, 473, 474, 475, 476, 477, 478, 479 }, Ids(tab));
    }

    [Fact]
    public async Task ApplyFilters_ResetsToFirstPage()
    {
        var tab = await _t.OpenTab("big");
        await tab.NextPage();

        tab.ColumnFilters["id"] = ">10";
        await tab.ApplyFiltersAsync();

        Assert.Equal(0, tab.PageIndex);
        Assert.Equal(11L, Ids(tab).First());
    }

    [Fact]
    public async Task RawWhere_AndColumnFilter_AreCombined()
    {
        var tab = await _t.OpenTab("big");

        tab.RawWhere = "id > 100 AND id < 300";
        tab.ColumnFilters["label"] = "9";
        await tab.ApplyFiltersAsync();

        var expected = Enumerable.Range(101, 199).Where(i => $"row {i}".Contains('9')).Select(i => (long)i).ToList();
        Assert.Equal(expected, Ids(tab));
    }

    [Fact]
    public async Task FilterByCell_NullValue_UsesNullFilter()
    {
        var tab = await _t.OpenTab("customers");
        var aliceRow = Enumerable.Range(0, tab.Rows.Count).Single(i => (string?)Cell(tab, i, "name") == "Alice");

        await tab.FilterByCellAsync(aliceRow, "boss_id");

        Assert.Equal("null", tab.ColumnFilters["boss_id"]);
        Assert.Equal([1L], Ids(tab));
    }

    [Fact]
    public async Task FilterByCell_Value_UsesEqualsFilter_AndKeepsOtherFilters()
    {
        var tab = await _t.OpenTab("customers");
        tab.ColumnFilters["name"] = "o";   // Bob, Carol
        await tab.ApplyFiltersAsync();
        var bob = Enumerable.Range(0, tab.Rows.Count).Single(i => (string?)Cell(tab, i, "name") == "Bob");

        await tab.FilterByCellAsync(bob, "boss_id");

        Assert.Equal("=1", tab.ColumnFilters["boss_id"]);
        Assert.Equal("o", tab.ColumnFilters["name"]);
        Assert.Equal([2L, 3L], Ids(tab));
    }

    [Fact]
    public async Task FilterByCell_SqliteDateTimeAndBoolean_MatchDespiteClrTypeMismatch()
    {
        // Microsoft.Data.Sqlite returns DATETIME as string and BOOLEAN as long although ColumnInfo says DateTime/bool.
        var tab = await _t.OpenTab("orders");
        Assert.IsType<string>(Cell(tab, 0, "created"));
        Assert.IsType<long>(Cell(tab, 0, "paid"));

        await tab.FilterByCellAsync(0, "created");
        Assert.Single(tab.Rows);
        Assert.Equal(10L, Cell(tab, 0, "id"));

        tab.ColumnFilters.Clear();
        await tab.ApplyFiltersAsync();
        var unpaid = Enumerable.Range(0, tab.Rows.Count).Single(i => (long)Cell(tab, i, "id")! == 11L);
        await tab.FilterByCellAsync(unpaid, "paid");
        Assert.Equal([11L], Ids(tab));
    }

    [Fact]
    public async Task FilterByCell_BinaryValue_SetsError_KeepsRows()
    {
        var tab = await _t.OpenTab("orders");
        var before = tab.Rows.Count;

        await tab.FilterByCellAsync(0, "payload");

        Assert.NotNull(tab.ErrorMessage);
        Assert.Equal(before, tab.Rows.Count);
        Assert.False(tab.ColumnFilters.ContainsKey("payload"));
    }

    [Fact]
    public async Task FollowForeignKey_OpensTabWithFilter()
    {
        var tab = await _t.OpenTab("orders");
        var fk = tab.Info!.ForeignKeys.Single();

        await tab.FollowForeignKeyAsync(0, fk);   // order 10 -> customer 1

        var (_, table, filters) = Assert.Single(_t.Opened);
        Assert.Equal(new TableRef(null, "customers"), table);
        Assert.Equal("=1", Assert.Single(filters!).Value);
        Assert.Equal("id", filters!.Keys.Single());
    }

    [Fact]
    public async Task FollowForeignKey_ByColumnName_FindsTheKey()
    {
        var tab = await _t.OpenTab("orders");

        Assert.True(tab.IsForeignKeyColumn("CUSTOMER_ID"));
        Assert.False(tab.IsForeignKeyColumn("total"));
        await tab.FollowForeignKeyAsync(1, "customer_id");   // order 11 -> customer 2

        Assert.Equal("=2", Assert.Single(_t.Opened).Filters!["id"]);
    }

    [Fact]
    public async Task FollowForeignKey_CompositeKey_UsesAllColumns()
    {
        var tab = await _t.OpenTab("links");
        var fk = tab.Info!.ForeignKeys.Single();

        await tab.FollowForeignKeyAsync(0, fk);   // link 1 -> pairs (1, 2)

        var (_, table, filters) = Assert.Single(_t.Opened);
        Assert.Equal("pairs", table.Name);
        Assert.Equal(2, filters!.Count);
        Assert.Equal("=1", filters["a"]);
        Assert.Equal("=2", filters["b"]);
    }

    [Fact]
    public async Task FollowForeignKey_SelfReference_OpensNewTabOfSameTable()
    {
        var tab = await _t.OpenTab("customers");
        var bob = Enumerable.Range(0, tab.Rows.Count).Single(i => (string?)Cell(tab, i, "name") == "Bob");

        await tab.FollowForeignKeyAsync(bob, tab.Info!.ForeignKeys.Single());

        var (_, table, filters) = Assert.Single(_t.Opened);
        Assert.Equal(new TableRef(null, "customers"), table);
        Assert.Equal("=1", filters!["id"]);
    }

    [Fact]
    public async Task FollowForeignKey_NullValue_DoesNothing()
    {
        var customers = await _t.OpenTab("customers");
        var alice = Enumerable.Range(0, customers.Rows.Count).Single(i => (string?)Cell(customers, i, "name") == "Alice");
        await customers.FollowForeignKeyAsync(alice, customers.Info!.ForeignKeys.Single());

        var orders = await _t.OpenTab("orders");
        var noCustomer = Enumerable.Range(0, orders.Rows.Count).Single(i => (long)Cell(orders, i, "id")! == 12L);
        await orders.FollowForeignKeyAsync(noCustomer, orders.Info!.ForeignKeys.Single());

        var links = await _t.OpenTab("links");   // composite: one NULL part -> no reference
        foreach (var i in new[] { 2, 3 }) await links.FollowForeignKeyAsync(i, links.Info!.ForeignKeys.Single());

        Assert.Empty(_t.Opened);
    }

    [Fact]
    public async Task Count_OnlyWhenRequested()
    {
        var tab = await _t.OpenTab("big", load: false);
        _t.Log.Clear();

        await tab.LoadAsync();
        await tab.NextPage();
        tab.ColumnFilters["label"] = "row 1";
        await tab.ApplyFiltersAsync();
        Assert.DoesNotContain(_t.Log.Items, sql => sql.Contains("COUNT(", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(_t.Scalar("SELECT COUNT(*) FROM big WHERE label LIKE '%row 1%'"), tab.TotalCount);   // a last page is its own count: no query

        var count = await tab.CountAsync();

        Assert.Single(_t.Log.Items, sql => sql.Contains("COUNT(", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(_t.Scalar("SELECT COUNT(*) FROM big WHERE label LIKE '%row 1%'"), count);
        Assert.Equal(count, tab.TotalCount);
    }

    [Fact]
    public async Task Count_UsesTheAppliedFilters_NotUnappliedBoxText()
    {
        var tab = await _t.OpenTab("big");
        tab.ColumnFilters["id"] = "<=10";   // typed but not applied

        Assert.Equal(500, await tab.CountAsync());
    }

    [Fact]
    public async Task Count_IsClearedWhenTheQueryChanges()
    {
        var tab = await _t.OpenTab("big");
        await tab.CountAsync();

        tab.ColumnFilters["id"] = "<=300";   // still more than one page: the total is unknown until counted
        await tab.ApplyFiltersAsync();

        Assert.Null(tab.TotalCount);
    }

    [Fact]
    public async Task SortToggle_AscDescNone()
    {
        var tab = await _t.OpenTab("big");
        Assert.Null(tab.Sort);

        await tab.ToggleSortAsync("label");
        Assert.Equal(new SortSpec("label", false), tab.Sort);
        Assert.Equal("row 1", Cell(tab, 0, "label"));
        Assert.Equal("row 10", Cell(tab, 1, "label"));   // text order, not numeric

        await tab.ToggleSortAsync("label");
        Assert.Equal(new SortSpec("label", true), tab.Sort);
        Assert.Equal("row 99", Cell(tab, 0, "label"));

        await tab.ToggleSortAsync("label");
        Assert.Null(tab.Sort);
        Assert.Equal(1L, Ids(tab).First());
    }

    [Fact]
    public async Task SortToggle_OtherColumn_StartsAscending()
    {
        var tab = await _t.OpenTab("big");
        await tab.ToggleSortAsync("label");
        await tab.ToggleSortAsync("label");

        await tab.ToggleSortAsync("id");

        Assert.Equal(new SortSpec("id", false), tab.Sort);
    }

    [Fact]
    public async Task FindColumn_CaseInsensitive()
    {
        var tab = await _t.OpenTab("orders");

        Assert.Equal(1, tab.FindColumn("CUSTOMER_ID"));
        Assert.Equal(0, tab.FindColumn("id"));
        Assert.Null(tab.FindColumn("nope"));
    }

    [Fact]
    public async Task FocusColumn_RaisesRequestWithTheIndex()
    {
        var tab = await _t.OpenTab("orders");
        var requested = new List<int>();
        tab.FocusColumnRequested += requested.Add;

        Assert.True(tab.FocusColumn("Total"));
        Assert.False(tab.FocusColumn("nope"));

        Assert.Equal([2], requested);
    }

    [Fact]
    public async Task BadFilterValue_SetsErrorMessage_KeepsPreviousRows()
    {
        var tab = await _t.OpenTab("big");
        var before = tab.Rows.ToList();

        tab.ColumnFilters["id"] = "=abc";
        await tab.ApplyFiltersAsync();

        Assert.Contains("id", tab.ErrorMessage);
        Assert.Equal(before, tab.Rows);
        Assert.False(tab.NeedsReload);

        // A later good query clears the error.
        tab.ColumnFilters["id"] = "=5";
        await tab.ApplyFiltersAsync();
        Assert.Null(tab.ErrorMessage);
        Assert.Equal([5L], Ids(tab));
    }

    [Fact]
    public async Task FailedFilter_DoesNotChangeWhatPagingUses()
    {
        var tab = await _t.OpenTab("big");
        tab.ColumnFilters["id"] = "=abc";
        await tab.ApplyFiltersAsync();

        await tab.NextPage();   // pages the last good query (unfiltered), not the broken one

        Assert.Null(tab.ErrorMessage);
        Assert.Equal(201L, Ids(tab).First());
    }

    [Fact]
    public async Task BadRawWhere_SetsErrorMessage_KeepsPreviousRows()
    {
        var tab = await _t.OpenTab("big");

        tab.RawWhere = "no_such_column = 1";
        await tab.ApplyFiltersAsync();

        Assert.NotNull(tab.ErrorMessage);
        Assert.Equal(200, tab.Rows.Count);
    }

    [Fact]
    public async Task RawWhere_MultipleStatements_IsRejected_AndNothingRuns()
    {
        var tab = await _t.OpenTab("big");
        _t.Log.Clear();

        tab.RawWhere = "1 = 1; DELETE FROM big";
        await tab.ApplyFiltersAsync();

        Assert.Contains("single", tab.ErrorMessage);
        Assert.Empty(_t.Log.Items);
        Assert.Equal(500, _t.Scalar("SELECT COUNT(*) FROM big"));
        Assert.Equal(200, tab.Rows.Count);
    }

    [Fact]
    public async Task RawWhere_WriteStatement_OnReadOnlyConnection_IsRejected_AndNothingRuns()
    {
        using var ro = new TableTabFixture(readOnly: true);
        var tab = await ro.OpenTab("big");
        ro.Log.Clear();

        tab.RawWhere = "id < 3 DELETE FROM big";   // no ';' : one statement to the splitter
        await tab.ApplyFiltersAsync();

        Assert.Contains("write", tab.ErrorMessage);
        Assert.Empty(ro.Log.Items);
        Assert.Equal(500, ro.Scalar("SELECT COUNT(*) FROM big"));
    }

    [Fact]
    public async Task ClosedConnection_ShowsReloadMessage_KeepsRows_AndReloadReopens()
    {
        var tab = await _t.OpenTab("big");
        _t.Sessions.Close(_t.Config.Id);   // Disconnect disposes the session the tab holds

        await tab.NextPage();

        Assert.Equal(TableTabViewModel.ConnectionClosedMessage, tab.ErrorMessage);
        Assert.True(tab.NeedsReload);
        Assert.Equal(200, tab.Rows.Count);
        Assert.Equal(0, tab.PageIndex);

        await tab.ReloadAsync();

        Assert.Null(tab.ErrorMessage);
        Assert.False(tab.NeedsReload);
        Assert.Equal(200, tab.Rows.Count);
    }

    [Fact]
    public async Task ClosedConnection_Count_ShowsReloadMessage_NoCrash()
    {
        var tab = await _t.OpenTab("big");
        _t.Sessions.Close(_t.Config.Id);

        var count = await tab.CountAsync();

        Assert.Equal(-1, count);
        Assert.True(tab.NeedsReload);
    }

    [Fact]
    public async Task Reload_WhenSessionCannotBeResolved_ShowsError()
    {
        var session = await _t.OpenSession();
        var tab = new TableTabViewModel(session, new TableRef(null, "big"),
            new TableTabHooks(_ => Task.FromResult<Session?>(null), (_, _, _) => Task.CompletedTask));
        await tab.LoadAsync();
        _t.Sessions.Close(_t.Config.Id);
        await tab.NextPage();

        await tab.ReloadAsync();

        Assert.NotNull(tab.ErrorMessage);
        Assert.True(tab.NeedsReload);
    }

    [Fact]
    public async Task Reload_WhenTheTableIsGone_ShowsError()
    {
        var session = await _t.OpenSession();
        var tab = new TableTabViewModel(session, new TableRef(null, "ghost"), _t.Hooks);

        await tab.LoadAsync();

        Assert.Contains("ghost", tab.ErrorMessage);
    }

    [Fact]
    public async Task Rows_AreRowViewModels_WithOriginalValues()
    {
        var tab = await _t.OpenTab("customers");

        var row = tab.Rows[0];
        Assert.False(row.IsNew);
        Assert.False(row.IsDeleted);
        Assert.False(row.IsDirty(1));
        Assert.Equal(row.Values, row.Original);
        Assert.NotSame(row.Values, row.Original);

        row.Values[1] = "changed";
        Assert.True(row.IsDirty(1));
        Assert.False(row.IsDirty(0));
    }

    [Fact]
    public async Task Structure_ListsColumnsKeysAndIndexes()
    {
        var tab = await _t.OpenTab("orders");

        var structure = tab.Structure;
        Assert.Equal(["id", "customer_id", "total", "created", "paid", "note", "payload"], structure.Columns.Select(c => c.Name));
        var id = structure.Columns[0];
        Assert.True(id.IsPrimaryKey);
        Assert.Equal("PK", id.Key);
        var customer = structure.Columns[1];
        Assert.Equal("FK", customer.Key);
        Assert.Equal("customers (id)", customer.References);
        Assert.Equal("INTEGER", customer.DataType);
        Assert.True(customer.Nullable);

        var fk = Assert.Single(structure.ForeignKeys);
        Assert.Equal("customer_id", fk.Columns);
        Assert.Equal("customers (id)", fk.Target);

        var customers = await _t.OpenTab("customers");
        var index = Assert.Single(customers.Structure.Indexes);
        Assert.Equal("ix_customers_name", index.Name);
        Assert.Equal("name", index.Columns);
        Assert.False(index.Unique);
    }

    [Fact]
    public async Task Structure_ForeignKeyTarget_OpensThatTable()
    {
        var tab = await _t.OpenTab("orders");

        await tab.Structure.ForeignKeys.Single().OpenAsync();
        await tab.Structure.Columns[1].OpenReferenceAsync();
        await tab.Structure.Columns[0].OpenReferenceAsync();   // not a FK: nothing

        Assert.Equal(2, _t.Opened.Count);
        Assert.All(_t.Opened, o => Assert.Equal(new TableRef(null, "customers"), o.Table));
        Assert.All(_t.Opened, o => Assert.Null(o.Filters));
    }

    [Fact]
    public async Task View_IsReadOnlyFlagged_AndLoads()
    {
        var tab = await _t.OpenTab("v_big");

        Assert.True(tab.IsView);
        Assert.Equal(200, tab.Rows.Count);
    }

    [Fact]
    public async Task GridColumns_CarryForeignKeyTarget()
    {
        var tab = await _t.OpenTab("orders");

        Assert.Null(tab.GridColumns[0].LinkTarget);
        Assert.Equal("customers (id)", tab.GridColumns[1].LinkTarget);
        Assert.True(tab.GridColumns[0].IsPrimaryKey);
    }

    [Fact]
    public async Task SchemaDrift_ExtraOrMissingReaderColumns_DoNotCrash()
    {
        var session = await _t.OpenSession();
        var tab = new TableTabViewModel(session, new TableRef(null, "customers"), _t.Hooks);
        await tab.LoadAsync();
        // Simulate a stale schema: the live table gained a column since the schema was loaded.
        using (var c = new SqliteConnection($"Data Source={_t.DbFile};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "ALTER TABLE customers ADD COLUMN extra TEXT";
            cmd.ExecuteNonQuery();
        }

        await tab.ApplyFiltersAsync();

        Assert.Null(tab.ErrorMessage);
        Assert.Equal(["id", "name", "boss_id", "extra"], tab.Columns.Select(c => c.Name));
        Assert.Equal(4, tab.GridColumns.Count);
    }

    [Fact]
    public async Task MainViewModel_UseTableTabs_BuildsRealTabs_AndFollowOpensFilteredTab()
    {
        var session = await _t.OpenSession();
        var tree = _t.Fx.CreateTree(new AppConfig([], [_t.Config], new()));
        var main = new MainViewModel(tree, _t.Fx.Status);
        main.UseTableTabs(_t.Sessions);

        await main.OpenTable(session, new TableRef(null, "orders"));
        var orders = Assert.IsType<TableTabViewModel>(Assert.Single(main.Tabs));
        await orders.LoadAsync();   // (the open also started a load; the latest one wins)
        await orders.FollowForeignKeyAsync(0, orders.Info!.ForeignKeys.Single());

        Assert.Equal(2, main.Tabs.Count);
        var customers = Assert.IsType<TableTabViewModel>(main.ActiveTab);
        Assert.Equal("Lite · customers", customers.Title);
        Assert.Equal("=1", customers.ColumnFilters["id"]);
        await customers.LoadAsync();
        Assert.Equal([1L], Ids(customers));
    }
}
