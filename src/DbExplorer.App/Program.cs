using System.Runtime.Versioning;
using Avalonia;

namespace DbExplorer.App;

[SupportedOSPlatform("windows")]
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupTrace.Mark("main");

        // Claude Code starts the exe as its MCP server: no window, nothing of the UI is loaded.
        if (Array.IndexOf(args, Claude.McpMode.Flag) >= 0) return Claude.McpMode.Run(args);

        // A flag without a value is reported in a message box (and exits) instead of crashing inside the startup prefetch.
        if (AppPaths.CheckArguments(args) is { } problem)
        {
            StartupError.Show(problem);
            return 2;
        }

        StartupPrefetch.Begin(args); // config load runs on a worker thread while Avalonia initialises the platform
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

        // Software rendering keeps idle RAM at ~85 MB working set instead of ~155 MB (GPU/ANGLE stack) and
        // starts faster; a 200-row grid does not need the GPU. Set DBEXPLORER_RENDERING=gpu to opt out.
        if (!string.Equals(Environment.GetEnvironmentVariable("DBEXPLORER_RENDERING"), "gpu", StringComparison.OrdinalIgnoreCase))
            builder = builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });

        return builder;
    }
}
