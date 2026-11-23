using DbExplorer.Core.Groups;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Tests;

public class GroupResultsTests
{
    private static ResultSet Set(string[] cols, params object?[][] rows) => new(cols, rows, false);

    private static MemberSet Member(string db, ResultSet set) => new(db, set);

    private static object?[] R(params object?[] values) => values;

    [Fact]
    public void Merge_PrependsDatabaseColumn_InMemberOrder()
    {
        var merged = GroupResults.Merge(new[]
        {
            Member("A", Set(new[] { "Id", "Name" }, R(1, "x"), R(2, "y"))),
            Member("B", Set(new[] { "Id", "Name" }, R(3, "z"))),
        });

        Assert.Equal(new[] { "Database", "Id", "Name" }, merged.Columns);
        Assert.Equal(3, merged.Rows.Count);
        Assert.Equal(new object?[] { "A", 1, "x" }, merged.Rows[0]);
        Assert.Equal(new object?[] { "A", 2, "y" }, merged.Rows[1]);
        Assert.Equal(new object?[] { "B", 3, "z" }, merged.Rows[2]);
        Assert.Empty(merged.Warnings);
        Assert.False(merged.Truncated);
    }

    [Fact]
    public void Merge_DifferentColumnSets_UsesCommonColumns_AndWarns()
    {
        var merged = GroupResults.Merge(new[]
        {
            Member("A", Set(new[] { "Id", "Name", "OnlyA" }, R(1, "x", "a"))),
            Member("B", Set(new[] { "NAME", "id", "OnlyB" }, R("y", 2, "b"))),
        });

        Assert.Equal(new[] { "Database", "Id", "Name" }, merged.Columns);
        Assert.Equal(new object?[] { "A", 1, "x" }, merged.Rows[0]);
        Assert.Equal(new object?[] { "B", 2, "y" }, merged.Rows[1]);
        Assert.Equal(2, merged.Warnings.Count);
        Assert.Contains(merged.Warnings, w => w.Contains("A") && w.Contains("OnlyA"));
        Assert.Contains(merged.Warnings, w => w.Contains("B") && w.Contains("OnlyB"));
    }

    [Fact]
    public void Merge_TruncatedWhenAnyMemberTruncated()
    {
        var merged = GroupResults.Merge(new[]
        {
            Member("A", Set(new[] { "Id" }, R(1))),
            Member("B", new ResultSet(new[] { "Id" }, new[] { R(2) }, true)),
        });

        Assert.True(merged.Truncated);
    }

    [Fact]
    public void Differences_IdenticalSets_ReturnsNothing()
    {
        var diff = GroupResults.Differences(new[]
        {
            Member("A", Set(new[] { "Id", "Name" }, R(1, "x"), R(2, "y"))),
            Member("B", Set(new[] { "Id", "Name" }, R(2, "y"), R(1, "x"))),
        });

        Assert.Equal(new[] { "Present in", "Id", "Name" }, diff.Columns);
        Assert.Empty(diff.Rows);
    }

    [Fact]
    public void Differences_RowMissingInOneMember_IsListedWithPresentIn()
    {
        var diff = GroupResults.Differences(new[]
        {
            Member("A", Set(new[] { "Id" }, R(1), R(2))),
            Member("B", Set(new[] { "Id" }, R(1))),
            Member("C", Set(new[] { "Id" }, R(1), R(2))),
        });

        var row = Assert.Single(diff.Rows);
        Assert.Equal(new object?[] { "A, C", 2 }, row);
    }

    [Fact]
    public void Differences_DuplicateCountsDiffer_AreAnnotated()
    {
        var diff = GroupResults.Differences(new[]
        {
            Member("A", Set(new[] { "Id" }, R(1), R(1))),
            Member("B", Set(new[] { "Id" }, R(1))),
        });

        var row = Assert.Single(diff.Rows);
        Assert.Equal(new object?[] { "A×2, B×1", 1 }, row);
    }

    [Fact]
    public void Differences_TreatsNullAndEmptyStringAsDifferent()
    {
        var diff = GroupResults.Differences(new[]
        {
            Member("A", Set(new[] { "V" }, R((object?)null))),
            Member("B", Set(new[] { "V" }, R(""))),
        });

        Assert.Equal(2, diff.Rows.Count);
        Assert.Equal(new object?[] { "A", null }, diff.Rows[0]);
        Assert.Equal(new object?[] { "B", "" }, diff.Rows[1]);
    }

    [Fact]
    public void Differences_NumbersOfDifferentRuntimeTypes_AreEqual()
    {
        var diff = GroupResults.Differences(new[]
        {
            Member("A", Set(new[] { "N" }, R(1L))),
            Member("B", Set(new[] { "N" }, R(1))),
            Member("C", Set(new[] { "N" }, R(1.0m))),
            Member("D", Set(new[] { "N" }, R(1.0d))),
        });

        Assert.Empty(diff.Rows);
    }

    [Fact]
    public void Differences_BinaryCompareByContent()
    {
        var same = GroupResults.Differences(new[]
        {
            Member("A", Set(new[] { "Bin" }, R(new byte[] { 1, 2, 3 }))),
            Member("B", Set(new[] { "Bin" }, R(new byte[] { 1, 2, 3 }))),
        });
        Assert.Empty(same.Rows);

        var different = GroupResults.Differences(new[]
        {
            Member("A", Set(new[] { "Bin" }, R(new byte[] { 1, 2, 3 }))),
            Member("B", Set(new[] { "Bin" }, R(new byte[] { 1, 2, 4 }))),
        });
        Assert.Equal(2, different.Rows.Count);
    }

    [Fact]
    public void Differences_BoolOneVsTrueAreEqual()
    {
        var diff = GroupResults.Differences(new[]
        {
            Member("Sqlite", Set(new[] { "Flag" }, R(1L), R(0L))),
            Member("Postgres", Set(new[] { "Flag" }, R(true), R(false))),
        });

        Assert.Empty(diff.Rows);
    }

    [Fact]
    public void Differences_StringsAreCaseSensitive()
    {
        var diff = GroupResults.Differences(new[]
        {
            Member("A", Set(new[] { "S" }, R("abc"))),
            Member("B", Set(new[] { "S" }, R("ABC"))),
        });

        Assert.Equal(2, diff.Rows.Count);
    }

    [Fact]
    public void Differences_RowKeyIsUnambiguousAcrossColumnBoundaries()
    {
        var diff = GroupResults.Differences(new[]
        {
            Member("A", Set(new[] { "X", "Y" }, R("a|b", "c"))),
            Member("B", Set(new[] { "X", "Y" }, R("a", "b|c"))),
        });

        Assert.Equal(2, diff.Rows.Count);
    }

    [Fact]
    public void Differences_DifferentColumnSets_UsesCommonColumns_AndWarns()
    {
        var diff = GroupResults.Differences(new[]
        {
            Member("A", Set(new[] { "Id", "OnlyA" }, R(1, "a"))),
            Member("B", Set(new[] { "id", "OnlyB" }, R(1, "b"))),
        });

        Assert.Equal(new[] { "Present in", "Id" }, diff.Columns);
        Assert.Empty(diff.Rows);
        Assert.Equal(2, diff.Warnings.Count);
    }

    [Fact]
    public void Canonical_NormalisesValues()
    {
        Assert.NotEqual(GroupResults.Canonical(null), GroupResults.Canonical(""));
        Assert.Equal("1", GroupResults.Canonical(1L));
        Assert.Equal("1", GroupResults.Canonical(1.0m));
        Assert.Equal("1.5", GroupResults.Canonical(1.5d));
        Assert.Equal("1.5", GroupResults.Canonical(1.5f));
        Assert.Equal("0", GroupResults.Canonical(0.00m));
        Assert.Equal("1", GroupResults.Canonical(true));
        Assert.Equal("0", GroupResults.Canonical(false));
        Assert.Equal("0102FF", GroupResults.Canonical(new byte[] { 1, 2, 255 }));
        var g = Guid.NewGuid();
        Assert.Equal(g.ToString("D").ToLowerInvariant(), GroupResults.Canonical(g));
        var utc = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Assert.Equal(GroupResults.Canonical(utc), GroupResults.Canonical(new DateTimeOffset(DateTime.SpecifyKind(utc.AddHours(2), DateTimeKind.Unspecified), TimeSpan.FromHours(2))));
        Assert.Equal("Abc", GroupResults.Canonical("Abc"));
    }
}
