using DbExplorer.Core.Updates;

namespace DbExplorer.App.Updates;

/// <summary>Asks whether a newer release exists. Never throws; runs off the UI thread (the caller wraps it in Task.Run).</summary>
public interface IUpdateChecker
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken ct);
}

/// <summary>Downloads (and verifies) the zip of a release into <paramref name="stagingDir"/>. Never throws.</summary>
public interface IUpdateDownloader
{
    Task<DownloadResult> DownloadAsync(ReleaseInfo release, string stagingDir, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>Starts the relay that swaps the files once the app has exited. Never throws.</summary>
public interface IUpdateApplier
{
    UpdateLaunchResult Launch(string zipPath);
}

/// <summary>What the update flow needs from the running shell: the exit prompts and the shutdown.</summary>
public interface IUpdateHost
{
    /// <summary>The prompts of a normal window close (Save / Discard / Cancel for unsaved work); false = the user cancelled.</summary>
    Task<bool> ConfirmExitAsync();

    /// <summary>The exit that was confirmed will not happen after all: tabs that agreed to close go back to normal.</summary>
    void ExitAborted();

    /// <summary>Closes the app through the normal close path (session and window state are saved), without asking again.</summary>
    void Shutdown();
}

/// <summary>The real checker: only an installed copy that knows its version and repository asks GitHub.</summary>
public sealed class GhUpdateChecker(IGhRunner gh, string? exePath) : IUpdateChecker
{
    public Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        try
        {
            var identity = AppIdentity.Current;
            if (identity.Version is null) return Task.FromResult<UpdateCheckResult>(new UpdateCheckResult.Unavailable(UnavailableReason.DevBuild));
            var (can, reason) = UpdateApplier.CanSelfUpdate(exePath, Path.GetDirectoryName(exePath));
            if (!can) return Task.FromResult<UpdateCheckResult>(new UpdateCheckResult.Unavailable(UnavailableReason.DevBuild, reason));
            return new UpdateChecker(gh).CheckAsync(identity, ct);
        }
        catch (Exception e)
        {
            return Task.FromResult<UpdateCheckResult>(new UpdateCheckResult.Unavailable(UnavailableReason.BadResponse, e.Message));
        }
    }
}

public sealed class GhUpdateDownloader(IGhRunner gh) : IUpdateDownloader
{
    public Task<DownloadResult> DownloadAsync(ReleaseInfo release, string stagingDir, IProgress<double>? progress, CancellationToken ct) =>
        new UpdateDownloader(gh).DownloadAsync(release, stagingDir, progress, ct);
}

/// <summary>Launches the relay for this very process: its exe, its folder, its pid and its original arguments.</summary>
public sealed class SelfUpdateApplier(string exePath, int processId, IReadOnlyList<string> args) : IUpdateApplier
{
    public UpdateLaunchResult Launch(string zipPath) =>
        UpdateApplier.Launch(zipPath, Path.GetDirectoryName(exePath)!, exePath, processId, args);
}
