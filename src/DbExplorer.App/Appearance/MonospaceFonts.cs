using Avalonia.Media;

namespace DbExplorer.App.Appearance;

/// <summary>
/// The installed families that look like code fonts. The platform font manager does not say which families are monospaced and
/// measuring hundreds of glyph typefaces would stall the dialog, so a family qualifies by name (the well-known code fonts and
/// anything called Mono, Code, Courier, Console, Typewriter or Fixed). Read only when the Editor page is built.
/// </summary>
public static class MonospaceFonts
{
    private static readonly string[] Markers =
    [
        "mono", "consolas", "courier", "console", "code", "typewriter", "fixed", "terminal", "menlo", "hack", "iosevka", "fira", "jetbrains",
        "inconsolata", "cascadia", "dejavu sans mono", "ubuntu mono", "anonymous pro", "input", "pt mono", "andale",
    ];

    public static bool LooksMonospaced(string family) =>
        family.Length > 0 && Markers.Any(m => family.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>The subset of <paramref name="families"/> that looks monospaced, sorted, without duplicates.</summary>
    public static IReadOnlyList<string> Filter(IEnumerable<string> families) =>
        families.Where(LooksMonospaced).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The code fonts the platform has installed.</summary>
    public static IReadOnlyList<string> Installed()
    {
        try { return Filter(FontManager.Current.SystemFonts.Select(f => f.Name)); }
        catch (Exception) { return []; }
    }
}
