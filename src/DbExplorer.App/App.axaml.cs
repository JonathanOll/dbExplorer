using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Config;
using DbExplorer.Core.Metadata;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App;

[SupportedOSPlatform("windows")] // DPAPI secret protector
public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Config was read on a worker thread while Avalonia initialised (see StartupPrefetch).
            var prefetched = StartupPrefetch.Take(desktop.Args ?? []);
            var paths = prefetched.Paths;
            var status = new StatusModel();
            var protector = prefetched.Protector;
            var store = prefetched.Store;
            var startup = prefetched.Startup;
            if (startup.Notice is not null) status.Error(startup.Notice);

            var window = new MainWindow();
            var dialogs = new DialogService(window);
            var cache = new MetadataCache(paths.CacheDir);
            var sessions = new SessionManager(store, cache, protector);
            var tree = new ConnectionTreeViewModel(
                store, startup.Config, sessions, status, dialogs, paths.DBeaverDir, startup.PersistenceEnabled);
            var main = new MainViewModel(tree, status, cache) { Banner = startup.Banner };
            var editDialogs = new EditDialogService(window);
            main.UseTableTabs(sessions, editDialogs);
            main.UseScriptTabs(new ScriptFiles(window), new Core.Scripting.ScriptSessionStore(paths.ScriptsDir), editDialogs);
            // Only stores the dialog service and the clipboard delegate: the dbupdate folder is not read until the gear menu opens.
            main.UseUpgrade(new UpgradeDialogService(window));
            main.CopyToClipboard = async text =>
            {
                if (window.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
            };
            // After the window is up: restoring the tabs (their editors, views) must not delay the first paint. Only tab shells
            // are built; nothing connects until a table tab is first selected (the active one loads at once).
            SessionStartup.Wire(window, main, startup, new Core.Config.TabSessionStore(paths.SessionFile),
                () => StartupPrefetch.TakeSession(prefetched));
            // The window persists the scripts and the tab session itself once its close is confirmed (MainWindow.OnClosed);
            // this covers an exit that does not go through the window (idempotent).
            desktop.Exit += (_, _) =>
            {
                main.PersistScripts();
                main.PersistSession();
            };
            // The update check starts a few seconds after the window opened (see UpdateStartup); nothing here touches the disk. Not in
            // read-only-config mode: the real setting is unknown there.
            var launchArgs = desktop.Args ?? [];
            Updates.UpdateStartup.Wire(window, main, launchArgs, startup.Config.AutoUpdateCheck,
                AppPaths.UpdateCheckDisabled(launchArgs) || !startup.PersistenceEnabled, enabled => tree.SetAutoUpdateCheck(enabled));
            window.RestorePlacement(paths.WindowStateFile);
            window.DataContext = main;
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}



