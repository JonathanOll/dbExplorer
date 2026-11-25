using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// The Settings dialog: a left list of category pages (<see cref="SettingsPages"/>), the selected page on the right and a search
/// box that narrows both to the settings whose label matches. There is no OK or Cancel: every change applies and is saved at
/// once through <see cref="SettingsContext"/>; "Reset to defaults" works per page.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly SettingsContext _ctx;

    public SettingsViewModel(SettingsContext ctx, string? initialPageId = null)
    {
        _ctx = ctx;
        Pages = SettingsPages.Create(ctx);
        VisiblePages = new ObservableCollection<SettingsPageViewModel>(Pages);
        _selectedPage = Pages.FirstOrDefault(p => p.Id == initialPageId) ?? Pages[0];
        ctx.Service.Changed += OnSettingsChanged;
    }

    public IReadOnlyList<SettingsPageViewModel> Pages { get; }

    /// <summary>The pages with a setting that matches the search, in order.</summary>
    public ObservableCollection<SettingsPageViewModel> VisiblePages { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResetPage))]
    private SettingsPageViewModel? _selectedPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearch), nameof(NoPageMatches))]
    private string _searchText = "";

    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>The search matches no setting of any page.</summary>
    public bool NoPageMatches => VisiblePages.Count == 0;

    public bool CanResetPage => SelectedPage?.CanReset == true;

    /// <summary>The dbupdate folder editor (the Database upgrade page), for the code that saves it when the dialog closes.</summary>
    public OptionsViewModel? Upgrade => _ctx.Upgrade;

    partial void OnSearchTextChanged(string value)
    {
        VisiblePages.Clear();
        foreach (var page in Pages)
        {
            page.Filter(value);
            if (page.MatchesQuery(value)) VisiblePages.Add(page);
        }
        if (SelectedPage is null || !VisiblePages.Contains(SelectedPage)) SelectedPage = VisiblePages.FirstOrDefault();
        OnPropertyChanged(nameof(NoPageMatches));
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    [RelayCommand]
    private void ResetPage() => SelectedPage?.ResetToDefaults();

    public void Select(string pageId)
    {
        if (Pages.FirstOrDefault(p => p.Id == pageId) is { } page)
        {
            SearchText = "";
            SelectedPage = page;
        }
    }

    /// <summary>
    /// Stores what is still pending (the dbupdate folder is checked before it is kept). False when the folder is not usable: the
    /// dialog then stays open on that page, with the error under the box.
    /// </summary>
    public bool TryCommitPending()
    {
        var upgrade = Pages.OfType<DatabaseUpgradeSettingsPage>().FirstOrDefault();
        if (upgrade is null || upgrade.Commit()) return true;
        Select(upgrade.Id);
        return false;
    }

    private void OnSettingsChanged(Core.Model.AppSettings _)
    {
        foreach (var page in Pages) page.Refresh();
    }

    public void Dispose() => _ctx.Service.Changed -= OnSettingsChanged;
}
