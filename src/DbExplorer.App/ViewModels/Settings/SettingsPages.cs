using DbExplorer.App.Settings;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>A boolean that lives outside <see cref="AppSettings"/> (a flag of the config, a view option): how to read and write it.</summary>
public sealed record BoolAccess(Func<bool> Get, Action<bool> Set);

/// <summary>What the settings pages need from the application. Optional parts are null in tests and when a feature is absent.</summary>
public sealed record SettingsContext(SettingsService Service)
{
    /// <summary>The dbupdate folder editor (validation and the info line are its delegates).</summary>
    public OptionsViewModel? Upgrade { get; init; }

    /// <summary>Called as soon as a valid dbupdate folder (or none) is committed in the Database upgrade page.</summary>
    public Action<string?>? ApplyDbUpdatePath { get; init; }

    public BoolAccess? AutoUpdateCheck { get; init; }
    /// <summary>The "record the query history" flag of the config.</summary>
    public BoolAccess? RecordHistory { get; init; }

    /// <summary>The history store (entry count, size, Clear history); null hides that group.</summary>
    public DbExplorer.Core.Scripting.QueryHistory? History { get; init; }

    /// <summary>Reads and stores the SQL formatter style (applied at once, persisted by the config).</summary>
    public Func<DbExplorer.Core.Scripting.SqlFormatOptions>? GetFormat { get; init; }
    public Action<DbExplorer.Core.Scripting.SqlFormatOptions>? SetFormat { get; init; }

    public BoolAccess? ShowFolders { get; init; }
    public BoolAccess? ShowGroups { get; init; }
    public BoolAccess? ShowScripts { get; init; }

    /// <summary>Copies text to the clipboard (shortcut list as markdown).</summary>
    public Func<string, Task>? CopyText { get; init; }

    /// <summary>Installed monospace font families offered by the editor page; null reads them from the platform when the page is built.</summary>
    public IReadOnlyList<string>? MonospaceFonts { get; init; }

    /// <summary>Applies an edit of <see cref="AppSettings"/> through the service (live and persisted).</summary>
    public void Edit(Func<AppSettings, AppSettings> change) => Service.Update(change);
}

/// <summary>
/// The list of settings pages, in the order of the left list. A feature adds its page with one line here (and its section in
/// <see cref="AppSettings"/>); pages whose backing feature is absent are left out rather than shown empty.
/// </summary>
public static class SettingsPages
{
    public static IReadOnlyList<SettingsPageViewModel> Create(SettingsContext ctx)
    {
        var pages = new List<SettingsPageViewModel>();
        if (ctx.ShowFolders is not null || ctx.ShowGroups is not null || ctx.ShowScripts is not null) pages.Add(new GeneralSettingsPage(ctx));
        pages.Add(new AppearanceSettingsPage(ctx));
        pages.Add(new EditorSettingsPage(ctx));
        pages.Add(new ResultsSettingsPage(ctx));
        if (ctx.RecordHistory is not null) pages.Add(new HistorySettingsPage(ctx));
        if (ctx.GetFormat is not null && ctx.SetFormat is not null) pages.Add(new FormatSettingsPage(ctx));
        pages.Add(new ShortcutsSettingsPage(ctx));
        if (ctx.AutoUpdateCheck is not null) pages.Add(new UpdatesSettingsPage(ctx));
        if (ctx.Upgrade is not null) pages.Add(new DatabaseUpgradeSettingsPage(ctx));
        return pages;
    }
}
