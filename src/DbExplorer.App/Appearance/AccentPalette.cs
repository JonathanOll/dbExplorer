using Avalonia.Media;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Appearance;

/// <summary>The tokens one accent colour defines in one theme variant (hex strings: this is the data behind the theme tokens).</summary>
public sealed record AccentColors(
    string Accent, string Strong, string ButtonBg, string ButtonHover, string OnAccent,
    string Soft, string SoftHover, string Ring, string Link);

/// <summary>
/// The six accent presets. Each one overrides the accent token family of both variants. Blue is the original palette of
/// <c>Theme.axaml</c>. Every button fill keeps at least 4.5:1 against <c>DbxOnAccent</c> (checked by a unit test), and the accent
/// itself keeps readable contrast against the content background because it is also used for links and icons.
/// </summary>
public static class AccentPalette
{
    public static IReadOnlyList<AccentPreset> Presets { get; } = Enum.GetValues<AccentPreset>();

    public static AccentColors For(AccentPreset preset, bool dark) => (preset, dark) switch
    {
        (AccentPreset.Teal, false) => new("#0F766E", "#0B524C", "#0F766E", "#0D635C", "#FFFFFF", "#DDF3EF", "#CBEBE6", "#0F766E", "#0B6E66"),
        (AccentPreset.Teal, true) => new("#5EC9BD", "#8FE0D6", "#0F766E", "#117D73", "#FFFFFF", "#14393A", "#1A4A4B", "#6AD4C8", "#6AD4C8"),
        (AccentPreset.Green, false) => new("#15803D", "#115E2D", "#15803D", "#136C34", "#FFFFFF", "#DFF4E6", "#CDEBD9", "#15803D", "#117234"),
        (AccentPreset.Green, true) => new("#5BD08A", "#8BE0AB", "#15803D", "#168739", "#FFFFFF", "#17341F", "#1E4429", "#6FD99A", "#6FD99A"),
        (AccentPreset.Orange, false) => new("#C2410C", "#8F3009", "#C2410C", "#A8380A", "#FFFFFF", "#FDEADB", "#FADCC6", "#C2410C", "#B03A0A"),
        (AccentPreset.Orange, true) => new("#F59E5B", "#FBBF8A", "#C2410C", "#C9460E", "#FFFFFF", "#3D2615", "#4E301A", "#F7A96A", "#F7A96A"),
        (AccentPreset.Pink, false) => new("#BE185D", "#8B1043", "#BE185D", "#A3144F", "#FFFFFF", "#FCE4EE", "#F9D3E3", "#BE185D", "#AB1553"),
        (AccentPreset.Pink, true) => new("#F472A8", "#F9A3C6", "#BE185D", "#D11D6A", "#FFFFFF", "#3F1B2E", "#4F2339", "#F68DB9", "#F68DB9"),
        (AccentPreset.Purple, false) => new("#7C3AED", "#5B21B6", "#7C3AED", "#6D2FD4", "#FFFFFF", "#EDE6FD", "#E1D6FB", "#7C3AED", "#6D2FD4"),
        (AccentPreset.Purple, true) => new("#A78BFA", "#C4B5FD", "#7C3AED", "#8346EE", "#FFFFFF", "#2A1F4D", "#35286A", "#B49DFB", "#B49DFB"),
        (_, false) => new("#3E63DD", "#3354C5", "#3E63DD", "#3556CC", "#FFFFFF", "#E4EAFD", "#D8E1FB", "#3E63DD", "#3556C4"),
        (_, true) => new("#7C97F5", "#9BB0F8", "#4565E0", "#4C6BE3", "#FFFFFF", "#29335E", "#313D70", "#8CA3F7", "#8AA3F7"),
    };

    /// <summary>The swatch shown in the settings (the fill of a primary button in the light theme).</summary>
    public static string Swatch(AccentPreset preset) => For(preset, dark: false).ButtonBg;

    /// <summary>The WCAG contrast ratio (1..21) between two colours.</summary>
    public static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>Mixes <paramref name="tint"/> into <paramref name="baseColor"/> by <paramref name="amount"/> (0..1): the soft row fills.</summary>
    public static Color Blend(Color baseColor, Color tint, double amount) => Color.FromRgb(
        (byte)Math.Round(baseColor.R + (tint.R - baseColor.R) * amount),
        (byte)Math.Round(baseColor.G + (tint.G - baseColor.G) * amount),
        (byte)Math.Round(baseColor.B + (tint.B - baseColor.B) * amount));
}
