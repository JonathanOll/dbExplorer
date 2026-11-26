using DbExplorer.Core.Diagrams;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DbExplorer.Core.Tests;

public sealed class DiagramTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"dbx-diagram-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_file); } catch (IOException) { }
    }

    private async Task<IReadOnlyList<TableInfo>> LoadAsync(string sql)
    {
        var cs = $"Data Source={_file};Pooling=False";
        await using (var setup = new SqliteConnection(cs))
        {
            await setup.OpenAsync();
            await using var cmd = setup.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        await using var c = new SqliteConnection(cs);
        return await new SqliteDriver().LoadSchemaAsync(c, CancellationToken.None);
    }

    private const string Shop = """
        CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
        CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES Customers);
        CREATE TABLE lines (order_id INTEGER NOT NULL REFERENCES orders(id), n INTEGER NOT NULL, PRIMARY KEY (order_id, n));
        CREATE TABLE notes (line_order INTEGER, line_n INTEGER, text TEXT,
          FOREIGN KEY (line_order, line_n) REFERENCES lines(order_id, n));
        CREATE TABLE invoices (id INTEGER PRIMARY KEY, order_id INTEGER NOT NULL REFERENCES orders(id));
        CREATE UNIQUE INDEX ux_invoice_order ON invoices(order_id);
        CREATE TABLE staff (id INTEGER PRIMARY KEY, boss INTEGER REFERENCES staff(id));
        CREATE TABLE alone (x TEXT);
        CREATE TABLE ghost (id INTEGER PRIMARY KEY, nowhere INTEGER REFERENCES missing(id));
        CREATE VIEW v AS SELECT * FROM orders;
        """;

    private static int Index(DiagramModel m, string name) =>
        m.Tables.Select((t, i) => (t, i)).Single(x => x.t.Ref.Name == name).i;

    private static DiagramRelation Relation(DiagramModel m, string child) => m.Relations.Single(r => r.Child == Index(m, child));

    [Fact]
    public async Task Build_ReadsTablesKeysAndCardinalities_FromASqliteFile()
    {
        var model = DiagramBuilder.Build(await LoadAsync(Shop), new DiagramScope());

        Assert.Equal(["alone", "customers", "ghost", "invoices", "lines", "notes", "orders", "staff"], model.Tables.Select(t => t.Ref.Name));
        Assert.Equal(5, model.Relations.Count);   // the key to a missing table is left out

        var orders = Relation(model, "orders");
        Assert.Equal(Index(model, "customers"), orders.Parent);   // REFERENCES in another case, implicit column
        Assert.Equal(["id"], orders.RefColumns);
        Assert.False(orders.Optional);
        Assert.False(orders.Unique);

        var notes = Relation(model, "notes");
        Assert.Equal(["line_order", "line_n"], notes.Columns);
        Assert.Equal(["order_id", "n"], notes.RefColumns);
        Assert.True(notes.Optional);

        Assert.True(Relation(model, "invoices").Unique);
        Assert.False(Relation(model, "lines").Unique);   // part of a composite key only
        Assert.True(Relation(model, "staff").IsSelf);

        var lines = model.Tables[Index(model, "lines")];
        Assert.True(lines.Columns[0] is { IsPrimaryKey: true, IsForeignKey: true });
        Assert.True(lines.Columns[1] is { IsPrimaryKey: true, IsForeignKey: false });
    }

    [Fact]
    public async Task Build_AroundATable_FollowsKeysBothWays_UpToTheDepth()
    {
        var schema = await LoadAsync(Shop);
        string[] Around(int depth) => DiagramBuilder.Build(schema, new DiagramScope(Focus: new TableRef(null, "orders"), Depth: depth))
            .Tables.Select(t => t.Ref.Name).ToArray();

        Assert.Equal(["customers", "invoices", "lines", "orders"], Around(1));
        Assert.Equal(["customers", "invoices", "lines", "notes", "orders"], Around(2));
        Assert.Empty(DiagramBuilder.Build(schema, new DiagramScope(Focus: new TableRef(null, "dropped"))).Tables);
    }

    [Fact]
    public void Build_FiltersBySchema_AndDropsRelationsLeavingIt()
    {
        var id = new ColumnInfo("id", "int", typeof(int), false, true, 0);
        TableInfo Table(string schema, string name, params ForeignKeyInfo[] keys) =>
            new(new TableRef(schema, name), false, [id], keys, []);
        var schema = new[]
        {
            Table("a", "t", new ForeignKeyInfo("f", ["id"], new TableRef("b", "T"), ["id"])),
            Table("b", "T"),
            Table("b", "u", new ForeignKeyInfo("g", ["id"], new TableRef("b", "T"), ["id"])),
        };

        Assert.Equal(["a", "b"], DiagramBuilder.Schemas(schema));
        var onlyB = DiagramBuilder.Build(schema, new DiagramScope(new HashSet<string> { "b" }));
        Assert.Equal(2, onlyB.Tables.Count);
        Assert.Single(onlyB.Relations);
        Assert.Equal(2, DiagramBuilder.Build(schema, new DiagramScope()).Relations.Count);
    }

    // ---- layout ----

    private static DiagramModel Graph(int tables, params (int Child, int Parent)[] links)
    {
        var column = new DiagramColumn("id", "integer", false, true, false);
        return new DiagramModel(
            Enumerable.Range(0, tables).Select(i => new DiagramTable(new TableRef(null, $"t{i:000}"), [column])).ToList(),
            links.Select((l, i) => new DiagramRelation($"fk{i}", l.Child, l.Parent, ["id"], ["id"], false, false)).ToList());
    }

    private static void AssertNoOverlap(DiagramPlacement placement)
    {
        var rects = placement.Tables;
        for (var i = 0; i < rects.Count; i++)
        {
            Assert.True(rects[i].X >= 0 && rects[i].Y >= 0 && rects[i].Right <= placement.Width && rects[i].Bottom <= placement.Height,
                $"table {i} leaves the canvas: {rects[i]}");
            for (var j = i + 1; j < rects.Count; j++)
                Assert.False(rects[i].Intersects(rects[j]), $"tables {i} and {j} overlap: {rects[i]} {rects[j]}");
        }
    }

    [Fact]
    public void Layout_PutsReferencedTablesOnTheLeft()
    {
        var placement = DiagramLayout.Arrange(Graph(3, (1, 0), (2, 1)), DiagramColumnMode.All);

        Assert.True(placement.Tables[0].Right < placement.Tables[1].X);
        Assert.True(placement.Tables[1].Right < placement.Tables[2].X);
        AssertNoOverlap(placement);
    }

    [Fact]
    public void Layout_CopesWithCyclesSelfReferencesAndLoneTables()
    {
        var model = Graph(7, (0, 1), (1, 2), (2, 0), (3, 3), (4, 5), (5, 4));

        var placement = DiagramLayout.Arrange(model, DiagramColumnMode.All);

        AssertNoOverlap(placement);
        Assert.Equal(placement.Tables, DiagramLayout.Arrange(model, DiagramColumnMode.All).Tables);   // deterministic
    }

    [Fact]
    public void Layout_OfHundredsOfTables_StaysCompactAndWithoutOverlap()
    {
        // A hub referenced by 150 tables, a chain of 150, and 200 tables on their own.
        var links = Enumerable.Range(1, 150).Select(i => (i, 0))
            .Concat(Enumerable.Range(152, 149).Select(i => (i, i - 1))).ToArray();

        var placement = DiagramLayout.Arrange(Graph(501, links), DiagramColumnMode.All);

        AssertNoOverlap(placement);
        Assert.True(placement.Height < 12_000, $"height {placement.Height}");   // the 150 children are wrapped into columns
    }

    [Fact]
    public void Layout_CanBeCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => DiagramLayout.Arrange(Graph(2, (1, 0)), DiagramColumnMode.All, ct: cts.Token));
    }

    [Fact]
    public async Task ColumnModes_ChangeWhatACardListsAndItsHeight()
    {
        var model = DiagramBuilder.Build(await LoadAsync(Shop), new DiagramScope());
        var notes = Index(model, "notes");

        var all = DiagramScene.Create(model, DiagramColumnMode.All);
        var keys = DiagramScene.Create(model, DiagramColumnMode.Keys);
        var names = DiagramScene.Create(model, DiagramColumnMode.Names);

        Assert.Equal(3, all.Columns(notes).Count);
        Assert.Equal(2, keys.Columns(notes).Count);
        Assert.Empty(names.Columns(notes));
        Assert.True(all.Rect(notes).Height > keys.Rect(notes).Height);
        Assert.Equal(DiagramMetrics.HeaderHeight, names.Rect(notes).Height);
    }

    [Fact]
    public void Scene_MovesHitsAndFindsTables()
    {
        var scene = DiagramScene.Create(Graph(12, (1, 0)), DiagramColumnMode.All);
        var changed = 0;
        scene.Changed += () => changed++;

        scene.Move(3, scene.Width + 500, -80);

        Assert.Equal(1, changed);
        Assert.Equal(0, scene.Rect(3).Y);   // never above the canvas
        Assert.Equal(scene.Rect(3).Right + DiagramLayout.Margin, scene.Width);
        Assert.Equal(3, scene.HitTest(scene.Rect(3).CenterX, scene.Rect(3).CenterY));
        Assert.Equal(3, scene.DrawOrder[^1]);
        Assert.Equal(-1, scene.HitTest(-5, -5));
        Assert.True(scene.AreLinked(0, 1));
        Assert.False(scene.AreLinked(0, 2));
        Assert.Equal([1, 10, 11], scene.Find("t001").Concat(scene.Find("t01")));
        Assert.Empty(scene.Find("  "));
    }

    [Fact]
    public void Routes_LeaveTheFacingSides_AndLoopOnASelfReference()
    {
        var left = new DiagramRect(0, 0, 100, 80);
        var right = new DiagramRect(300, 0, 100, 80);

        var apart = DiagramRoute.Between(right, 40, left, 40, self: false);
        var stacked = DiagramRoute.Between(new DiagramRect(0, 200, 100, 80), 40, left, 40, self: false);
        var self = DiagramRoute.Between(left, 40, left, 40, self: true);

        Assert.Equal((300, 100, -1, 1), (apart.Start.X, apart.End.X, apart.StartSide, apart.EndSide));
        Assert.Equal(stacked.StartSide, stacked.EndSide);
        Assert.True(self.Control1.X > left.Right);
        Assert.NotEqual(self.Start.Y, self.End.Y);
    }
}
