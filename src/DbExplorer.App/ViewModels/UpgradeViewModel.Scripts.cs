using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbExplorer.App.ViewModels;

/// <summary>The script list of the preview: the search by file name and the one-click toggle of the long-running scripts.</summary>
public sealed partial class UpgradeViewModel
{
    // ---- search: only changes what is shown, never what is ticked or run ----

    /// <summary>Case-insensitive part of a file name; empty shows every script.</summary>
    [NotifyPropertyChangedFor(nameof(IsFiltering), nameof(ShownText), nameof(HasNoMatch), nameof(NoMatchText))]
    [NotifyCanExecuteChangedFor(nameof(ClearFilterCommand))]
    [ObservableProperty] private string _filterText = "";

    /// <summary>The groups the list shows: <see cref="Groups"/> narrowed by <see cref="FilterText"/>, folders without a match left out.</summary>
    [NotifyPropertyChangedFor(nameof(ShownText), nameof(HasNoMatch))]
    [ObservableProperty] private IReadOnlyList<UpgradeFolderGroup> _visibleGroups = [];

    public bool IsFiltering => !string.IsNullOrEmpty(FilterText);

    /// <summary><c>3 of 27 shown</c> beside the search box.</summary>
    public string ShownText => $"{VisibleGroups.Sum(g => g.Rows.Count)} of {Rows.Count} shown";

    /// <summary>The search matches no file: the list shows its empty state.</summary>
    public bool HasNoMatch => IsFiltering && VisibleGroups.Count == 0;

    public string NoMatchText => $"No file name contains \"{FilterText}\". Hidden scripts keep their ticks.";

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnGroupsChanged(IReadOnlyList<UpgradeFolderGroup> value) => ApplyFilter();

    private void ApplyFilter()
    {
        var text = FilterText;
        VisibleGroups = string.IsNullOrEmpty(text)
            ? Groups
            : [.. Groups.Select(g => g with { Rows = [.. g.Rows.Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase))] })
                .Where(g => g.Rows.Count > 0)];
    }

    [RelayCommand(CanExecute = nameof(IsFiltering))]
    private void ClearFilter() => FilterText = "";

    // ---- long-running scripts: ticked or unticked together ----

    private IEnumerable<UpgradeScriptRow> LongRows => Rows.Where(r => r.IsLongRunning && r.CanSelect);

    /// <summary>At least one long-running script is ticked: the toggle unticks them all (a mixed list is disabled first).</summary>
    private bool AnyLongTicked => LongRows.Any(r => r.IsSelected);

    /// <summary><c>Untick long (3)</c> or <c>Tick long (3)</c>.</summary>
    public string LongToggleText
    {
        get
        {
            var count = LongRows.Count();
            if (count == 0) return "Tick long";
            return $"{(AnyLongTicked ? "Untick" : "Tick")} long ({count})";
        }
    }

    /// <summary>What the toggle does, or why it is off.</summary>
    public string LongToggleTip
    {
        get
        {
            if (Phase == UpgradePhase.Running) return "The scripts cannot be changed while a run is in progress.";
            var count = LongRows.Count();
            if (count == 0) return "No long-running script in this list.";
            var what = count == 1 ? "the long-running script" : $"all {count} long-running scripts";
            return $"{(AnyLongTicked ? "Untick" : "Tick")} {what}, including any hidden by the search";
        }
    }

    private bool CanToggleLong() => Phase == UpgradePhase.Preview && LongRows.Any();

    /// <summary>Unticks every long-running script when one is ticked, ticks them all otherwise; the search does not limit it.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleLong))]
    private void ToggleLong()
    {
        if (!CanToggleLong()) return;
        var tick = !AnyLongTicked;
        foreach (var row in LongRows) row.IsSelected = tick;
    }

    private void RefreshLongToggle()
    {
        OnPropertyChanged(nameof(LongToggleText));
        OnPropertyChanged(nameof(LongToggleTip));
        ToggleLongCommand.NotifyCanExecuteChanged();
    }
}
