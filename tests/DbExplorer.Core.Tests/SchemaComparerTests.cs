using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Tests;

public class SchemaComparerTests
{
    private static ColumnInfo Col(string name, string type = "int", int ordinal = 0) =>
        new(name, type, typeof(object), true, false, ordinal);

    private static TableInfo Table(string name, bool isView = false, params ColumnInfo[] cols) =>
        new(new TableRef(null, name), isView, cols, Array.Empty<ForeignKeyInfo>(), Array.Empty<IndexInfo>());

    private static TableInfo Table(string name, params ColumnInfo[] cols) => Table(name, false, cols);

    private static SchemaMember Member(string db, params TableInfo[] tables) => new(db, tables);

    [Fact]
    public void Identical_Schemas_ReportNothing()
    {
        var result = SchemaComparer.Compare(new[]
        {
            Member("a", Table("T", Col("Id"), Col("Name", "text"))),
            Member("b", Table("T", Col("Id"), Col("Name", "text"))),
        });

        Assert.Equal(2, result.Count);
        Assert.All(result, d =>
        {
            Assert.Empty(d.MissingTables);
            Assert.Empty(d.ExtraTables);
            Assert.Empty(d.ColumnDiffs);
        });
    }

    [Fact]
    public void MissingAndExtraTables_ArePerMember()
    {
        var result = SchemaComparer.Compare(new[]
        {
            Member("a", Table("Shared", Col("Id")), Table("OnlyA", Col("Id")), Table("AB", Col("Id"))),
            Member("b", Table("Shared", Col("Id")), Table("AB", Col("Id"))),
            Member("c", Table("Shared", Col("Id")), Table("OnlyC", Col("Id"))),
        });

        var a = result.Single(d => d.Database == "a");
        var b = result.Single(d => d.Database == "b");
        var c = result.Single(d => d.Database == "c");

        Assert.Equal(new[] { "OnlyC" }, a.MissingTables);
        Assert.Equal(new[] { "OnlyA" }, a.ExtraTables);
        Assert.Equal(new[] { "OnlyA", "OnlyC" }, b.MissingTables.OrderBy(x => x));
        Assert.Empty(b.ExtraTables);
        Assert.Equal(new[] { "AB", "OnlyA" }, c.MissingTables.OrderBy(x => x));
        Assert.Equal(new[] { "OnlyC" }, c.ExtraTables);
    }

    [Fact]
    public void ColumnMissingExtraAndTypeDiffers()
    {
        var result = SchemaComparer.Compare(new[]
        {
            Member("a", Table("T", Col("Id"), Col("Name", "text"), Col("Age", "int"))),
            Member("b", Table("T", Col("Id"), Col("Age", "bigint"), Col("Extra"))),
        });

        var a = result.Single(d => d.Database == "a");
        var b = result.Single(d => d.Database == "b");

        // a: Name only here -> Extra; Age type tie -> first member's (int), so a has no diff; Extra col missing (Expected = type elsewhere).
        Assert.Contains(new ColumnDiff("T", "Name", "Extra", null, "text"), a.ColumnDiffs);
        Assert.Contains(new ColumnDiff("T", "Extra", "Missing", "int", null), a.ColumnDiffs);
        Assert.DoesNotContain(a.ColumnDiffs, x => x.Column == "Age");
        Assert.Equal(2, a.ColumnDiffs.Count);

        Assert.Contains(new ColumnDiff("T", "Name", "Missing", "text", null), b.ColumnDiffs);
        Assert.Contains(new ColumnDiff("T", "Extra", "Extra", null, "int"), b.ColumnDiffs);
        Assert.Contains(new ColumnDiff("T", "Age", "TypeDiffers", "int", "bigint"), b.ColumnDiffs);
        Assert.Equal(3, b.ColumnDiffs.Count);
    }

    [Fact]
    public void TypeDiffers_UsesTheMostCommonType()
    {
        var result = SchemaComparer.Compare(new[]
        {
            Member("a", Table("T", Col("X", "text"))),
            Member("b", Table("T", Col("X", "int"))),
            Member("c", Table("T", Col("X", "int"))),
        });

        var a = result.Single(d => d.Database == "a");
        Assert.Equal(new ColumnDiff("T", "X", "TypeDiffers", "int", "text"), Assert.Single(a.ColumnDiffs));
        Assert.Empty(result.Single(d => d.Database == "b").ColumnDiffs);
        Assert.Empty(result.Single(d => d.Database == "c").ColumnDiffs);
    }

    [Fact]
    public void NamesAreCaseInsensitive()
    {
        var result = SchemaComparer.Compare(new[]
        {
            Member("a", Table("Users", Col("Id"), Col("Name", "TEXT"))),
            Member("b", Table("USERS", Col("ID"), Col("name", "text"))),
        });

        Assert.All(result, d =>
        {
            Assert.Empty(d.MissingTables);
            Assert.Empty(d.ExtraTables);
            Assert.Empty(d.ColumnDiffs);
        });
    }

    [Fact]
    public void ViewVersusTable_IsReported()
    {
        var result = SchemaComparer.Compare(new[]
        {
            Member("a", Table("V", false, Col("Id"))),
            Member("b", Table("V", true, Col("Id"))),
            Member("c", Table("V", false, Col("Id"))),
        });

        var b = result.Single(d => d.Database == "b");
        Assert.Equal(new ColumnDiff("V", "(object kind)", "TypeDiffers", "table", "view"), Assert.Single(b.ColumnDiffs));
        Assert.Empty(result.Single(d => d.Database == "a").ColumnDiffs);
        Assert.Empty(result.Single(d => d.Database == "c").ColumnDiffs);
    }

    [Fact]
    public void SingleMember_ReportsNothing()
    {
        var result = SchemaComparer.Compare(new[] { Member("a", Table("T", Col("Id"))) });

        var d = Assert.Single(result);
        Assert.Equal("a", d.Database);
        Assert.Empty(d.MissingTables);
        Assert.Empty(d.ExtraTables);
        Assert.Empty(d.ColumnDiffs);
    }
}
