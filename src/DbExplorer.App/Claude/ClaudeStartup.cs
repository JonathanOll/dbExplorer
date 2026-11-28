using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Threading;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Claude;

namespace DbExplorer.App.Claude;

/// <summary>Gives the shell the Claude Code integration. Nothing is read or opened before the window shows.</summary>
[SupportedOSPlatform("windows")]
internal static class ClaudeStartup
{
    public static void Wire(Window window, MainViewModel main, AppPaths paths, IDialogService dialogs)
    {
        main.UseClaude(new ClaudeHooks(
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DbExplorer.exe"),
            paths.DataDir,
            paths.ClaudeMcpConfigFile,
            paths.LibraryDir,
            new ClaudeActivityLog(paths.ClaudeActivityFile),
            () => ClaudeLauncher.FindClaude(Environment.GetEnvironmentVariable("PATH"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), File.Exists),
            start => Process.Start(start)?.Dispose(),
            dialogs,
            handle => ClaudeBridge.Listen(ClaudeBridge.PipeName(paths.DataDir), handle),
            () => window.IsActive));
        main.ClaudeDispatch = async work => await Dispatcher.UIThread.InvokeAsync(work);
        // After the first paint, and only when the user turned the integration on (a flag of the config already loaded).
        window.Opened += (_, _) => Dispatcher.UIThread.Post(main.SyncClaudeListener, DispatcherPriority.Background);
    }
}
