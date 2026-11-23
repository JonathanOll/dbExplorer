using Avalonia.Controls;
using Avalonia.Threading;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Config;

namespace DbExplorer.App;

/// <summary>When the tabs of the last run come back: after the window opened, never before its first paint.</summary>
internal static class SessionStartup
{
    /// <param name="load">Gives the session read from disk (on app start: the one prefetched on a worker thread).</param>
    public static void Wire(Window window, MainViewModel main, StartupConfig startup, TabSessionStore store, Func<TabSessionLoad> load) =>
        window.Opened += (_, _) => Dispatcher.UIThread.Post(() => Restore(main, startup, store, load), DispatcherPriority.Background);

    /// <summary>
    /// Hands the session to the shell and restores. In read-only-config mode (<see cref="StartupConfig.PersistenceEnabled"/>
    /// false) the session file is neither read nor written: only the unsaved scripts come back, as before; the same when
    /// the file exists but cannot be read. A missing or corrupt (moved aside) file means "unsaved scripts only", and a new
    /// session is written from then on. Never throws.
    /// </summary>
    internal static void Restore(MainViewModel main, StartupConfig startup, TabSessionStore store, Func<TabSessionLoad> load)
    {
        string? notice = null;
        try
        {
            if (startup.PersistenceEnabled)
            {
                var loaded = load();
                notice = loaded.Notice;
                // A file that exists but could not be read (locked for a moment) must not be replaced by a near-empty session:
                // this run neither restores from it nor writes it (same rule as the config).
                if (!loaded.Unreadable) main.UseTabSession(store, loaded.Session);
            }
        }
        catch (Exception)
        {
            // no session: restore the unsaved scripts only
        }
        main.RestoreTabs();
        if (notice is not null) main.Status.Error(notice);
    }
}
