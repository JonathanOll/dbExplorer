using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Appearance;

/// <summary>
/// Turns <see cref="AppSettings"/> into theme state: the theme variant, the accent token family (colours of the existing
/// brushes are changed in place, so every static and dynamic reference follows), grid density and fonts (resources set on the
/// application, which win over <c>Theme.axaml</c>) and the UI scale (a resource the window wrapper binds to, see
/// <see cref="UiScaleHost"/>). Idempotent and cheap: the default settings change nothing visible.
/// </summary>
public static class AppearanceApplier
{
    private static readonly Dictionary<(string Key, bool Dark), Color> Originals = new();

    private static readonly (string Key, Func<AccentColors, string> Get)[] AccentTokens =
    [
        ("DbxAccent", c => c.Accent), ("DbxAccentStrong", c => c.Strong), ("DbxAccentButtonBg", c => c.ButtonBg),
        ("DbxAccentButtonHover", c => c.ButtonHover), ("DbxOnAccent", c => c.OnAccent), ("DbxAccentSoft", c => c.Soft),
        ("DbxAccentSoftHover", c => c.SoftHover), ("DbxFocusRing", c => c.Ring), ("DbxLink", c => c.Link),
    ];

    // Tinted fills derived from the accent: key, amount of accent mixed into the content background (light, dark).
    private static readonly (string Key, double Light, double Dark)[] TintTokens =
    [
        ("DbxRowHover", 0.07, 0.12), ("DbxRowSelected", 0.20, 0.35), ("DbxRowSelectedInactive", 0.11, 0.20),
        ("DbxEditorSelection", 0.20, 0.38), ("DbxEditorCurrentLine", 0.05, 0.08),
    ];

    // What the application shows now. Starts as the defaults because Theme.axaml defines them: only differences are applied,
    // so one change does not make every dynamic resource of every open window re-evaluate.
    private static AppSettings s_applied = new();

    /// <summary>Forgets what was applied (tests that rebuild the application).</summary>
    public static void ResetTracking() => s_applied = new();

    public static void Apply(AppSettings settings, Application? app = null)
    {
        app ??= Application.Current;
        if (app is null) return;
        var (a, before) = (settings.Appearance, s_applied);
        var was = before.Appearance;
        if (a.Theme != was.Theme)
            app.RequestedThemeVariant = a.Theme switch
            {
                ThemeChoice.Light => ThemeVariant.Light,
                ThemeChoice.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };

        if (a.Accent != was.Accent) ApplyAccent(app, a.Accent);

        if (a.UiScalePercent != was.UiScalePercent)
        {
            var scale = a.UiScalePercent / 100.0;
            app.Resources["DbxUiScale"] = new ScaleTransform(scale, scale);
        }

        if (a.Density != was.Density)
        {
            var row = a.RowHeight;
            app.Resources["DbxGridRowHeight"] = row;
            app.Resources["DbxRowHeight"] = row;
            app.Resources["TreeViewItemMinHeight"] = row - 2;
        }

        if (settings.Results.GridFontSize != before.Results.GridFontSize) app.Resources["DbxGridFontSize"] = settings.Results.GridFontSize;
        if (settings.Editor.FontSize != before.Editor.FontSize) app.Resources["DbxEditorFontSize"] = settings.Editor.FontSize;
        if (settings.Editor.FontFamily != before.Editor.FontFamily) app.Resources["DbxEditorFont"] = EditorFont(app, settings.Editor);
        if (settings.Results.Zebra != before.Results.Zebra) SetZebra(app, settings.Results.Zebra);
        s_applied = settings;
    }

    /// <summary>The editor's font family: the chosen installed family (falling back to the theme stack), else the theme's mono font.</summary>
    public static FontFamily EditorFont(Application app, EditorSettings editor)
    {
        if (editor.FontFamily.Length > 0) return new FontFamily(editor.FontFamily + ", " + ThemeMonoName(app));
        return app.TryFindResource("DbxMonoFont", out var mono) && mono is FontFamily f ? f : FontFamily.Default;
    }

    private static string ThemeMonoName(Application app) =>
        app.TryFindResource("DbxMonoFont", out var mono) && mono is FontFamily f ? f.Name : "Consolas";

    private static void ApplyAccent(Application app, AccentPreset preset)
    {
        foreach (var dark in new[] { false, true })
        {
            var variant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            var colors = AccentPalette.For(preset, dark);
            foreach (var (key, get) in AccentTokens) SetColor(app, variant, key, Color.Parse(get(colors)));

            // Blue is the theme's own palette: put the original tinted fills back, other presets mix the accent into the content colour.
            var content = Original(app, variant, "DbxContentBg") ?? Colors.White;
            var tint = Color.Parse(colors.ButtonBg);
            foreach (var (key, light, darkAmount) in TintTokens)
            {
                if (preset == AccentPreset.Blue) { Restore(app, variant, key); continue; }
                SetColor(app, variant, key, AccentPalette.Blend(content, dark ? Color.Parse(colors.Accent) : tint, dark ? darkAmount : light));
            }
        }
    }

    private static void SetZebra(Application app, bool on)
    {
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            if (on) Restore(app, variant, "DbxGridZebra");
            else SetColor(app, variant, "DbxGridZebra", Colors.Transparent);
        }
    }

    private static SolidColorBrush? Brush(Application app, ThemeVariant variant, string key) =>
        app.TryGetResource(key, variant, out var value) ? value as SolidColorBrush : null;

    private static Color? Original(Application app, ThemeVariant variant, string key)
    {
        var dark = variant == ThemeVariant.Dark;
        if (Originals.TryGetValue((key, dark), out var c)) return c;
        if (Brush(app, variant, key) is not { } brush) return null;
        Originals[(key, dark)] = brush.Color;
        return brush.Color;
    }

    private static void SetColor(Application app, ThemeVariant variant, string key, Color color)
    {
        _ = Original(app, variant, key);   // remember the theme's own colour before the first change
        if (Brush(app, variant, key) is { } brush && brush.Color != color) brush.Color = color;
    }

    private static void Restore(Application app, ThemeVariant variant, string key)
    {
        if (Original(app, variant, key) is { } original && Brush(app, variant, key) is { } brush && brush.Color != original)
            brush.Color = original;
    }
}
