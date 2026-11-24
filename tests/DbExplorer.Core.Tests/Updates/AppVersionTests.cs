using DbExplorer.Core.Updates;

namespace DbExplorer.Core.Tests.Updates;

public class AppVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v1.2.3", 1, 2, 3, null)]
    [InlineData("V10.0.7", 10, 0, 7, null)]
    [InlineData("  1.2.3  ", 1, 2, 3, null)]
    [InlineData("1.2.3+abcdef", 1, 2, 3, null)]
    [InlineData("1.2.3-beta.1+sha", 1, 2, 3, "beta.1")]
    [InlineData("v1.2.3-rc.2", 1, 2, 3, "rc.2")]
    public void TryParse_AcceptsKnownShapes(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(AppVersion.TryParse(text, out var v));
        Assert.Equal((major, minor, patch, pre), (v.Major, v.Minor, v.Patch, v.Prerelease));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-be ta")]
    [InlineData("99999999999.0.0")]
    [InlineData("v")]
    public void TryParse_RejectsGarbage_WithoutThrowing(string? text)
    {
        Assert.False(AppVersion.TryParse(text, out _));
        Assert.Null(AppVersion.ParseOrNull(text));
    }

    [Theory]
    [InlineData("1.2.4", "1.2.3")]
    [InlineData("1.3.0", "1.2.9")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.10.0", "1.9.0")]
    [InlineData("1.0.0", "1.0.0-beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.1")]
    [InlineData("1.0.0-beta.10", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta", "1.0.0-alpha")]
    [InlineData("1.0.0-beta.1", "1.0.0-beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-alpha.1")]
    public void Compare_FirstIsGreater(string bigger, string smaller)
    {
        var a = AppVersion.ParseOrNull(bigger)!;
        var b = AppVersion.ParseOrNull(smaller)!;
        Assert.True(a > b);
        Assert.True(b < a);
        Assert.True(a >= b);
        Assert.False(a <= b);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compare_IgnoresBuildMetadata_AndLeadingV()
    {
        Assert.Equal(AppVersion.ParseOrNull("1.2.3+aaa"), AppVersion.ParseOrNull("v1.2.3+bbb"));
        Assert.Equal(AppVersion.ParseOrNull("1.2.3")!.GetHashCode(), AppVersion.ParseOrNull("v1.2.3+x")!.GetHashCode());
    }

    [Fact]
    public void ToString_DropsMetadata()
    {
        Assert.Equal("1.2.3-beta.1", AppVersion.ParseOrNull("v1.2.3-beta.1+sha")!.ToString());
        Assert.Equal("1.2.3", AppVersion.ParseOrNull("1.2.3+sha")!.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1.0.0")]              // unstamped SDK default
    [InlineData("1.0.0+abcdef123")]    // unstamped SDK default with source revision
    public void FromInformational_UnknownBuilds_AreNull(string? text) =>
        Assert.Null(AppVersion.FromInformational(text));

    [Theory]
    [InlineData("1.0.3+abc", "1.0.3")]
    [InlineData("1.0.1", "1.0.1")]
    [InlineData("1.0.0-beta.1+abc", "1.0.0-beta.1")]
    [InlineData("2.0.0", "2.0.0")]
    public void FromInformational_StampedBuilds_Parse(string text, string expected) =>
        Assert.Equal(expected, AppVersion.FromInformational(text)!.ToString());

    [Fact]
    public void FromAssembly_NullAssembly_IsUnknown() =>
        Assert.Null(AppVersion.FromAssembly(null));
}
