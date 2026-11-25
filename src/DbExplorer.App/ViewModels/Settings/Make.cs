using DbExplorer.App.Settings;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>Builders that bind a setting item to a property of <see cref="AppSettings"/> through the service.</summary>
internal static class Make
{
    public static ToggleSettingItem Toggle(SettingsContext ctx, string label, string? description,
        Func<AppSettings, bool> get, Func<AppSettings, bool, AppSettings> with, bool defaultValue, string keywords = "") =>
        new(label, description, () => get(ctx.Service.Current), v => ctx.Edit(s => with(s, v)), defaultValue) { Keywords = keywords };

    public static NumberSettingItem Number(SettingsContext ctx, string label, string? description,
        Func<AppSettings, double> get, Func<AppSettings, double, AppSettings> with,
        double min, double max, double step, double defaultValue, string? suffix = null, string keywords = "") =>
        new(label, description, () => get(ctx.Service.Current), v => ctx.Edit(s => with(s, v)), min, max, step, defaultValue, suffix) { Keywords = keywords };

    public static ChoiceSettingItem Choice<T>(SettingsContext ctx, string label, string? description,
        IReadOnlyList<ChoiceOption> options, Func<AppSettings, T> get, Func<AppSettings, T, AppSettings> with, T defaultValue,
        bool chips = true, string keywords = "") where T : notnull =>
        new(label, description, options, () => get(ctx.Service.Current), v => ctx.Edit(s => with(s, (T)v)), defaultValue)
        { UseChips = chips, Keywords = keywords };

    public static ToggleSettingItem Toggle(string label, string? description, BoolAccess access, bool defaultValue, string keywords = "") =>
        new(label, description, access.Get, access.Set, defaultValue) { Keywords = keywords };
}
