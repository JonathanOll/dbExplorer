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
    private static Task<TabSessionLoad>? _session;

    public static void Begin(string[] args)
    {
        _config = Task.Run(() => LoadConfig(args));
        // The tab session (one tiny file) is read right after the config, still off the UI thread. A separate task: the startup
        // path (Take) never waits for it, only the post-paint restore does.
        _session = _config.ContinueWith(c => LoadSession(c.Result), TaskScheduler.Default);
    }

    /// <summary>The prefetched result, or a synchronous load when <see cref="Begin"/> was not called (tests).</summary>
    public static Prefetched Take(string[] args) =>
        _config is null ? LoadConfig(args) : _config.GetAwaiter().GetResult();

    /// <summary>The session read from disk: no session when persistence is disabled, the file is missing or anything failed.</summary>
    public static TabSessionLoad TakeSession(Prefetched prefetched)
    {
        try
        {
            return _session is null ? LoadSession(prefetched) : _session.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return new TabSessionLoad(null, null);
        }
    }

    private static TabSessionLoad LoadSession(Prefetched prefetched) =>
        prefetched.Startup.PersistenceEnabled
            ? new TabSessionStore(prefetched.Paths.SessionFile).Load()
            : new TabSessionLoad(null, null);   // read-only-config mode: nothing may be touched, not even a corrupt file moved

    private static Prefetched LoadConfig(string[] args)
    {
        var paths = AppPaths.Resolve(args);
        var protector = new DpapiSecretProtector();
        var store = new ConfigStore(paths.ConfigFile, protector);
        return new Prefetched(paths, protector, store, ConfigBootstrapper.Load(store));
    }
}
