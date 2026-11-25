using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.App.Views;

namespace DbExplorer.App.ViewModels;

/// <summary>A section of the shortcut list after filtering.</summary>
public sealed record ShortcutGroupViewModel(string Title, IReadOnlyList<ShortcutItem> Items);

/// <summary>
/// Every keyboard shortcut, from <see cref="ShortcutCatalog"/> (the one list the F1 card and the welcome page use). Read only:
/// shortcuts cannot be rebound. A search box filters by key or description; the whole list copies as a markdown table.
/// </summary>
public sealed partial class ShortcutsSettingsPage : SettingsPageViewModel
{
    private readonly SettingsContext _ctx;
    private string _dialogQuery = "";

    public ShortcutsSettingsPage(SettingsContext ctx)
        : base("shortcuts", "Shortcuts", "IconKeyboard", "The keyboard shortcuts of dbExplorer. They cannot be changed.")
    {
        _ctx = ctx;
        Rebuild();
    }

    public override bool IsGeneric => false;
    public override bool CanReset => false;

    /// <summary>The page's own search box (key or description).</summary>
    [ObservableProperty] private string _searchText = "";

    public IReadOnlyList<ShortcutGroupViewModel> VisibleSections { get; private set; } = [];

    public bool HasNoShortcuts => VisibleSections.Count == 0;

    partial void OnSearchTextChanged(string value) => Rebuild();

    public override bool MatchesQuery(string query) =>
        string.IsNullOrWhiteSpace(query)
        || "shortcuts keyboard keys hotkeys".Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
        || Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
        || ShortcutCatalog.Sections.Any(s => s.Items.Any(i => Matches(i, query)));

    public override void Filter(string query)
    {
        _dialogQuery = query ?? "";
        base.Filter(query ?? "");
        Rebuild();
    }

    private static bool Matches(ShortcutItem item, string query) =>
        item.Keys.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) || item.Text.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    private void Rebuild()
    {
        IEnumerable<ShortcutItem> Keep(ShortcutSection section)
        {
            // A query naming the section ("table") keeps its shortcuts; otherwise each item is matched on its own.
            var items = section.Items.AsEnumerable();
            foreach (var query in new[] { SearchText, PageNamed(_dialogQuery) ? "" : _dialogQuery })
                if (!string.IsNullOrWhiteSpace(query) && !section.Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
                    items = items.Where(i => Matches(i, query));
            return items;
        }

        VisibleSections = ShortcutCatalog.Sections
            .Select(s => new ShortcutGroupViewModel(s.Title, Keep(s).ToList()))
            .Where(g => g.Items.Count > 0)
            .ToList();
        OnPropertyChanged(nameof(VisibleSections));
        OnPropertyChanged(nameof(HasNoShortcuts));
    }

    private bool PageNamed(string query) =>
        string.IsNullOrWhiteSpace(query) || Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
        || "shortcuts keyboard keys hotkeys".Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The full list as markdown (one table per section), whatever the search shows.</summary>
    public static string ToMarkdown()
    {
        var sb = new StringBuilder("# dbExplorer keyboard shortcuts\n");
        foreach (var section in ShortcutCatalog.Sections)
        {
            sb.Append("\n## ").Append(section.Title).Append("\n\n| Shortcut | Action |\n| --- | --- |\n");
            foreach (var item in section.Items) sb.Append("| `").Append(item.Keys).Append("` | ").Append(item.Text.Replace("|", "\\|")).Append(" |\n");
        }
        return sb.ToString();
    }

    [RelayCommand]
    private async Task CopyMarkdownAsync()
    {
        if (_ctx.CopyText is { } copy) await copy(ToMarkdown());
    }
}
