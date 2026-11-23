using System.Runtime.Versioning;
using DbExplorer.Core.Config;

namespace DbExplorer.App;

/// <summary>What the startup path needs from disk, loaded on a worker thread while Avalonia sets up its platform.</summary>
internal sealed record Prefetched(AppPaths Paths, DpapiSecretProtector Protector, ConfigStore Store, StartupConfig Startup);

/// <summary>
/// Overlaps the config load (AppPaths, DPAPI protector, JSON read; about 45 ms) with Avalonia's platform initialisation on the
/// UI thread (about 90 ms). Deliberately limited to non-UI work: warming Avalonia control types from a worker thread was tried
/// and rejected (their static constructors need the UI thread's platform services and crash the app).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class StartupPrefetch
{
    private static Task<Prefetched>? _config;

    public static void Begin(string[] args) => _config = Task.Run(() => LoadConfig(args));

    /// <summary>The prefetched result, or a synchronous load when <see cref="Begin"/> was not called (tests).</summary>
    public static Prefetched Take(string[] args) =>
        _config is null ? LoadConfig(args) : _config.GetAwaiter().GetResult();

    private static Prefetched LoadConfig(string[] args)
    {
        var paths = AppPaths.Resolve(args);
        var protector = new DpapiSecretProtector();
        var store = new ConfigStore(paths.ConfigFile, protector);
        return new Prefetched(paths, protector, store, ConfigBootstrapper.Load(store));
    }
}
