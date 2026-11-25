using DbExplorer.App.Appearance;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>Theme, UI scale, accent colour and density. Applied live by <see cref="AppearanceApplier"/>.</summary>
public sealed class AppearanceSettingsPage : SettingsPageViewModel
{
    public AppearanceSettingsPage(SettingsContext ctx)
        : base("appearance", "Appearance", "IconPalette", "Theme, size, colour and density of the whole application.")
    {
        var d = new AppearanceSettings();
        AddGroup("Theme",
            Make.Choice(ctx, "Theme", "System follows the Windows light or dark setting.",
                [new("System", ThemeChoice.System), new("Light", ThemeChoice.Light), new("Dark", ThemeChoice.Dark)],
                s => s.Appearance.Theme, (s, v) => s with { Appearance = s.Appearance with { Theme = v } }, d.Theme, keywords: "dark light mode system"),
            Make.Choice(ctx, "Accent colour", "Selection, focus, links and the primary button.",
                [.. AccentPalette.Presets.Select(p => new ChoiceOption(p.ToString(), p, AccentPalette.Swatch(p)))],
                s => s.Appearance.Accent, (s, v) => s with { Appearance = s.Appearance with { Accent = v } }, d.Accent, keywords: "color highlight"));
        AddGroup("Size and density",
            Make.Number(ctx, "UI scale", "Scales text, icons and spacing. Menus and drop-down lists keep their size.",
                s => s.Appearance.UiScalePercent, (s, v) => s with { Appearance = s.Appearance with { UiScalePercent = (int)Math.Round(v) } },
                AppearanceSettings.MinScalePercent, AppearanceSettings.MaxScalePercent, 5, d.UiScalePercent, "%", keywords: "zoom size dpi"),
            Make.Choice(ctx, "Row density", "Row height of the data grids and the navigator.",
                [new("Comfortable", GridDensity.Comfortable), new("Compact", GridDensity.Compact), new("Spacious", GridDensity.Spacious)],
                s => s.Appearance.Density, (s, v) => s with { Appearance = s.Appearance with { Density = v } }, d.Density, keywords: "compact grid rows height"));
    }
}
