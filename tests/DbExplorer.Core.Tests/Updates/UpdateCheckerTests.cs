using DbExplorer.Core.Updates;

namespace DbExplorer.Core.Tests.Updates;

public class UpdateCheckerTests
{
    private const string Repo = "JonathanOll/dbExplorer";
    private const string Hash = "821e21e4a9f2c3511a7351b08cfa23ae8b264820d7da8a1a56cfc1cf1282b1a1";

    private static AppVersion V(string s) => AppVersion.ParseOrNull(s)!;

    private static string Json(string tag = "v1.0.3", string digest = "\"sha256:" + Hash + "\"", string extra = "") =>
        "{\"isDraft\":false,\"isPrerelease\":false,\"tagName\":\"" + tag + "\"," + extra +
        "\"assets\":[{\"name\":\"DbExplorer.zip\",\"size\":69551342,\"digest\":" + digest +
        ",\"url\":\"https://github.com/JonathanOll/dbExplorer/releases/download/" + tag + "/DbExplorer.zip\"}]}";

    private static Task<UpdateCheckResult> Check(GhResult gh, string current = "1.0.2", string? repo = Repo) =>
        new UpdateChecker(new FakeGhRunner(gh)).CheckAsync(V(current), repo);

    [Fact]
    public async Task Newer_IsAvailable_WithAssetDetails()
    {
        var result = await Check(GhResult.Ok(Json()));

        var info = Assert.IsType<UpdateCheckResult.UpdateAvailable>(result).Release;
        Assert.Equal((Repo, "v1.0.3", "1.0.3", "DbExplorer.zip", 69551342L, Hash),
            (info.Repository, info.Tag, info.Version.ToString(), info.AssetName, info.Size, info.Sha256));
    }

    [Fact]
    public async Task Check_PassesRepoToGh()
    {
        var gh = new FakeGhRunner(GhResult.Ok(Json()));

        await new UpdateChecker(gh).CheckAsync(V("1.0.2"), Repo);

        var call = Assert.Single(gh.Calls);
        Assert.Equal(["release", "view", "--repo", Repo], call.Take(4));
    }

    [Fact]
    public async Task CheckAsync_FromIdentity_UsesItsVersionAndRepo()
    {
        var gh = new FakeGhRunner(GhResult.Ok(Json()));

        var result = await new UpdateChecker(gh).CheckAsync(AppIdentity.FromValues("1.0.2+abc", "o/r"));

        Assert.IsType<UpdateCheckResult.UpdateAvailable>(result);
        Assert.Contains("o/r", gh.Calls[0]);
    }

    [Fact]
    public async Task SameVersion_IsUpToDate()
    {
        var result = await Check(GhResult.Ok(Json()), current: "1.0.3");

        var ok = Assert.IsType<UpdateCheckResult.UpToDate>(result);
        Assert.Equal(V("1.0.3"), ok.Latest);
    }

    [Fact]
    public async Task OlderRelease_IsUpToDate() =>
        Assert.IsType<UpdateCheckResult.UpToDate>(await Check(GhResult.Ok(Json()), current: "2.0.0"));

    [Fact]
    public async Task PrereleaseCurrent_IsOlderThanItsRelease() =>
        Assert.IsType<UpdateCheckResult.UpdateAvailable>(await Check(GhResult.Ok(Json()), current: "1.0.3-beta.1"));

    [Fact]
    public async Task DraftOrPrereleaseFlag_IsIgnored()
    {
        var json = Json().Replace("\"isDraft\":false", "\"isDraft\":true");

        Assert.IsType<UpdateCheckResult.UpToDate>(await Check(GhResult.Ok(json)));
    }

    [Fact]
    public async Task UnknownCurrentVersion_IsDevBuild_AndGhIsNeverCalled()
    {
        var gh = new FakeGhRunner(GhResult.Ok(Json()));

        var result = await new UpdateChecker(gh).CheckAsync((AppVersion?)null, Repo);

        Assert.Equal(UnavailableReason.DevBuild, Assert.IsType<UpdateCheckResult.Unavailable>(result).Reason);
        Assert.Empty(gh.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a repo")]
    [InlineData("a/b/c")]
    public async Task MissingOrInvalidRepo_IsNoRepoConfigured_AndGhIsNeverCalled(string? repo)
    {
        var gh = new FakeGhRunner(GhResult.Ok(Json()));

        var result = await new UpdateChecker(gh).CheckAsync(V("1.0.2"), repo);

        Assert.Equal(UnavailableReason.NoRepoConfigured, Assert.IsType<UpdateCheckResult.Unavailable>(result).Reason);
        Assert.Empty(gh.Calls);
    }

    [Fact]
    public async Task GhNotInstalled_IsGhMissing() =>
        Assert.Equal(UnavailableReason.GhMissing, Reason(await Check(GhResult.Missing())));

    [Fact]
    public async Task NonZeroExit_IsNotAuthenticatedOrNetwork_WithStderr()
    {
        var result = await Check(GhResult.Fail(4, "To get started with GitHub CLI, please run:  gh auth login\nmore"));

        var u = Assert.IsType<UpdateCheckResult.Unavailable>(result);
        Assert.Equal(UnavailableReason.NotAuthenticatedOrNetwork, u.Reason);
        Assert.StartsWith("To get started", u.Detail);
    }

    [Fact]
    public async Task Timeout_IsNotAuthenticatedOrNetwork() =>
        Assert.Equal(UnavailableReason.NotAuthenticatedOrNetwork,
            Reason(await Check(new GhResult(GhRunStatus.TimedOut, -1, "", ""))));

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1,2]")]
    [InlineData("{}")]
    [InlineData("{\"tagName\":\"nightly\",\"assets\":[]}")]
    [InlineData("{\"tagName\":null,\"assets\":[]}")]
    public async Task GarbageJson_IsBadResponse(string json) =>
        Assert.Equal(UnavailableReason.BadResponse, Reason(await Check(GhResult.Ok(json))));

    [Fact]
    public async Task NewerRelease_WithoutZipAsset_IsBadResponse()
    {
        var json = "{\"tagName\":\"v2.0.0\",\"assets\":[{\"name\":\"notes.txt\",\"size\":1}]}";

        Assert.Equal(UnavailableReason.BadResponse, Reason(await Check(GhResult.Ok(json))));
    }

    [Fact]
    public async Task NewerRelease_WithoutAssetsArray_IsBadResponse() =>
        Assert.Equal(UnavailableReason.BadResponse, Reason(await Check(GhResult.Ok("{\"tagName\":\"v2.0.0\"}"))));

    [Theory]
    [InlineData("null")]
    [InlineData("\"md5:abc\"")]
    [InlineData("\"sha256:zzzz\"")]
    [InlineData("\"sha256:abcd\"")]
    public async Task MissingOrUnusableDigest_GivesNullSha(string digest)
    {
        var result = await Check(GhResult.Ok(Json(digest: digest)));

        Assert.Null(Assert.IsType<UpdateCheckResult.UpdateAvailable>(result).Release.Sha256);
    }

    [Fact]
    public async Task Digest_IsLowerCased()
    {
        var result = await Check(GhResult.Ok(Json(digest: "\"SHA256:" + Hash.ToUpperInvariant() + "\"")));

        Assert.Equal(Hash, Assert.IsType<UpdateCheckResult.UpdateAvailable>(result).Release.Sha256);
    }

    [Fact]
    public async Task OtherZipAsset_IsUsed_WhenDefaultNameIsAbsent()
    {
        var json = "{\"tagName\":\"v2.0.0\",\"assets\":[{\"name\":\"readme.txt\",\"size\":1},{\"name\":\"App-win.zip\",\"size\":5}]}";

        var result = await Check(GhResult.Ok(json));

        Assert.Equal("App-win.zip", Assert.IsType<UpdateCheckResult.UpdateAvailable>(result).Release.AssetName);
    }

    [Fact]
    public async Task Cancellation_IsReported_NotThrown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await new UpdateChecker(new FakeGhRunner(GhResult.Ok(Json()))).CheckAsync(V("1.0.2"), Repo, cts.Token);

        Assert.Equal(UnavailableReason.Cancelled, Reason(result));
    }

    [Fact]
    public async Task RunnerThatThrows_IsBadResponse_NotThrown()
    {
        var gh = new FakeGhRunner(_ => throw new InvalidOperationException("boom"));

        var result = await new UpdateChecker(gh).CheckAsync(V("1.0.2"), Repo);

        Assert.Equal(UnavailableReason.BadResponse, Reason(result));
    }

    private static UnavailableReason Reason(UpdateCheckResult r) => Assert.IsType<UpdateCheckResult.Unavailable>(r).Reason;
}
