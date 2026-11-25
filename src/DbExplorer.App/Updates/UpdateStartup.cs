using Avalonia.Threading;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Updates;

namespace DbExplorer.App.Updates;

/// <summary>The shell side of the update flow: the window's exit prompts and its normal close.</summary>
internal sealed class WindowUpdateHost(MainViewModel main, MainWindow window) : IUpdateHost
{
    public Task<bool> ConfirmExitAsync() => main.ConfirmExitAsync();
    public void ExitAborted() => main.ExitAborted();
    public void Shutdown() => window.CloseConfirmed();
}

/// <summary>Builds the update view model and schedules the background check: never before the window is shown and idle.</summary>
internal static class UpdateStartup
{
    /// <summary>How long after the window opened the check starts (the first paint and the tab restore come first).</summary>
    internal static readonly TimeSpan CheckDelay = TimeSpan.FromSeconds(8);

    /// <param name="autoCheck">The persisted setting.</param>
    /// <param name="suppressed">Off for this run (<c>--no-update-check</c> or the env var).</param>
    /// <param name="saveAutoCheck">Persists the setting when the user flips it.</param>
    public static void Wire(MainWindow window, MainViewModel main, string[] args, bool autoCheck, bool suppressed, Action<bool> saveAutoCheck)
    {
        // Only cheap objects are built here (no process, no reflection, no disk): all of that runs in the check, on a worker.
        var exe = Environment.ProcessPath;
        // Plain HTTPS first (no gh needed); gh only when HTTPS failed and gh is installed.
        var gh = new FallbackGhRunner(new GitHubHttpRunner(), new GhProcessRunner());
        var update = new UpdateViewModel(
            new GhUpdateChecker(gh, exe), new GhUpdateDownloader(gh),
            exe is null ? null : new SelfUpdateApplier(exe, Environment.ProcessId, args),
            autoCheck: autoCheck, suppressed: suppressed, saveAutoCheck: saveAutoCheck,
            post: a => Dispatcher.UIThread.Post(a))
        {
            Host = new WindowUpdateHost(main, window),
        };
        main.UseUpdates(update);
        if (suppressed || !autoCheck) return;

        window.Opened += (_, _) => Dispatcher.UIThread.Post(
            () => _ = Task.Delay(CheckDelay).ContinueWith(
                _ => Dispatcher.UIThread.Post(() => _ = update.CheckAsync(), DispatcherPriority.Background),
                TaskScheduler.Default),
            DispatcherPriority.Background);
    }
}
