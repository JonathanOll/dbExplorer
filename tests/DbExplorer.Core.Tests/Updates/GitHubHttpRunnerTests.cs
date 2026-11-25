using System.Net;
using System.Security.Cryptography;
using System.Text;
using DbExplorer.Core.Updates;

namespace DbExplorer.Core.Tests.Updates;

public class GitHubHttpRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-http-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Zip = "fake zip content"u8.ToArray();
    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string ReleaseJson(string? digest) =>
        "{\"tag_name\":\"v1.2.0\",\"draft\":false,\"prerelease\":false,\"assets\":[" +
        "{\"name\":\"notes.txt\",\"size\":3,\"url\":\"https://api.github.com/a/1\",\"browser_download_url\":\"https://github.com/o/r/releases/download/v1.2.0/notes.txt\"}," +
        "{\"name\":\"DbExplorer.zip\",\"size\":" + Zip.Length + ",\"url\":\"https://api.github.com/a/2\",\"browser_download_url\":\"https://github.com/o/r/releases/download/v1.2.0/DbExplorer.zip\"" +
        (digest is null ? "" : ",\"digest\":\"" + digest + "\"") + "}]}";

    private static HttpResponseMessage Serve(HttpRequestMessage r, string? digest) => r.RequestUri!.Host switch
    {
        "api.github.com" when r.RequestUri.AbsolutePath.Contains("/releases/") => Json(ReleaseJson(digest)),
        "api.github.com" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://objects.example/blob") } },
        _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip) },
    };

    private static GitHubHttpRunner Runner(StubHandler h, string? token = null) =>
        new(h, n => n == "GITHUB_TOKEN" ? token : null);

    private static readonly string[] ViewLatest = ["release", "view", "--repo", "o/r", "--json", "tagName,isDraft,isPrerelease,assets"];

    [Fact]
    public async Task View_Latest_ParsesAndPicksTheZipAsset()
    {
        var h = new StubHandler(r => Serve(r, "sha256:" + Sha(Zip)));
        var run = await Runner(h).RunAsync(ViewLatest, TimeSpan.FromSeconds(5), default);

        Assert.True(run.Succeeded);
        Assert.Equal("https://api.github.com/repos/o/r/releases/latest", h.Requests[0].RequestUri!.ToString());
        var result = await new UpdateChecker(Runner(new StubHandler(r => Serve(r, "sha256:" + Sha(Zip)))))
            .CheckAsync(AppVersion.ParseOrNull("1.0.0"), "o/r");
        var avail = Assert.IsType<UpdateCheckResult.UpdateAvailable>(result);
        Assert.Equal("v1.2.0", avail.Release.Tag);
        Assert.Equal("DbExplorer.zip", avail.Release.AssetName);
        Assert.Equal(Sha(Zip), avail.Release.Sha256);
    }

    [Fact]
    public async Task View_Tag_UsesTagEndpoint()
    {
        var h = new StubHandler(r => Serve(r, null));
        await Runner(h).RunAsync(["release", "view", "v1.2.0", "--repo", "o/r"], TimeSpan.FromSeconds(5), default);
        Assert.EndsWith("/repos/o/r/releases/tags/v1.2.0", h.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Download_WithDigest_IsVerifiedByTheDownloader()
    {
        var h = new StubHandler(r => Serve(r, "sha256:" + Sha(Zip)));
        var release = new ReleaseInfo("o/r", "v1.2.0", AppVersion.ParseOrNull("1.2.0")!, "DbExplorer.zip", Zip.Length, Sha(Zip));

        var result = await new UpdateDownloader(Runner(h)).DownloadAsync(release, _dir);

        Assert.True(result.Succeeded);
        Assert.True(result.Verified);
        Assert.Equal(Zip, File.ReadAllBytes(result.ZipPath!));
        // Public download: the browser url, no API call for the file.
        Assert.Contains(h.Requests, r => r.RequestUri!.Host == "github.com");
    }

    [Fact]
    public async Task Download_DigestMismatch_IsRefusedAndDeleted()
    {
        var h = new StubHandler(r => Serve(r, null));
        var release = new ReleaseInfo("o/r", "v1.2.0", AppVersion.ParseOrNull("1.2.0")!, "DbExplorer.zip", Zip.Length, new string('0', 64));

        var result = await new UpdateDownloader(Runner(h)).DownloadAsync(release, _dir);

        Assert.False(result.Succeeded);
        Assert.Equal(DownloadFailure.HashMismatch, result.Failure);
        Assert.False(File.Exists(Path.Combine(_dir, "DbExplorer.zip")));
    }

    [Fact]
    public async Task RateLimit_403_SaysToSetTheToken()
    {
        var h = new StubHandler(_ =>
        {
            var r = Json("{\"message\":\"API rate limit exceeded\"}", HttpStatusCode.Forbidden);
            r.Headers.Add("X-RateLimit-Remaining", "0");
            return r;
        });
        var run = await Runner(h).RunAsync(ViewLatest, TimeSpan.FromSeconds(5), default);

        Assert.False(run.Succeeded);
        Assert.Contains("GITHUB_TOKEN", run.StdErr);
        Assert.Contains("rate limit", run.StdErr);
    }

    [Fact]
    public async Task NotFound_404_IsAFailureWithAMessage()
    {
        var h = new StubHandler(_ => Json("{}", HttpStatusCode.NotFound));
        var run = await Runner(h).RunAsync(["release", "view", "v9.9.9", "--repo", "o/r"], TimeSpan.FromSeconds(5), default);

        Assert.False(run.Succeeded);
        Assert.Contains("not found", run.StdErr);
    }

    [Fact]
    public async Task NetworkError_IsAFailureNotAnException()
    {
        var h = new StubHandler(_ => throw new HttpRequestException("no route"));
        var run = await Runner(h).RunAsync(ViewLatest, TimeSpan.FromSeconds(5), default);

        Assert.False(run.Succeeded);
        Assert.Equal(GhRunStatus.Completed, run.Status);
        Assert.Contains("not reachable", run.StdErr);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }
    }

    [Fact]
    public async Task Timeout_IsTimedOut()
    {
        var run = await new GitHubHttpRunner(new HangingHandler(), _ => null).RunAsync(ViewLatest, TimeSpan.FromMilliseconds(100), default);
        Assert.Equal(GhRunStatus.TimedOut, run.Status);
    }

    [Fact]
    public async Task Cancellation_IsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var task = new GitHubHttpRunner(new HangingHandler(), _ => null).RunAsync(ViewLatest, TimeSpan.FromSeconds(30), cts.Token);
        cts.CancelAfter(50);
        Assert.Equal(GhRunStatus.Cancelled, (await task).Status);
    }

    [Fact]
    public async Task Token_IsSentToTheApiOnly_AndNeverToTheDownloadHost()
    {
        var h = new StubHandler(r => Serve(r, "sha256:" + Sha(Zip)));
        var release = new ReleaseInfo("o/r", "v1.2.0", AppVersion.ParseOrNull("1.2.0")!, "DbExplorer.zip", Zip.Length, Sha(Zip));

        var result = await new UpdateDownloader(Runner(h, "tok123")).DownloadAsync(release, _dir);

        Assert.True(result.Succeeded);
        foreach (var r in h.Requests)
        {
            var has = r.Headers.TryGetValues("Authorization", out var v);
            if (r.RequestUri!.Host == "api.github.com") Assert.Equal("Bearer tok123", v!.Single());
            else Assert.False(has);
        }
        // With a token the API asset url is used (private repositories) and its redirect followed.
        Assert.Contains(h.Requests, r => r.RequestUri!.AbsolutePath == "/a/2");
        Assert.Contains(h.Requests, r => r.RequestUri!.Host == "objects.example");
    }

    [Fact]
    public async Task NoToken_NoAuthorizationHeader()
    {
        var h = new StubHandler(r => Serve(r, null));
        await Runner(h).RunAsync(ViewLatest, TimeSpan.FromSeconds(5), default);
        Assert.False(h.Requests[0].Headers.Contains("Authorization"));
        Assert.True(h.Requests[0].Headers.UserAgent.Count > 0);
    }

    [Fact]
    public async Task Fallback_UsesGhWhenHttpFails_AndKeepsHttpErrorWhenGhMissing()
    {
        var failing = new FakeGhRunner(GhResult.Fail(1, "http boom"));
        var gh = new FakeGhRunner(GhResult.Ok("{}"));
        Assert.True((await new FallbackGhRunner(failing, gh).RunAsync(["x"], TimeSpan.FromSeconds(1), default)).Succeeded);

        var missing = new FakeGhRunner(GhResult.Missing());
        var r = await new FallbackGhRunner(failing, missing).RunAsync(["x"], TimeSpan.FromSeconds(1), default);
        Assert.Equal("http boom", r.StdErr);

        var ok = new FakeGhRunner(GhResult.Ok("{}"));
        var never = new FakeGhRunner(GhResult.Ok("other"));
        await new FallbackGhRunner(ok, never).RunAsync(["x"], TimeSpan.FromSeconds(1), default);
        Assert.Empty(never.Calls);
    }
}
