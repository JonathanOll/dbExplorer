using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.App.Updates;
using DbExplorer.Core.Updates;

namespace DbExplorer.App.ViewModels;

public enum UpdateState
{
    /// <summary>Nothing to offer (not checked yet, up to date, or the check was not possible).</summary>
    Idle,
    Available,
    Downloading,
    /// <summary>Downloaded: asking about unsaved work, then handing over to the relay and closing.</summary>
    Applying,
    Failed,
    /// <summary>"Later": hidden for the rest of this run.</summary>
    Dismissed,
}

/// <summary>
/// The update banner: Idle -> Available -> Downloading -> Applying (the app then closes), or Failed (retry or dismiss);
/// "Later" gives Dismissed. Everything that touches the outside (GitHub, the relay, the shell) is injected, so the flow is
/// testable. Without a checker (tests of other view models, dev runs) it stays Idle for ever.
/// </summary>
public partial class UpdateViewModel : ObservableObject
{
    private readonly IUpdateChecker? _checker;
    private readonly IUpdateDownloader? _downloader;
    private readonly IUpdateApplier? _applier;
    private readonly Func<string> _stagingDir;
    private readonly Action<bool>? _saveAutoCheck;
    private readonly Action<Action> _post;
    private readonly bool _suppressed;
    private string? _zipPath;      // a verified download kept after the user cancelled at the unsaved-work prompt
    private string? _stagingUsed;

    /// <param name="autoCheck">The persisted setting: look for updates at start.</param>
    /// <param name="suppressed">Off for this run only (<c>--no-update-check</c>, env var); the setting itself is untouched.</param>
    /// <param name="saveAutoCheck">Persists the setting when the user flips it.</param>
    /// <param name="post">Runs an action on the UI thread (progress reports come from a worker); default: inline.</param>
    public UpdateViewModel(IUpdateChecker? checker = null, IUpdateDownloader? downloader = null, IUpdateApplier? applier = null,
        Func<string>? stagingDir = null, bool autoCheck = true, bool suppressed = false,
        Action<bool>? saveAutoCheck = null, Action<Action>? post = null)
    {
        _checker = checker;
        _downloader = downloader;
        _applier = applier;
        _stagingDir = stagingDir ?? (() => Path.Combine(Path.GetTempPath(), "dbExplorer-update-dl-" + Guid.NewGuid().ToString("N")[..8]));
        _autoCheckEnabled = autoCheck;
        _suppressed = suppressed;
        _saveAutoCheck = saveAutoCheck;
        _post = post ?? (a => a());
    }

    /// <summary>The shell the flow asks about unsaved work and shuts down; set once the window exists.</summary>
    public IUpdateHost? Host { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(IsAvailable), nameof(IsDownloading), nameof(IsApplying), nameof(IsFailed), nameof(CanAct), nameof(ActionText), nameof(DismissText), nameof(Text))]
    private UpdateState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text), nameof(ProgressPercent))]
    private ReleaseInfo? _release;

    /// <summary>Download progress, 0..1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text), nameof(ProgressPercent))]
    private double _progress;

    /// <summary>Why the last attempt failed (shown in the banner).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    private string? _failure;

    /// <summary>The "check for updates automatically" setting, as flipped by the user.</summary>
    [ObservableProperty] private bool _autoCheckEnabled;

    partial void OnAutoCheckEnabledChanged(bool value)
    {
        try { _saveAutoCheck?.Invoke(value); }
        catch (Exception e) { Trace.WriteLine("Update setting not saved: " + e.Message); }
    }

    public bool IsVisible => State is UpdateState.Available or UpdateState.Downloading or UpdateState.Applying or UpdateState.Failed;
    public bool IsAvailable => State == UpdateState.Available;
    public bool IsDownloading => State == UpdateState.Downloading;
    public bool IsApplying => State == UpdateState.Applying;
    public bool IsFailed => State == UpdateState.Failed;
    /// <summary>The buttons show while a choice is open (not during the download or the restart).</summary>
    public bool CanAct => State is UpdateState.Available or UpdateState.Failed;
    public string ActionText => State == UpdateState.Failed ? "Retry" : "Update and restart";
    public string DismissText => State == UpdateState.Failed ? "Dismiss" : "Later";
    public int ProgressPercent => (int)Math.Round(Math.Clamp(Progress, 0, 1) * 100);

    /// <summary>The banner message.</summary>
    public string Text => State switch
    {
        UpdateState.Available => $"Version {Release?.Version} is available.",
        UpdateState.Downloading => $"Downloading version {Release?.Version}... {ProgressPercent} %",
        UpdateState.Applying => $"Restarting to install version {Release?.Version}...",
        UpdateState.Failed => $"Update to version {Release?.Version} failed: {Failure}",
        _ => "",
    };

    /// <summary>
    /// The background check. Does nothing when the setting or the run switch is off, when it already ran, or when the update
    /// flow is past Idle. Silent on every failure (a Trace line at most): never a dialog, never an exception.
    /// </summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (_checker is null || !AutoCheckEnabled || _suppressed || State != UpdateState.Idle) return;
        try
        {
            var result = await Task.Run(() => _checker.CheckAsync(ct), ct);
            switch (result)
            {
                case UpdateCheckResult.UpdateAvailable available when State == UpdateState.Idle:
                    Release = available.Release;
                    State = UpdateState.Available;
                    break;
                case UpdateCheckResult.Unavailable unavailable:
                    Trace.WriteLine($"Update check unavailable: {unavailable.Reason} {unavailable.Detail}");
                    break;
            }
        }
        catch (Exception e)
        {
            Trace.WriteLine("Update check failed: " + e.Message);
        }
    }

    /// <summary>"Update and restart" / "Retry": download, ask about unsaved work, start the relay, close the app. Never throws.</summary>
    [RelayCommand]
    public async Task UpdateAsync()
    {
        if (State is not (UpdateState.Available or UpdateState.Failed) || Release is not { } release) return;
        if (_downloader is null || _applier is null || Host is not { } host) return;

        Failure = null;
        var confirmed = false;
        try
        {
            if (_zipPath is null || !File.Exists(_zipPath))
            {
                _zipPath = null;
                State = UpdateState.Downloading;
                Progress = 0;
                _stagingUsed = _stagingDir();
                var progress = new SyncProgress(v => _post(() =>
                {
                    if (State == UpdateState.Downloading) Progress = v;
                }));
                var download = await _downloader.DownloadAsync(release, _stagingUsed, progress, CancellationToken.None);
                if (!download.Succeeded) { Fail(Describe(download)); return; }
                _zipPath = download.ZipPath;
            }

            State = UpdateState.Applying;
            // Same prompts as closing the window: unsaved edits ask Save / Discard / Cancel, dirty scripts are kept by their autosave.
            if (!await host.ConfirmExitAsync())
            {
                State = UpdateState.Available;   // the download is kept: a second click goes straight on
                return;
            }
            confirmed = true;

            var launch = _applier.Launch(_zipPath!);
            if (!launch.Started)
            {
                host.ExitAborted();
                Fail(launch.Reason ?? "The update could not be started.");
                return;
            }
            host.Shutdown();   // the relay waits for this process to exit, then swaps the files and relaunches
        }
        catch (Exception e)
        {
            if (confirmed) TryAbort(host);
            Fail(e.Message);
        }
    }

    /// <summary>"Later": hides the banner for this run (a download in progress or a restart under way cannot be dismissed).</summary>
    [RelayCommand]
    public void Dismiss()
    {
        if (State is not (UpdateState.Available or UpdateState.Failed)) return;
        State = UpdateState.Dismissed;
        CleanStaging();
    }

    private void Fail(string reason)
    {
        _zipPath = null;
        CleanStaging();
        Failure = reason;
        State = UpdateState.Failed;
    }

    private static void TryAbort(IUpdateHost host)
    {
        try { host.ExitAborted(); } catch (Exception e) { Trace.WriteLine("ExitAborted failed: " + e.Message); }
    }

    private void CleanStaging()
    {
        _zipPath = null;
        if (_stagingUsed is not { } dir) return;
        _stagingUsed = null;
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) { Trace.WriteLine("Update staging folder not removed: " + e.Message); }
    }

    private static string Describe(DownloadResult result) => result.Failure switch
    {
        DownloadFailure.GhMissing => "The GitHub CLI (gh) was not found.",
        DownloadFailure.HashMismatch => "The downloaded file does not match its checksum; it was discarded and nothing was installed.",
        DownloadFailure.FileMissing => "The downloaded file is missing" + Suffix(result.Detail),
        DownloadFailure.Cancelled => "The download was cancelled.",
        _ => "The download failed" + Suffix(result.Detail),
    };

    private static string Suffix(string? detail) => string.IsNullOrWhiteSpace(detail) ? "." : $" ({detail.Trim()}).";

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
