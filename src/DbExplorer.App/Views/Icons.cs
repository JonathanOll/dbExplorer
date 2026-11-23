using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Views;

/// <summary>
/// Small value converters for the views: icon lookup by resource key (the geometries live in Styles/Icons.axaml and are
/// looked up once), plus a few text helpers. Colours are never decided here: they come from theme brushes in XAML.
/// </summary>
public static class Icons
{
    private static readonly Dictionary<string, Geometry?> Cache = [];

    /// <summary>Icon resource key (e.g. "IconTable") to its geometry; null when unknown.</summary>
    public static readonly IValueConverter ByKey = new FuncValueConverter<string?, Geometry?>(Lookup);

    /// <summary>True (a view) gives the view icon, false the table icon.</summary>
    public static readonly IValueConverter TableOrView = new FuncValueConverter<bool, Geometry?>(isView => Lookup(isView ? "IconView" : "IconTable"));

    /// <summary>Status bar severity: true (error) gives the error icon, false the info icon.</summary>
    public static readonly IValueConverter StatusIcon = new FuncValueConverter<bool, Geometry?>(isError => Lookup(isError ? "IconError" : "IconInfo"));

    /// <summary>A script title starts with a bullet while it has unsaved text; the tab draws a dot instead.</summary>
    public static readonly IValueConverter WithoutBullet = new FuncValueConverter<string?, string?>(t => t is not null && t.StartsWith("• ", StringComparison.Ordinal) ? t[2..] : t);

    public static readonly IValueConverter EngineName = new FuncValueConverter<DbKind, string>(kind => kind switch
    {
        DbKind.Postgres => "PostgreSQL",
        DbKind.SqlServer => "SQL Server",
        DbKind.Sqlite => "SQLite",
        _ => kind.ToString(),
    });

    /// <summary>The count shown after a folder name, e.g. "(3)".</summary>
    public static readonly IValueConverter CountInParens = new FuncValueConverter<int, string>(n => $"({n})");

    /// <summary>The geometry of an icon resource key, or null.</summary>
    public static Geometry? Find(string key) => Lookup(key);

    private static Geometry? Lookup(string? key)
    {
        if (key is null) return null;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        Geometry? geometry = null;
        if (Application.Current is { } app && app.TryGetResource(key, null, out var value)) geometry = value as Geometry;
        if (geometry is not null) Cache[key] = geometry;   // a miss (resources not loaded yet) is not remembered
        return geometry;
    }
}
