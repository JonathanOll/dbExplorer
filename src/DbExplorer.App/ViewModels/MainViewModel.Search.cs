using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.ViewModels;

/// <summary>The "Search in scripts and data" overlay (Ctrl+Alt+F).</summary>
public partial class MainViewModel
{
    private SearchViewModel? _search;

    /// <summary>The search overlay currently shown, or null. The view model outlives the overlay: reopening shows the last results.</summary>
    [ObservableProperty] private SearchViewModel? _searchOverlay;

    [RelayCommand]
    public void ShowSearch() => ShowSearch(null);

    /// <summary>Opens the search overlay on <paramref name="scope"/> (the scope of the last use when null).</summary>
    public void ShowSearch(SearchScope? scope)
    {
        var search = _search ??= new SearchViewModel(new SearchHooks(
            OpenScriptSources, () => _library?.Root, () => Tree.Config.Connections.ToList(),
            (config, ct) => Tree.EnsureConnectedAsync(config, ct), OpenScriptMatchAsync,
            (session, table, filters) => OpenTable(session, table, filters)), Status);

        if (ReferenceEquals(SearchOverlay, search))   // already open: only switch the scope
        {
            if (scope is { } current) search.Scope = current;
            return;
        }

        search.RefreshConnections((ActiveTab?.Connection ?? Tree.SelectedConnection)?.Id);
        if (scope is { } wanted) search.Scope = wanted;
        // A one-line selection in the active script is the obvious thing to search for.
        if (ActiveScript is { Selection: { } sel } script && sel.End > sel.Start && sel.End <= script.Text.Length
            && script.Text[sel.Start..sel.End] is var picked && picked.Length <= 200 && picked.IndexOfAny(['\r', '\n']) < 0)
            search.Query = picked;

        void Closed()
        {
            search.CloseRequested -= Closed;
            if (ReferenceEquals(SearchOverlay, search)) SearchOverlay = null;
        }
        search.CloseRequested += Closed;
        SearchOverlay = search;
    }

    /// <summary>The open scripts as the search sees them: their current (possibly unsaved) text.</summary>
    private IReadOnlyList<SearchScriptSource> OpenScriptSources() =>
        Tabs.OfType<ScriptTabViewModel>()
            .Select(t => new SearchScriptSource(SearchViewModel.TabKeyPrefix + t.Id, t.Title.TrimStart('•', ' '), t.FilePath, t.Text, t.IsDirty || t.FilePath is null))
            .ToList();

    /// <summary>Goes to the script of a hit (its tab, or the file opened in a tab) and selects the hit.</summary>
    private async Task OpenScriptMatchAsync(ScriptMatch match)
    {
        ScriptTabViewModel? tab;
        if (match.Source.StartsWith(SearchViewModel.TabKeyPrefix, StringComparison.Ordinal))
        {
            tab = Tabs.OfType<ScriptTabViewModel>().FirstOrDefault(t => SearchViewModel.TabKeyPrefix + t.Id == match.Source);
            if (tab is null)
            {
                Status.Info("That script tab was closed since the search.");
                return;
            }
        }
        else
        {
            await OpenScriptPathAsync(match.Source);
            var full = Path.GetFullPath(match.Source);
            tab = Tabs.OfType<ScriptTabViewModel>()
                .FirstOrDefault(t => t.FilePath is { } p && string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase));
            if (tab is null) return;   // OpenScriptPathAsync already said why
        }

        ActiveTab = tab;
        tab.RevealLine(match.Line, match.Column, match.Length);
    }
}
