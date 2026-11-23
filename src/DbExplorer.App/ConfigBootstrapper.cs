using DbExplorer.Core.Config;
using DbExplorer.Core.Model;

namespace DbExplorer.App;

/// <summary>Outcome of the startup config load.</summary>
/// <param name="Notice">One-off message for the status bar (e.g. a corrupt file was moved aside).</param>
/// <param name="Banner">Persistent warning for the window, set when persistence is disabled.</param>
/// <param name="PersistenceEnabled">False in read-only-config mode: nothing may be written to the config file.</param>
public sealed record StartupConfig(AppConfig Config, string? Notice, string? Banner, bool PersistenceEnabled);

public static class ConfigBootstrapper
{
    /// <summary>
    /// Loads the config for startup. A corrupt file is moved to <c>.bad-&lt;timestamp&gt;</c> and the app starts
    /// empty with a notice. Any other failure (locked or unreadable file, recovery move refused) starts the app in
    /// read-only-config mode: empty config, nothing is ever written, the file is left untouched, and a banner says so.
    /// </summary>
    public static StartupConfig Load(ConfigStore store)
    {
        try
        {
            var loaded = store.LoadOrRecover();
            return new StartupConfig(loaded.Config, loaded.Warning, null, true);
        }
        catch (Exception e)
        {
            var banner = $"Could not read {Path.GetFileName(store.FilePath)} ({e.Message}). Changes will NOT be saved.";
            return new StartupConfig(AppConfig.Empty(), null, banner, false);
        }
    }
}
