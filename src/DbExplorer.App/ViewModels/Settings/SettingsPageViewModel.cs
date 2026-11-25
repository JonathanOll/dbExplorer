using CommunityToolkit.Mvvm.ComponentModel;

namespace DbExplorer.App.ViewModels;

/// <summary>A titled block of settings inside a page; <see cref="Items"/> are the ones that match the current search.</summary>
public sealed record SettingGroupViewModel(string Title, IReadOnlyList<SettingItemViewModel> Items);

/// <summary>
/// One category of the Settings dialog. A plain page is just groups of <see cref="SettingItemViewModel"/>: the dialog renders
/// them with no view code of its own, so a feature adds a page by deriving from this class, adding groups in the constructor and
/// listing it in <see cref="SettingsPages"/>. A page with its own layout (shortcuts, database upgrade) also gets a DataTemplate.
/// </summary>
public class SettingsPageViewModel(string id, string title, string iconKey, string? subtitle = null) : ObservableObject
{
    private readonly List<(string Title, List<SettingItemViewModel> Items)> _groups = [];
    private string _query = "";

    public string Id { get; } = id;
    public string Title { get; } = title;

    /// <summary>Resource key of the icon in <c>Styles/Icons.axaml</c>.</summary>
    public string IconKey { get; } = iconKey;
    public string? Subtitle { get; } = subtitle;

    /// <summary>Every item of the page, in order.</summary>
    public IEnumerable<SettingItemViewModel> Items => _groups.SelectMany(g => g.Items);

    /// <summary>The groups with the items that match the search (a group without a match is left out).</summary>
    public IReadOnlyList<SettingGroupViewModel> VisibleGroups { get; private set; } = [];

    /// <summary>Extra content shown under the generic groups (the formatting preview); the dialog needs a DataTemplate for its type.</summary>
    public virtual object? Footer => null;

    /// <summary>The page has its own template and shows no generic items.</summary>
    public virtual bool IsGeneric => true;

    /// <summary>The search has no match on this page (the dialog shows "No settings match").</summary>
    public bool HasNoMatch => IsGeneric && VisibleGroups.Count == 0;

    protected void AddGroup(string groupTitle, params SettingItemViewModel[] items)
    {
        _groups.Add((groupTitle, [.. items]));
        Filter(_query);
    }

    /// <summary>True when the page or one of its settings matches the search text (an empty search matches everything).</summary>
    public virtual bool MatchesQuery(string query) =>
        string.IsNullOrWhiteSpace(query) || Items.Any(i => i.Matches(query)) || Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Narrows the visible items to the search text. A search that names the page itself shows the whole page.</summary>
    public virtual void Filter(string query)
    {
        _query = query ?? "";
        var pageNamed = !string.IsNullOrWhiteSpace(_query) && Title.Contains(_query.Trim(), StringComparison.OrdinalIgnoreCase);
        VisibleGroups = _groups
            .Select(g => new SettingGroupViewModel(g.Title, pageNamed ? g.Items : g.Items.Where(i => i.Matches(_query)).ToList()))
            .Where(g => g.Items.Count > 0)
            .ToList();
        OnPropertyChanged(nameof(VisibleGroups));
        OnPropertyChanged(nameof(HasNoMatch));
    }

    /// <summary>Whether "Reset to defaults" does anything on this page.</summary>
    public virtual bool CanReset => Items.Any();

    /// <summary>Puts every setting of the page back to its default; each one applies and persists like an edit.</summary>
    public virtual void ResetToDefaults()
    {
        foreach (var item in Items) item.Reset();
    }

    /// <summary>Re-reads the stored values (after a reset or a change from elsewhere).</summary>
    public virtual void Refresh()
    {
        foreach (var item in Items) item.Refresh();
    }
}
