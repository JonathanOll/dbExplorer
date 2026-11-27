using System.Diagnostics;
using DbExplorer.Core.Comparing;

namespace DbExplorer.Core.Tests;

public class ResultComparerTests
{
    private static ComparedData Data(string columns, params object?[][] rows) =>
        new(columns.Split(',').Select(c => new ComparedColumn(c)).ToList(), rows);

    private static ComparisonResult Run(ComparedData left, ComparedData right, string keys = "id", bool ignoreCase = false, bool ignoreSpaces = false)
    {
        var pairs = ColumnMatching.Match(left, right).Pairs;
        var key = keys.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(k => pairs.Single(p => p.Name == k)).ToList();
        return ResultComparer.Compare(left, right, new ComparisonOptions(key, pairs, ignoreCase, ignoreSpaces));
    }

    [Fact]
    public void ByKey_FindsSameChangedAddedAndRemoved_InTheOrderOfTheLeft()
    {
        var left = Data("id,name", [1L, "a"], [2L, "b"], [3L, "c"]);
        var right = Data("id,name", [0, "z"], [3, "c"], [1, "A"], [4, "d"]);

        var result = Run(left, right);

        Assert.Equal((1, 1, 2, 1), (result.Same, result.Changed, result.Added, result.Removed));
        Assert.Equal(4, result.Differences);
        Assert.Equal(
            [(RowChange.Added, -1, 0), (RowChange.Changed, 0, 2), (RowChange.Added, -1, 3), (RowChange.Removed, 1, -1), (RowChange.Same, 2, 1)],
            result.Rows.Select(r => (r.Change, r.Left, r.Right)));
        Assert.Equal([1], result.Rows[1].Changed);
        Assert.False(result.Duplicates.Any);
    }

    [Fact]
    public void ByPosition_PairsRowsInOrder_AndTheRestIsOneSided()
    {
        var result = Run(Data("id", [1], [2], [3]), Data("id", [1], [9]), keys: "");

        Assert.Equal([RowChange.Same, RowChange.Changed, RowChange.Removed], result.Rows.Select(r => r.Change));
        Assert.Equal(1, Run(Data("id", [1]), Data("id", [1], [2]), keys: "").Added);
    }

    [Fact]
    public void NumbersAcrossTypes_AreEqual_AndNullEqualsOnlyNull()
    {
        var left = Data("id,n,s", [1, 5, null], [2, 1.5m, ""]);
        var right = Data("id,n,s", [1L, 5L, DBNull.Value], [2L, 1.5d, null]);

        var result = Run(left, right);

        Assert.Equal(1, result.Same);
        Assert.Equal([2], result.Rows[1].Changed);
    }

    [Fact]
    public void TextDatesAndGuids_EqualTypedOnes()
    {
        var guid = Guid.NewGuid();
        var left = Data("id,at,g,day", [1, "2025-03-01 10:00:00", guid.ToString().ToUpperInvariant(), "2025-03-02"]);
        var right = Data("id,at,g,day", [1, new DateTime(2025, 3, 1, 10, 0, 0), guid, new DateOnly(2025, 3, 2)]);

        Assert.Equal(1, Run(left, right).Same);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, true, 1)]
    public void TextOptions_ApplyToKeysAndValues(bool ignoreCase, bool ignoreSpaces, int same)
    {
        var left = Data("id,name", ["A-1", "Two  words"]);
        var right = Data("id,name", [" a-1", "two words "]);

        Assert.Equal(same, Run(left, right, ignoreCase: ignoreCase, ignoreSpaces: ignoreSpaces).Same);
    }

    [Fact]
    public void ColumnsNotCompared_DoNotMakeADifference()
    {
        var left = Data("id,a,b", [1, "x", "y"]);
        var right = Data("id,a,b", [1, "x", "changed"]);
        var pairs = ColumnMatching.Match(left, right).Pairs;

        var result = ResultComparer.Compare(left, right, new ComparisonOptions([pairs[0]], [pairs[1]]));

        Assert.Equal(1, result.Same);
    }

    [Fact]
    public void DuplicateKeys_AreReported_AndPairedInOrder()
    {
        var left = Data("id,v", [1, "a"], [1, "b"], [1, "c"], [2, "d"]);
        var right = Data("id,v", [1, "a"], [1, "x"], [2, "d"], [2, "e"]);

        var result = Run(left, right);

        Assert.Equal((2, 1, 1, 1), (result.Same, result.Changed, result.Added, result.Removed));
        Assert.Equal(3, result.Duplicates.LeftRows);
        Assert.Equal(4, result.Duplicates.RightRows);
        Assert.Contains("id = 1", result.Duplicates.Examples);
    }

    [Fact]
    public void ColumnMatching_ByNameIgnoringCase_ReportsOneSidedColumnsAndTypes()
    {
        var left = new ComparedData([new("Id", "int"), new("a"), new("gone")], [[1, "x", 1]]);
        var right = new ComparedData([new("new"), new("A"), new("ID", "bigint")], [[1, 2L, 1L]]);

        var match = ColumnMatching.Match(left, right);

        Assert.Equal([new ColumnPair("Id", 0, 2), new ColumnPair("a", 1, 1)], match.Pairs);
        Assert.Equal(["gone"], match.OnlyLeft);
        Assert.Equal(["new"], match.OnlyRight);
        Assert.Equal(["Id: int / bigint", "a: String / Int64"], match.TypeNotes);
    }

    private static KeyGuess Guess(ComparedData left, ComparedData right) =>
        KeyGuesser.Guess(left, right, ColumnMatching.Match(left, right).Pairs);

    [Fact]
    public void KeyGuess_PrefersThePrimaryKey()
    {
        var data = new ComparedData([new("name"), new("code", IsPrimaryKey: true)], [["a", 1], ["b", 2]]);

        Assert.Equal(["code"], Guess(data, data).Keys.Select(k => k.Name));
    }

    [Fact]
    public void KeyGuess_TakesAUniqueColumn_IdFirst_SkippingNullsAndFractions()
    {
        var data = Data("price,label,id", [1.5, "a", 1], [2.5, "b", 2]);
        Assert.Equal(["id"], Guess(data, data).Keys.Select(k => k.Name));

        var other = Data("price,label,note", [1.5, "a", null], [2.5, "b", "n"]);
        Assert.Equal(["label"], Guess(other, other).Keys.Select(k => k.Name));
    }

    [Fact]
    public void KeyGuess_NeedsUniquenessOnBothSides_ThenTriesTwoColumns_ThenGivesUp()
    {
        var left = Data("id,line", [1, 1], [2, 2]);
        var right = Data("id,line", [1, 1], [1, 2]);
        Assert.Equal(["line"], Guess(left, right).Keys.Select(k => k.Name));

        var pairs = Data("order_id,line", [1, 1], [1, 2], [2, 1]);
        Assert.Equal(["order_id", "line"], Guess(pairs, pairs).Keys.Select(k => k.Name));

        var same = Data("a", [1], [1]);
        var none = Guess(same, same);
        Assert.Empty(none.Keys);
        Assert.Contains("position", none.Reason);
    }

    [Fact]
    public void Cancelled_Throws()
    {
        var data = Data("id", [1]);
        var pairs = ColumnMatching.Match(data, data).Pairs;
        var ct = new CancellationToken(canceled: true);

        Assert.ThrowsAny<OperationCanceledException>(() => ResultComparer.Compare(data, data, new ComparisonOptions(pairs, pairs), ct));
        Assert.ThrowsAny<OperationCanceledException>(() => KeyGuesser.Guess(data, data, pairs, ct));
    }

    [Fact]
    public void HundredThousandRows_StayFast()
    {
        object?[][] Rows(int changeEvery) => Enumerable.Range(0, 100_000)
            .Select(i => new object?[] { (long)i, "name " + i, i % changeEvery == 0 ? "x" : "y", i * 1.5, new DateTime(2025, 1, 1).AddMinutes(i), null, i % 7, "text" })
            .ToArray();
        var left = Data("id,a,b,c,d,e,f,g", Rows(100_000));
        var right = Data("id,a,b,c,d,e,f,g", Rows(100));
        var watch = Stopwatch.StartNew();

        var pairs = ColumnMatching.Match(left, right).Pairs;
        var keys = KeyGuesser.Guess(left, right, pairs).Keys;
        var result = ResultComparer.Compare(left, right, new ComparisonOptions(keys, pairs));

        Assert.Equal(999, result.Changed);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
    }
}
