using System.Security.Cryptography;

namespace DbExplorer.Core.Updates;

public enum DownloadFailure
{
    GhMissing,
    /// <summary>gh exited non-zero or timed out.</summary>
    GhFailed,
    /// <summary>gh succeeded but the expected file is not there.</summary>
    FileMissing,
    /// <summary>The sha256 does not match the release digest (file deleted).</summary>
    HashMismatch,
    Cancelled,
}

/// <param name="ZipPath">Set on success.</param>
/// <param name="Verified">True when the sha256 was checked against the release digest; false when GitHub gave none.</param>
public sealed record DownloadResult(string? ZipPath, DownloadFailure? Failure, string? Detail, bool Verified)
{
    public bool Succeeded => ZipPath is not null;

    public static DownloadResult Success(string path, bool verified) => new(path, null, null, verified);
    public static DownloadResult Failed(DownloadFailure failure, string? detail = null) => new(null, failure, detail, false);
}

/// <summary>Downloads a release zip with `gh release download` and verifies its sha256. Never throws.</summary>
public sealed class UpdateDownloader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProgressPoll = TimeSpan.FromMilliseconds(200);

    private readonly IGhRunner _gh;

    public UpdateDownloader(IGhRunner gh) => _gh = gh;

    /// <param name="stagingDir">Created if missing; chosen by the caller (e.g. under %TEMP%).</param>
    /// <param name="progress">Fraction 0..1, polled from the file size while gh runs (only when release.Size is known).</param>
    public async Task<DownloadResult> DownloadAsync(ReleaseInfo release, string stagingDir, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            if (AppIdentity.NormalizeRepository(release.Repository) is null || !IsPlainFileName(release.AssetName))
                return DownloadResult.Failed(DownloadFailure.GhFailed, "invalid release info");

            Directory.CreateDirectory(stagingDir);
            var path = Path.Combine(stagingDir, release.AssetName);

            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var poller = progress is not null && release.Size > 0
                ? PollAsync(path, release.Size, progress, pollCts.Token)
                : Task.CompletedTask;

            GhResult run;
            try
            {
                run = await _gh.RunAsync(
                    ["release", "download", release.Tag, "--repo", release.Repository,
                     "--pattern", release.AssetName, "--dir", stagingDir, "--clobber"],
                    Timeout, ct).ConfigureAwait(false);
            }
            finally
            {
                await pollCts.CancelAsync().ConfigureAwait(false);
                await poller.ConfigureAwait(false);
            }

            switch (run.Status)
            {
                case GhRunStatus.NotInstalled: return DownloadResult.Failed(DownloadFailure.GhMissing);
                case GhRunStatus.Cancelled: TryDelete(path); return DownloadResult.Failed(DownloadFailure.Cancelled);
                case GhRunStatus.TimedOut: TryDelete(path); return DownloadResult.Failed(DownloadFailure.GhFailed, "gh timed out");
            }
            if (run.ExitCode != 0)
            {
                TryDelete(path);
                return DownloadResult.Failed(DownloadFailure.GhFailed, run.StdErr.Trim());
            }

            if (!File.Exists(path)) return DownloadResult.Failed(DownloadFailure.FileMissing, path);

            if (release.Sha256 is null)
            {
                progress?.Report(1);
                return DownloadResult.Success(path, verified: false);
            }

            var actual = await HashAsync(path, ct).ConfigureAwait(false);
            if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path);
                return DownloadResult.Failed(DownloadFailure.HashMismatch, $"expected {release.Sha256}, got {actual}");
            }

            progress?.Report(1);
            return DownloadResult.Success(path, verified: true);
        }
        catch (OperationCanceledException)
        {
            return DownloadResult.Failed(DownloadFailure.Cancelled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return DownloadResult.Failed(DownloadFailure.FileMissing, ex.Message);
        }
    }

    private static bool IsPlainFileName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name == Path.GetFileName(name) && name is not ("." or "..");

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
    }

    private static async Task PollAsync(string path, long total, IProgress<double> progress, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(ProgressPoll, ct).ConfigureAwait(false);
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists) progress.Report(Math.Min(0.99, (double)info.Length / total));
                }
                catch (IOException) { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
