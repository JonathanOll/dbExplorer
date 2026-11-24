using DbExplorer.Core.Updates;

namespace DbExplorer.Core.Tests.Updates;

public class GhProcessRunnerTests
{
    [Fact]
    public async Task MissingExecutable_IsNotInstalled_NotAnException()
    {
        var runner = new GhProcessRunner("definitely-not-a-real-gh-" + Guid.NewGuid().ToString("N"));

        var result = await runner.RunAsync(["--version"], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(GhRunStatus.NotInstalled, result.Status);
        Assert.False(result.Succeeded);
    }
}
