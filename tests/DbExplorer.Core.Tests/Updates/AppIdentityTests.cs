using DbExplorer.Core.Updates;

namespace DbExplorer.Core.Tests.Updates;

public class AppIdentityTests
{
    [Fact]
    public void FromValues_StampedBuild_HasVersionAndRepo()
    {
        var id = AppIdentity.FromValues("1.4.2+abc123", "JonathanOll/dbExplorer");

        Assert.Equal("1.4.2", id.Version!.ToString());
        Assert.Equal("JonathanOll/dbExplorer", id.Repository);
    }

    [Fact]
    public void FromValues_DevBuild_HasNoVersion_ButKeepsRepo()
    {
        var id = AppIdentity.FromValues("1.0.0+abc", "o/r");

        Assert.Null(id.Version);
        Assert.Equal("o/r", id.Repository);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("justname")]
    [InlineData("a/b/c")]
    [InlineData("/name")]
    [InlineData("owner/")]
    [InlineData("own er/name")]
    [InlineData("owner/na me")]
    [InlineData("owner/..")]
    [InlineData("../name")]
    [InlineData("-owner/name")]
    [InlineData("https://github.com/o/r")]
    [InlineData("owner\\name")]
    [InlineData("owner/name; rm -rf")]
    public void InvalidRepository_IsNull(string? repo) =>
        Assert.Null(AppIdentity.FromValues("1.2.3", repo).Repository);

    [Theory]
    [InlineData("JonathanOll/dbExplorer", "JonathanOll/dbExplorer")]
    [InlineData("  my-org/my_repo.v2 ", "my-org/my_repo.v2")]
    public void ValidRepository_IsKept(string repo, string expected) =>
        Assert.Equal(expected, AppIdentity.NormalizeRepository(repo));

    [Fact]
    public void FromAssembly_NullAssembly_IsEmptyIdentity()
    {
        var id = AppIdentity.FromAssembly(null);

        Assert.Null(id.Version);
        Assert.Null(id.Repository);
    }
}
