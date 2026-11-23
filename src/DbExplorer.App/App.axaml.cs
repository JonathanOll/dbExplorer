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
            // After the window is up: creating the editor of a restored script must not delay the first paint.
            window.Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
                () => main.RestoreScripts(), Avalonia.Threading.DispatcherPriority.Background);
            // The window persists the scripts itself once its close is confirmed (MainWindow.OnClosed); this covers an exit
            // that does not go through the window (idempotent).
            desktop.Exit += (_, _) => main.PersistScripts();
            window.RestorePlacement(paths.WindowStateFile);
            window.DataContext = main;
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}



