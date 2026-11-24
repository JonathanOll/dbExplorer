using System.Security.Cryptography;
using DbExplorer.Core.Updates;

namespace DbExplorer.Core.Tests.Updates;

public class UpdateDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-update-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Content = "fake zip content"u8.ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ReleaseInfo Release(string? sha) =>
        new("o/r", "v1.0.3", AppVersion.ParseOrNull("1.0.3")!, "DbExplorer.zip", Content.Length, sha);

    // Fake gh that "downloads" by writing the file into the --dir argument.
    private static FakeGhRunner Writes(byte[] bytes) => new(args =>
    {
        var dir = args[args.ToList().IndexOf("--dir") + 1];
        File.WriteAllBytes(Path.Combine(dir, "DbExplorer.zip"), bytes);
        return GhResult.Ok();
    });

    [Fact]
    public async Task HashMatches_ReturnsZipPath_Verified()
    {
        var gh = Writes(Content);

        var result = await new UpdateDownloader(gh).DownloadAsync(Release(Sha(Content)), _dir);

        Assert.True(result.Succeeded);
        Assert.True(result.Verified);
        Assert.Equal(Path.Combine(_dir, "DbExplorer.zip"), result.ZipPath);
        Assert.True(File.Exists(result.ZipPath));
    }

    [Fact]
    public async Task Download_PassesRepoTagAndPatternToGh()
    {
        var gh = Writes(Content);

        await new UpdateDownloader(gh).DownloadAsync(Release(Sha(Content)), _dir);

        var args = Assert.Single(gh.Calls);
        Assert.Equal(["release", "download", "v1.0.3", "--repo", "o/r", "--pattern", "DbExplorer.zip", "--dir", _dir, "--clobber"], args);
    }

    [Fact]
    public async Task HashComparison_IgnoresCase()
    {
        var result = await new UpdateDownloader(Writes(Content)).DownloadAsync(Release(Sha(Content).ToUpperInvariant()), _dir);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task HashMismatch_DeletesFile_AndFails()
    {
        var result = await new UpdateDownloader(Writes(Content)).DownloadAsync(Release(Sha("other"u8.ToArray())), _dir);

        Assert.False(result.Succeeded);
        Assert.Equal(DownloadFailure.HashMismatch, result.Failure);
        Assert.Null(result.ZipPath);
        Assert.False(File.Exists(Path.Combine(_dir, "DbExplorer.zip")));
    }

    [Fact]
    public async Task MissingDigest_Succeeds_NotVerified()
    {
        var result = await new UpdateDownloader(Writes(Content)).DownloadAsync(Release(null), _dir);

        Assert.True(result.Succeeded);
        Assert.False(result.Verified);
    }

    [Fact]
    public async Task GhFailure_Fails_WithStderr_AndLeavesNoFile()
    {
        var gh = new FakeGhRunner(GhResult.Fail(1, "HTTP 404"));

        var result = await new UpdateDownloader(gh).DownloadAsync(Release(Sha(Content)), _dir);

        Assert.Equal(DownloadFailure.GhFailed, result.Failure);
        Assert.Equal("HTTP 404", result.Detail);
        Assert.False(File.Exists(Path.Combine(_dir, "DbExplorer.zip")));
    }

    [Fact]
    public async Task GhMissing_IsReported() =>
        Assert.Equal(DownloadFailure.GhMissing,
            (await new UpdateDownloader(new FakeGhRunner(GhResult.Missing())).DownloadAsync(Release(null), _dir)).Failure);

    [Fact]
    public async Task GhSucceedsButNoFile_IsFileMissing() =>
        Assert.Equal(DownloadFailure.FileMissing,
            (await new UpdateDownloader(new FakeGhRunner(GhResult.Ok())).DownloadAsync(Release(Sha(Content)), _dir)).Failure);

    [Fact]
    public async Task Cancellation_IsReported_NotThrown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await new UpdateDownloader(Writes(Content)).DownloadAsync(Release(null), _dir, ct: cts.Token);

        Assert.Equal(DownloadFailure.Cancelled, result.Failure);
    }

    [Fact]
    public async Task StagingDir_IsCreated()
    {
        var nested = Path.Combine(_dir, "a", "b");

        var result = await new UpdateDownloader(Writes(Content)).DownloadAsync(Release(null), nested);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(nested));
    }

    [Fact]
    public async Task Progress_ReachesOne_OnSuccess()
    {
        double last = 0;
        var progress = new SyncProgress(v => last = v);

        await new UpdateDownloader(Writes(Content)).DownloadAsync(Release(Sha(Content)), _dir, progress);

        Assert.Equal(1.0, last);
    }

    [Theory]
    [InlineData("..\\evil.zip")]
    [InlineData("sub/evil.zip")]
    [InlineData("")]
    public async Task UnsafeAssetName_IsRefused_WithoutCallingGh(string name)
    {
        var gh = new FakeGhRunner(GhResult.Ok());
        var release = Release(null) with { AssetName = name };

        var result = await new UpdateDownloader(gh).DownloadAsync(release, _dir);

        Assert.False(result.Succeeded);
        Assert.Empty(gh.Calls);
    }

    [Fact]
    public async Task InvalidRepo_IsRefused_WithoutCallingGh()
    {
        var gh = new FakeGhRunner(GhResult.Ok());

        var result = await new UpdateDownloader(gh).DownloadAsync(Release(null) with { Repository = "bad" }, _dir);

        Assert.False(result.Succeeded);
        Assert.Empty(gh.Calls);
    }

    private sealed class SyncProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }
}
