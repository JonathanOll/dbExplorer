using DbExplorer.Core.Upgrade;

namespace DbExplorer.Core.Tests.Upgrade;

public class NaturalComparerTests
{
    private static int Cmp(string? a, string? b) => NaturalComparer.Instance.Compare(a, b);

    [Fact]
    public void DigitRuns_CompareAsNumbers()
    {
        Assert.True(Cmp("adj - 003", "adj - 037") < 0);
        Assert.True(Cmp("adj - 2", "adj - 10") < 0);
        Assert.True(Cmp("adj - 10", "adj - 2") > 0);
        Assert.True(Cmp("adj - 003", "adj - 2") > 0);
    }

    [Fact]
    public void HugeDigitRuns_DoNotOverflow()
    {
        Assert.True(Cmp("a99999999999999999999", "a100000000000000000000") < 0);
    }

    [Fact]
    public void Comparison_IsCaseInsensitiveForTheMainOrder()
    {
        Assert.True(Cmp("ADJ - 2", "adj - 10") < 0);
        Assert.True(Cmp("abc", "ABD") < 0);
    }

    [Fact]
    public void LeadingZeros_AreDeterministic_AndNeverEqualForDifferentStrings()
    {
        var ab = Cmp("a1", "a01");
        Assert.NotEqual(0, ab);
        Assert.Equal(-Math.Sign(ab), Math.Sign(Cmp("a01", "a1")));
        Assert.Equal(0, Cmp("a1", "a1"));
        Assert.NotEqual(0, Cmp("abc", "ABC"));
    }

    [Fact]
    public void Null_IsSafeAndSortsFirst()
    {
        Assert.Equal(0, Cmp(null, null));
        Assert.True(Cmp(null, "a") < 0);
        Assert.True(Cmp("a", null) > 0);
    }

    [Fact]
    public void ShorterPrefix_SortsFirst()
    {
        Assert.True(Cmp("fbo - 1", "fbo - 1 - a") < 0);
        Assert.True(Cmp("", "a") < 0);
    }
}
