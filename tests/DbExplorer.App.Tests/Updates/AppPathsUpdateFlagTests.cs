namespace DbExplorer.App.Tests.Updates;

public class AppPathsUpdateFlagTests
{
    private static string? NoEnv(string _) => null;

    [Fact]
    public void ByDefaultTheCheckIsNotDisabled() =>
        Assert.False(AppPaths.UpdateCheckDisabled([], NoEnv));

    [Fact]
    public void TheFlagDisablesIt() =>
        Assert.True(AppPaths.UpdateCheckDisabled(["--data-dir", "x", "--no-update-check"], NoEnv));

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("yes please", false)]
    public void TheEnvironmentVariableDisablesItOnlyWhenOn(string value, bool disabled) =>
        Assert.Equal(disabled, AppPaths.UpdateCheckDisabled([], name => name == "DBEXPLORER_NO_UPDATE_CHECK" ? value : null));

    [Fact]
    public void TheFlagIsAcceptedByCheckArguments_AndNamedInTheUsage()
    {
        Assert.Null(AppPaths.CheckArguments(["--no-update-check"]));
        Assert.Null(AppPaths.CheckArguments(["--no-update-check", "--data-dir", "x"]));
        Assert.Contains("--no-update-check", AppPaths.Usage);
        Assert.Contains("--no-update-check", AppPaths.CheckArguments(["--data-dir"]));
    }
}
