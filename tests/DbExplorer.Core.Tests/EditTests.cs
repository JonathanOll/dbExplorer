using System.Data.Common;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

internal static class EditTestData
{
    public static ColumnInfo Col(string name, Type clr, bool pk = false, bool nullable = true, int ordinal = 0) =>
        new(name, clr.Name, clr, nullable, pk, ordinal);

    public static TableInfo Table(string? schema, string name, bool isView, params ColumnInfo[] cols) =>
        new(new TableRef(schema, name), isView, cols, [], []);

    public static ConnectionConfig Conn(Guid id, bool readOnly = false) =>
        new(id, "c", DbKind.Sqlite, null, null, null, "x.db", null, null, false, false, readOnly);

    public static IReadOnlyDictionary<string, object?> Row(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value);
}

public class KeyResolverTests
{
    private static readonly Guid ConnId = Guid.NewGuid();

    private static AppConfig ConfigWith(string tableKey, params string[] columns)
    {
        var cfg = AppConfig.Empty();
        cfg.VirtualKeys[ConnId] = new Dictionary<string, string[]> { [tableKey] = columns };
        return cfg;
    }

    private static TableInfo NoPkTable(bool isView = false) => EditTestData.Table("dbo", "t", isView,
        EditTestData.Col("a", typeof(long), ordinal: 0),
        EditTestData.Col("b", typeof(string), ordinal: 1),
        EditTestData.Col("c", typeof(string), ordinal: 2));

    [Fact]
    public void Resolve_UsesPrimaryKey()
    {
        var t = EditTestData.Table(null, "t", false,
            EditTestData.Col("id", typeof(long), pk: true, nullable: false),
            EditTestData.Col("name", typeof(string), ordinal: 1));
        // A virtual key is configured too: the real primary key wins.
        var cfg = ConfigWith("t", "name");

        var key = KeyResolver.Resolve(t, EditTestData.Conn(ConnId), cfg);

        Assert.Equal(new[] { "id" }, key);
    }

    [Fact]
    public void Resolve_FallsBackToVirtualKey()
    {
        var key = KeyResolver.Resolve(NoPkTable(), EditTestData.Conn(ConnId), ConfigWith("dbo.t", "a", "b"));

        Assert.Equal(new[] { "a", "b" }, key);
    }

    [Fact]
    public void Resolve_VirtualStar_ExpandsToAllColumns()
    {
        var key = KeyResolver.Resolve(NoPkTable(), EditTestData.Conn(ConnId), ConfigWith("dbo.t", "*"));

        Assert.Equal(new[] { "a", "b", "c" }, key);
    }

    [Fact]
    public void Resolve_NoKey_ReturnsNull()
    {
        Assert.Null(KeyResolver.Resolve(NoPkTable(), EditTestData.Conn(ConnId), AppConfig.Empty()));
        // Virtual key of another connection or another table does not apply.
        Assert.Null(KeyResolver.Resolve(NoPkTable(), EditTestData.Conn(Guid.NewGuid()), ConfigWith("dbo.t", "a")));
        Assert.Null(KeyResolver.Resolve(NoPkTable(), EditTestData.Conn(ConnId), ConfigWith("dbo.other", "a")));
    }

    [Fact]
    public void Resolve_VirtualKeyWithUnknownOrEmptyColumns_ReturnsNull()
    {
        // The schema changed since the key was saved: refuse rather than build a wrong WHERE.
        Assert.Null(KeyResolver.Resolve(NoPkTable(), EditTestData.Conn(ConnId), ConfigWith("dbo.t", "a", "gone")));
        Assert.Null(KeyResolver.Resolve(NoPkTable(), EditTestData.Conn(ConnId), ConfigWith("dbo.t")));
    }

    [Fact]
    public void Resolve_ReadOnlyConnectionOrView_ReturnsNull()
    {
        var withPk = EditTestData.Table(null, "t", false, EditTestData.Col("id", typeof(long), pk: true));
        Assert.Null(KeyResolver.Resolve(withPk, EditTestData.Conn(ConnId, readOnly: true), AppConfig.Empty()));

        var view = EditTestData.Table(null, "v", true, EditTestData.Col("id", typeof(long), pk: true));
        Assert.Null(KeyResolver.Resolve(view, EditTestData.Conn(ConnId), AppConfig.Empty()));

        Assert.Null(KeyResolver.Resolve(NoPkTable(isView: true), EditTestData.Conn(ConnId), ConfigWith("dbo.t", "*")));
        Assert.Null(KeyResolver.Resolve(NoPkTable(), EditTestData.Conn(ConnId, readOnly: true), ConfigWith("dbo.t", "*")));
    }
}

public class EditPlannerTests
{
    private static readonly IDbDriver Sqlite = new SqliteDriver();

    private static TableInfo OrderLines() => EditTestData.Table(null, "order_lines", false,
        EditTestData.Col("order_id", typeof(long), pk: true, nullable: false, ordinal: 0),
        EditTestData.Col("line_no", typeof(long), pk: true, nullable: false, ordinal: 1),
        EditTestData.Col("product", typeof(string), ordinal: 2),
        EditTestData.Col("qty", typeof(long), ordinal: 3));

    [Fact]
    public void Plan_Update_WhereUsesAllKeyColumns()
    {
        var edit = new UpdateRow(
            EditTestData.Row(("order_id", 7L), ("line_no", 2L)),
            EditTestData.Row(("product", "bolt")));

        var stmt = Assert.Single(EditPlanner.Plan(Sqlite, OrderLines(), ["order_id", "line_no"], [edit]));

        Assert.Equal(
            "UPDATE \"order_lines\" SET \"product\" = @p0 WHERE \"order_id\" = @p1 AND \"line_no\" = @p2",
            stmt.Command.Sql);
        Assert.Equal(
            new[] { ("@p0", (object?)"bolt"), ("@p1", 7L), ("@p2", 2L) },
            stmt.Command.Params.Select(p => (p.Name, p.Value)).ToArray());
        Assert.Equal(
            "UPDATE \"order_lines\" SET \"product\" = 'bolt' WHERE \"order_id\" = 7 AND \"line_no\" = 2",
            stmt.PreviewSql);
    }

    [Fact]
    public void Plan_Update_MultipleChanges_AreAllSet()
    {
        var edit = new UpdateRow(
            EditTestData.Row(("order_id", 1L), ("line_no", 1L)),
            EditTestData.Row(("product", "nut"), ("qty", 5L)));

        var stmt = Assert.Single(EditPlanner.Plan(Sqlite, OrderLines(), ["order_id", "line_no"], [edit]));

        Assert.Equal(
            "UPDATE \"order_lines\" SET \"product\" = @p0, \"qty\" = @p1 WHERE \"order_id\" = @p2 AND \"line_no\" = @p3",
            stmt.Command.Sql);
    }

    [Fact]
    public void Plan_NullKeyValue_UsesIsNull()
    {
        // Virtual key on nullable columns: "= NULL" never matches, so the planner must emit IS NULL.
        var t = EditTestData.Table(null, "t", false,
            EditTestData.Col("a", typeof(string), ordinal: 0),
            EditTestData.Col("b", typeof(string), ordinal: 1));
        var edit = new UpdateRow(EditTestData.Row(("a", "x"), ("b", null)), EditTestData.Row(("b", "y")));

        var stmt = Assert.Single(EditPlanner.Plan(Sqlite, t, ["a", "b"], [edit]));

        Assert.Equal("UPDATE \"t\" SET \"b\" = @p0 WHERE \"a\" = @p1 AND \"b\" IS NULL", stmt.Command.Sql);
        Assert.Equal(new[] { "@p0", "@p1" }, stmt.Command.Params.Select(p => p.Name).ToArray());
        Assert.Equal("UPDATE \"t\" SET \"b\" = 'y' WHERE \"a\" = 'x' AND \"b\" IS NULL", stmt.PreviewSql);

        var del = Assert.Single(EditPlanner.Plan(Sqlite, t, ["a", "b"], [new DeleteRow(EditTestData.Row(("a", null), ("b", null)))]));
        Assert.Equal("DELETE FROM \"t\" WHERE \"a\" IS NULL AND \"b\" IS NULL", del.Command.Sql);
        Assert.Empty(del.Command.Params);
    }

    [Fact]
    public void Plan_Update_NullDiffersFromEmptyString()
    {
        var t = OrderLines();
        var key = EditTestData.Row(("order_id", 1L), ("line_no", 1L));

        var toNull = Assert.Single(EditPlanner.Plan(Sqlite, t, ["order_id", "line_no"],
            [new UpdateRow(key, EditTestData.Row(("product", null)))]));
        var toEmpty = Assert.Single(EditPlanner.Plan(Sqlite, t, ["order_id", "line_no"],
            [new UpdateRow(key, EditTestData.Row(("product", "")))]));

        Assert.Contains("\"product\" = NULL", toNull.Command.Sql);
        Assert.DoesNotContain(toNull.Command.Params, p => p.Value is string);
        Assert.Contains("\"product\" = NULL", toNull.PreviewSql);

        Assert.Contains("\"product\" = @p0", toEmpty.Command.Sql);
        Assert.Equal("", toEmpty.Command.Params[0].Value);
        Assert.Contains("\"product\" = ''", toEmpty.PreviewSql);
    }

    [Fact]
    public void Plan_Insert_And_Delete()
    {
        var t = OrderLines();
        var edits = new RowEdit[]
        {
            new InsertRow(EditTestData.Row(("order_id", 3L), ("line_no", 1L), ("product", null))),
            new DeleteRow(EditTestData.Row(("order_id", 9L), ("line_no", 4L))),
            new InsertRow(EditTestData.Row()),
        };

        var plan = EditPlanner.Plan(Sqlite, t, ["order_id", "line_no"], edits);

        Assert.Equal(3, plan.Count);
        Assert.Equal(
            "INSERT INTO \"order_lines\" (\"order_id\", \"line_no\", \"product\") VALUES (@p0, @p1, NULL)",
            plan[0].Command.Sql);
        Assert.Equal(new object?[] { 3L, 1L }, plan[0].Command.Params.Select(p => p.Value).ToArray());
        Assert.Equal(
            "INSERT INTO \"order_lines\" (\"order_id\", \"line_no\", \"product\") VALUES (3, 1, NULL)",
            plan[0].PreviewSql);

        Assert.Equal("DELETE FROM \"order_lines\" WHERE \"order_id\" = @p0 AND \"line_no\" = @p1", plan[1].Command.Sql);
        Assert.Equal("DELETE FROM \"order_lines\" WHERE \"order_id\" = 9 AND \"line_no\" = 4", plan[1].PreviewSql);

        Assert.Equal("INSERT INTO \"order_lines\" DEFAULT VALUES", plan[2].Command.Sql);
        Assert.Equal(new[] { false, true, false }, plan.Select(s => s.ExpectsSingleRow).ToArray());
    }

    [Fact]
    public void Plan_UsesSchemaQualifiedTableName()
    {
        var t = EditTestData.Table("dbo", "t", false, EditTestData.Col("id", typeof(long), pk: true));
        var plan = EditPlanner.Plan(Sqlite, t, ["id"], [new DeleteRow(EditTestData.Row(("id", 1L)))]);

        Assert.Equal("DELETE FROM \"dbo\".\"t\" WHERE \"id\" = @p0", plan[0].Command.Sql);
    }

    [Fact]
    public void Preview_EscapesSingleQuotes()
    {
        var edit = new UpdateRow(
            EditTestData.Row(("order_id", 1L), ("line_no", 1L)),
            EditTestData.Row(("product", "O'Brien'; DROP TABLE x;--")));

        var stmt = Assert.Single(EditPlanner.Plan(Sqlite, OrderLines(), ["order_id", "line_no"], [edit]));

        Assert.Contains("'O''Brien''; DROP TABLE x;--'", stmt.PreviewSql);
        // The executable command never carries the value inline.
        Assert.DoesNotContain("Brien", stmt.Command.Sql);
        Assert.Equal("O'Brien'; DROP TABLE x;--", stmt.Command.Params[0].Value);
    }

    [Fact]
    public void Preview_RendersCommonTypes()
    {
        var t = EditTestData.Table(null, "t", false,
            EditTestData.Col("id", typeof(long), pk: true),
            EditTestData.Col("d", typeof(DateTime)),
            EditTestData.Col("m", typeof(decimal)),
            EditTestData.Col("g", typeof(Guid)),
            EditTestData.Col("bin", typeof(byte[])));
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var edit = new InsertRow(EditTestData.Row(
            ("d", new DateTime(2024, 3, 5, 14, 7, 9, 120)),
            ("m", 12.5m),
            ("g", guid),
            ("bin", new byte[] { 0xAB, 0x01 })));

        var stmt = Assert.Single(EditPlanner.Plan(Sqlite, t, ["id"], [edit]));

        Assert.Equal(
            "INSERT INTO \"t\" (\"d\", \"m\", \"g\", \"bin\") VALUES ('2024-03-05 14:07:09.120', 12.5, '11111111-2222-3333-4444-555555555555', X'AB01')",
            stmt.PreviewSql);
    }

    [Fact]
    public void Plan_CoercesEditedTextToColumnClrType()
    {
        var t = EditTestData.Table(null, "t", false,
            EditTestData.Col("id", typeof(long), pk: true),
            EditTestData.Col("qty", typeof(long)),
            EditTestData.Col("created", typeof(DateTime)),
            EditTestData.Col("active", typeof(bool)),
            EditTestData.Col("name", typeof(string)));
        var edit = new UpdateRow(
            EditTestData.Row(("id", 1L)),
            EditTestData.Row(("qty", "42"), ("created", "2024-03-05 14:07:09"), ("active", "true"), ("name", "42")));

        var stmt = Assert.Single(EditPlanner.Plan(Sqlite, t, ["id"], [edit]));

        Assert.Equal(42L, stmt.Command.Params[0].Value);
        Assert.Equal(new DateTime(2024, 3, 5, 14, 7, 9), stmt.Command.Params[1].Value);
        Assert.Equal(true, stmt.Command.Params[2].Value);
        Assert.Equal("42", stmt.Command.Params[3].Value); // string column stays text
    }

    [Fact]
    public void Plan_KeyValuesAreBoundAsGiven()
    {
        // Key values come from the rows as read (SQLite hands back text for DATETIME): never re-typed.
        var t = EditTestData.Table(null, "t", false, EditTestData.Col("created", typeof(DateTime)));
        var stmt = Assert.Single(EditPlanner.Plan(Sqlite, t, ["created"],
            [new DeleteRow(EditTestData.Row(("created", "2024-03-05T14:07:09Z")))]));

        Assert.Equal("2024-03-05T14:07:09Z", stmt.Command.Params[0].Value);
    }

    [Fact]
    public void Plan_InvalidEditedValue_Throws()
    {
        var t = OrderLines();
        var edit = new UpdateRow(
            EditTestData.Row(("order_id", 1L), ("line_no", 1L)),
            EditTestData.Row(("qty", "abc")));

        var ex = Assert.Throws<InvalidEditException>(() => EditPlanner.Plan(Sqlite, t, ["order_id", "line_no"], [edit]));
        Assert.Equal("qty", ex.Column);
    }

    [Fact]
    public void Plan_UnknownColumn_Throws()
    {
        var t = OrderLines();
        var key = EditTestData.Row(("order_id", 1L), ("line_no", 1L));

        Assert.Throws<InvalidEditException>(() => EditPlanner.Plan(Sqlite, t, ["order_id", "line_no"],
            [new UpdateRow(key, EditTestData.Row(("nope", "x")))]));
        Assert.Throws<InvalidEditException>(() => EditPlanner.Plan(Sqlite, t, ["order_id", "line_no"],
            [new InsertRow(EditTestData.Row(("nope", "x")))]));
    }

    [Fact]
    public void Plan_UpdateOrDeleteWithoutUsableKey_Throws()
    {
        var t = OrderLines();
        var changes = EditTestData.Row(("qty", 1L));

        // An empty key would produce an unfiltered UPDATE/DELETE.
        Assert.Throws<ArgumentException>(() => EditPlanner.Plan(Sqlite, t, [],
            [new UpdateRow(EditTestData.Row(), changes)]));
        Assert.Throws<ArgumentException>(() => EditPlanner.Plan(Sqlite, t, [],
            [new DeleteRow(EditTestData.Row())]));
        // Key value missing for one of the key columns.
        Assert.Throws<ArgumentException>(() => EditPlanner.Plan(Sqlite, t, ["order_id", "line_no"],
            [new DeleteRow(EditTestData.Row(("order_id", 1L)))]));
        Assert.Throws<ArgumentException>(() => EditPlanner.Plan(Sqlite, t, ["order_id", "line_no"],
            [new UpdateRow(EditTestData.Row(("order_id", 1L)), changes)]));
        // Key column that does not exist.
        Assert.Throws<InvalidEditException>(() => EditPlanner.Plan(Sqlite, t, ["nope"],
            [new DeleteRow(EditTestData.Row(("nope", 1L)))]));
    }

    [Fact]
    public void Plan_View_Throws()
    {
        var v = EditTestData.Table(null, "v", true, EditTestData.Col("id", typeof(long)));

        Assert.Throws<InvalidOperationException>(() => EditPlanner.Plan(Sqlite, v, ["id"],
            [new DeleteRow(EditTestData.Row(("id", 1L)))]));
    }

    [Fact]
    public void Plan_UpdateWithNoChanges_IsSkipped()
    {
        var plan = EditPlanner.Plan(Sqlite, OrderLines(), ["order_id", "line_no"],
            [new UpdateRow(EditTestData.Row(("order_id", 1L), ("line_no", 1L)), EditTestData.Row())]);

        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_KeepsEditOrder()
    {
        var t = OrderLines();
        var plan = EditPlanner.Plan(Sqlite, t, ["order_id", "line_no"],
        [
            new DeleteRow(EditTestData.Row(("order_id", 1L), ("line_no", 1L))),
            new InsertRow(EditTestData.Row(("order_id", 2L), ("line_no", 1L))),
            new UpdateRow(EditTestData.Row(("order_id", 3L), ("line_no", 1L)), EditTestData.Row(("qty", 1L))),
        ]);

        Assert.StartsWith("DELETE", plan[0].Command.Sql);
        Assert.StartsWith("INSERT", plan[1].Command.Sql);
        Assert.StartsWith("UPDATE", plan[2].Command.Sql);
    }
}

public sealed class EditDbFixture : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "dbExplorer-edit-tests-" + Guid.NewGuid().ToString("N") + ".db");

    public EditDbFixture()
    {
        Execute("""
            CREATE TABLE items (
                id INTEGER PRIMARY KEY,
                name TEXT,
                qty INTEGER,
                created DATETIME,
                active BOOLEAN
            );
            INSERT INTO items (id, name, qty, created, active) VALUES
                (1, 'one', 10, '2024-01-01 10:00:00', 0),
                (2, 'two', 20, '2024-01-02 10:00:00', 1),
                (3, 'three', 30, NULL, NULL);
            CREATE TABLE loose (a TEXT, b TEXT);
            INSERT INTO loose (a, b) VALUES ('x', NULL), ('y', 'z');
            """);
    }

    public void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    public T Scalar<T>(string sql)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? default! : (T)Convert.ChangeType(v, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    public void Dispose()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}

public sealed class EditApplierTests : IDisposable
{
    private readonly EditDbFixture _db = new(); // fresh database per test
    private static readonly SqliteDriver Driver = new();

    public void Dispose() => _db.Dispose();

    private async Task<TableInfo> LoadTableAsync(string name)
    {
        await using var c = _db.Open();
        var tables = await Driver.LoadSchemaAsync(c, CancellationToken.None);
        return tables.Single(t => t.Ref.Name == name);
    }

    private async Task ApplyAsync(IReadOnlyList<PlannedStatement> plan)
    {
        await using var c = _db.Open();
        await EditApplier.ApplyAsync(c, plan, CancellationToken.None);
    }

    [Fact]
    public async Task Apply_Success_Commits()
    {
        var items = await LoadTableAsync("items");
        var plan = EditPlanner.Plan(Driver, items, ["id"],
        [
            new UpdateRow(EditTestData.Row(("id", 1L)), EditTestData.Row(("name", "uno"), ("qty", "11"))),
            new InsertRow(EditTestData.Row(("id", 100L), ("name", "new"))),
            new DeleteRow(EditTestData.Row(("id", 2L))),
        ]);

        await ApplyAsync(plan);

        Assert.Equal("uno", _db.Scalar<string>("SELECT name FROM items WHERE id = 1"));
        Assert.Equal(11L, _db.Scalar<long>("SELECT qty FROM items WHERE id = 1"));
        Assert.Equal("new", _db.Scalar<string>("SELECT name FROM items WHERE id = 100"));
        Assert.Equal(0L, _db.Scalar<long>("SELECT COUNT(*) FROM items WHERE id = 2"));
    }

    [Fact]
    public async Task Apply_StaleRow_RollsBackAndThrows()
    {
        var items = await LoadTableAsync("items");
        var plan = EditPlanner.Plan(Driver, items, ["id"],
        [
            new UpdateRow(EditTestData.Row(("id", 1L)), EditTestData.Row(("name", "changed"))),
            new UpdateRow(EditTestData.Row(("id", 3L)), EditTestData.Row(("name", "changed"))),
        ]);
        _db.Execute("DELETE FROM items WHERE id = 3"); // row removed after the user loaded it

        var ex = await Assert.ThrowsAsync<StaleRowException>(() => ApplyAsync(plan));

        Assert.Same(plan[1], ex.Statement);
        Assert.Equal("one", _db.Scalar<string>("SELECT name FROM items WHERE id = 1")); // first update not persisted
    }

    [Fact]
    public async Task Apply_StaleDelete_Throws()
    {
        var items = await LoadTableAsync("items");
        var plan = EditPlanner.Plan(Driver, items, ["id"], [new DeleteRow(EditTestData.Row(("id", 999L)))]);

        await Assert.ThrowsAsync<StaleRowException>(() => ApplyAsync(plan));
    }

    private IReadOnlyList<string> StarKeyOfLooseWithDuplicates(TableInfo loose)
    {
        _db.Execute("INSERT INTO loose (a, b) VALUES ('dup', 'dup'), ('dup', 'dup')");
        var cfg = AppConfig.Empty();
        var connId = Guid.NewGuid();
        cfg.VirtualKeys[connId] = new Dictionary<string, string[]> { ["loose"] = ["*"] };
        return KeyResolver.Resolve(loose, EditTestData.Conn(connId), cfg)!;
    }

    [Fact]
    public async Task Apply_UpdateMatchingDuplicateRows_RollsBackAndThrowsAmbiguous()
    {
        var items = await LoadTableAsync("items");
        var loose = await LoadTableAsync("loose");
        var key = StarKeyOfLooseWithDuplicates(loose);
        var plan = EditPlanner.Plan(Driver, items, ["id"],
            [new UpdateRow(EditTestData.Row(("id", 1L)), EditTestData.Row(("name", "changed")))]).Concat(
            EditPlanner.Plan(Driver, loose, key,
                [new UpdateRow(EditTestData.Row(("a", "dup"), ("b", "dup")), EditTestData.Row(("b", "edited")))])).ToList();

        var ex = await Assert.ThrowsAsync<AmbiguousRowException>(() => ApplyAsync(plan));

        Assert.Equal(2, ex.Affected);
        Assert.Same(plan[1], ex.Statement);
        Assert.Equal("one", _db.Scalar<string>("SELECT name FROM items WHERE id = 1")); // earlier statement rolled back
        Assert.Equal(2L, _db.Scalar<long>("SELECT COUNT(*) FROM loose WHERE a = 'dup' AND b = 'dup'"));
    }

    [Fact]
    public async Task Apply_DeleteMatchingDuplicateRows_RollsBackAndThrowsAmbiguous()
    {
        var items = await LoadTableAsync("items");
        var loose = await LoadTableAsync("loose");
        var key = StarKeyOfLooseWithDuplicates(loose);
        var plan = EditPlanner.Plan(Driver, items, ["id"],
            [new DeleteRow(EditTestData.Row(("id", 1L)))]).Concat(
            EditPlanner.Plan(Driver, loose, key,
                [new DeleteRow(EditTestData.Row(("a", "dup"), ("b", "dup")))])).ToList();

        var ex = await Assert.ThrowsAsync<AmbiguousRowException>(() => ApplyAsync(plan));

        Assert.Equal(2, ex.Affected);
        Assert.Equal(1L, _db.Scalar<long>("SELECT COUNT(*) FROM items WHERE id = 1"));
        Assert.Equal(2L, _db.Scalar<long>("SELECT COUNT(*) FROM loose WHERE a = 'dup' AND b = 'dup'"));
        Assert.Equal(4L, _db.Scalar<long>("SELECT COUNT(*) FROM loose"));
    }

    [Fact]
    public async Task Apply_Insert_IsNotRowCountChecked()
    {
        var items = await LoadTableAsync("items");
        var plan = EditPlanner.Plan(Driver, items, ["id"], [new InsertRow(EditTestData.Row(("id", 50L)))]);
        Assert.False(plan[0].ExpectsSingleRow);

        await ApplyAsync(plan);

        Assert.Equal(1L, _db.Scalar<long>("SELECT COUNT(*) FROM items WHERE id = 50"));
    }

    [Fact]
    public async Task Apply_DatabaseError_RollsBackEverything()
    {
        var items = await LoadTableAsync("items");
        var plan = EditPlanner.Plan(Driver, items, ["id"],
        [
            new UpdateRow(EditTestData.Row(("id", 1L)), EditTestData.Row(("name", "changed"))),
            new InsertRow(EditTestData.Row(("id", 2L), ("name", "duplicate pk"))),
        ]);

        await Assert.ThrowsAnyAsync<DbException>(() => ApplyAsync(plan));

        Assert.Equal("one", _db.Scalar<string>("SELECT name FROM items WHERE id = 1"));
    }

    [Fact]
    public async Task Apply_Empty_DoesNothing()
    {
        await ApplyAsync([]);
    }

    [Fact]
    public async Task Apply_CancelledToken_RollsBack()
    {
        var items = await LoadTableAsync("items");
        var plan = EditPlanner.Plan(Driver, items, ["id"],
            [new UpdateRow(EditTestData.Row(("id", 1L)), EditTestData.Row(("name", "changed")))]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await using var c = _db.Open();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EditApplier.ApplyAsync(c, plan, cts.Token));

        Assert.Equal("one", _db.Scalar<string>("SELECT name FROM items WHERE id = 1"));
    }

    [Fact]
    public async Task Apply_NullAndEmptyString_AreStoredDistinctly()
    {
        var items = await LoadTableAsync("items");
        await ApplyAsync(EditPlanner.Plan(Driver, items, ["id"],
        [
            new UpdateRow(EditTestData.Row(("id", 1L)), EditTestData.Row(("name", null))),
            new UpdateRow(EditTestData.Row(("id", 2L)), EditTestData.Row(("name", ""))),
        ]));

        Assert.Equal(1L, _db.Scalar<long>("SELECT COUNT(*) FROM items WHERE id = 1 AND name IS NULL"));
        Assert.Equal(1L, _db.Scalar<long>("SELECT COUNT(*) FROM items WHERE id = 2 AND name = ''"));
    }

    [Fact]
    public async Task Apply_TypedColumns_RoundTrip()
    {
        var items = await LoadTableAsync("items");
        await ApplyAsync(EditPlanner.Plan(Driver, items, ["id"],
        [
            new UpdateRow(EditTestData.Row(("id", 1L)),
                EditTestData.Row(("created", "2025-06-30 08:15:00"), ("active", "true"))),
        ]));

        Assert.Equal("2025-06-30 08:15:00", _db.Scalar<string>("SELECT created FROM items WHERE id = 1"));
        Assert.Equal(1L, _db.Scalar<long>("SELECT active FROM items WHERE id = 1"));
    }

    [Fact]
    public async Task Apply_VirtualKeyWithNullValue_MatchesRow()
    {
        var loose = await LoadTableAsync("loose");
        var cfg = AppConfig.Empty();
        var connId = Guid.NewGuid();
        cfg.VirtualKeys[connId] = new Dictionary<string, string[]> { ["loose"] = ["*"] };
        var key = KeyResolver.Resolve(loose, EditTestData.Conn(connId), cfg);
        Assert.NotNull(key);

        await ApplyAsync(EditPlanner.Plan(Driver, loose, key!,
            [new UpdateRow(EditTestData.Row(("a", "x"), ("b", null)), EditTestData.Row(("b", "filled")))]));

        Assert.Equal(1L, _db.Scalar<long>("SELECT COUNT(*) FROM loose WHERE a = 'x' AND b = 'filled'"));
        Assert.Equal(1L, _db.Scalar<long>("SELECT COUNT(*) FROM loose WHERE a = 'y' AND b = 'z'"));
    }

    [Fact]
    public async Task Apply_InjectionAttemptInValue_IsStoredLiterally()
    {
        var items = await LoadTableAsync("items");
        const string evil = "x'); DROP TABLE items;--";

        await ApplyAsync(EditPlanner.Plan(Driver, items, ["id"],
            [new UpdateRow(EditTestData.Row(("id", 1L)), EditTestData.Row(("name", evil)))]));

        Assert.Equal(evil, _db.Scalar<string>("SELECT name FROM items WHERE id = 1"));
        Assert.Equal(3L, _db.Scalar<long>("SELECT COUNT(*) FROM items"));
    }
}
