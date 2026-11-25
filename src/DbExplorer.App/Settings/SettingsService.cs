using DbExplorer.Core.Model;

namespace DbExplorer.App.Settings;

/// <summary>
/// The live user settings. Everything that depends on a setting (the theme applier, script editors, the cell formatter, the
/// settings pages) reads <see cref="Current"/> and listens to <see cref="Changed"/>; nothing caches its own copy. A change is
/// applied in memory at once and then handed to <see cref="Persist"/> (the config store), so the UI never waits for the disk.
/// </summary>
public sealed class SettingsService
{
    /// <summary>The process-wide instance (replaced once at start-up with the settings read from the config; tests swap it).</summary>
    public static SettingsService Instance { get; set; } = new(new AppSettings());

    public SettingsService(AppSettings initial) => Current = (initial ?? new()).Normalized();

    public AppSettings Current { get; private set; }

    /// <summary>Saves the new settings; returns false when they could not be kept (status bar already says why). Null: not persisted.</summary>
    public Func<AppSettings, bool>? Persist { get; set; }

    /// <summary>Raised after every change, on the calling thread (the UI thread in the app).</summary>
    public event Action<AppSettings>? Changed;

    /// <summary>Applies a change (values are normalised, e.g. clamped), notifies, persists. Returns whether it is stored.</summary>
    public bool Update(Func<AppSettings, AppSettings> change)
    {
        var next = change(Current).Normalized();
        if (next == Current) return true;
        Current = next;
        Changed?.Invoke(next);
        return Persist?.Invoke(next) ?? true;
    }
}
