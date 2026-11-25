using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Querying;

namespace DbExplorer.App.ViewModels;

/// <summary>One applied filter shown as a removable chip under the toolbar.</summary>
/// <param name="Column">The column, or the empty string for the raw WHERE clause.</param>
/// <param name="Text">What was typed in the filter box, or the WHERE condition.</param>
/// <param name="RemoveCommand">Clears this filter and reloads.</param>
public sealed record FilterChip(string Column, string Text, IAsyncRelayCommand RemoveCommand)
{
    public bool IsWhere => Column.Length == 0;

    public string Label => IsWhere ? "WHERE " + Text : Column + " " + Text;

    public string RemoveTip => IsWhere ? "Remove the WHERE condition" : $"Remove the filter on {Column}";
}

/// <summary>
/// What the table tab view shows besides the rows: filter chips and their draft state, the loading / empty / failed states of the
/// grid, the pending-changes summary. All of it is derived from state the tab already has; nothing here queries.
/// </summary>
public sealed partial class TableTabViewModel
{
    private IReadOnlyList<FilterChip> _filterChips = [];
    private bool _loadFailed;

    // ---- filters ----

    /// <summary>The filters the rows on screen were loaded with (column filters, then the WHERE condition).</summary>
    public IReadOnlyList<FilterChip> FilterChips => _filterChips;

    /// <summary>The rows on screen are filtered.</summary>
    public bool HasActiveFilters => _filterChips.Count > 0;

    /// <summary>The filter boxes or the WHERE box differ from what the rows on screen were loaded with (typed, not applied).</summary>
    public bool IsFilterDirty
    {
        get
        {
            var applied = _applied;
            var typed = ColumnFilters.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            var typedWhere = string.IsNullOrWhiteSpace(RawWhere) ? null : RawWhere;
            if (applied is null) return typed.Count > 0 || typedWhere is not null;
            if (typedWhere != applied.RawWhere || typed.Count != applied.Filters.Count) return true;
            return applied.Filters.Any(f => !typed.TryGetValue(f.Column, out var text) || text != f.Text);
        }
    }

    /// <summary>"Filters" or "Filters (2)": the toggle of the filter boxes says how many filters are on. A text, not a badge that appears: its width must follow.</summary>
    public string FiltersLabel => HasActiveFilters ? $"Filters ({_filterChips.Count})" : "Filters";

    /// <summary>"Not applied yet" while the boxes hold something other than what the rows were loaded with.</summary>
    public string NotAppliedText => IsFilterDirty ? "Not applied yet" : "";

    /// <summary>The applied filter text of a column (what the rows were loaded with), for the filter box's "not applied yet" mark.</summary>
    public string GetAppliedFilter(string column) =>
        _applied?.Filters.FirstOrDefault(f => string.Equals(f.Column, column, StringComparison.OrdinalIgnoreCase))?.Text ?? "";

    /// <summary>Clears the filter of one column and reloads (the other filters and the WHERE condition stay).</summary>
    [RelayCommand]
    public async Task ClearFilterAsync(string column)
    {
        ColumnFilters.Remove(column);
        NotifyFiltersChanged();
        await ApplyFiltersAsync();
    }

    /// <summary>Clears every column filter and the WHERE condition, then reloads.</summary>
    [RelayCommand]
    public async Task ClearFiltersAsync()
    {
        ColumnFilters.Clear();
        RawWhere = null;
        NotifyFiltersChanged();
        await ApplyFiltersAsync();
    }

    /// <summary>Clears the WHERE condition and reloads.</summary>
    [RelayCommand]
    public async Task ClearWhereAsync()
    {
        RawWhere = null;
        await ApplyFiltersAsync();
    }

    private void RefreshFilterState()
    {
        var chips = new List<FilterChip>();
        if (_applied is { } q)
        {
            foreach (var f in q.Filters)
            {
                var column = f.Column;
                chips.Add(new FilterChip(column, f.Text, new AsyncRelayCommand(() => ClearFilterAsync(column))));
            }
            if (q.RawWhere is { } where) chips.Add(new FilterChip("", where, new AsyncRelayCommand(ClearWhereAsync)));
        }
        _filterChips = chips;
        OnPropertyChanged(nameof(FilterChips));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(IsFilterDirty));
        OnPropertyChanged(nameof(NotAppliedText));
        OnPropertyChanged(nameof(FiltersLabel));
        OnPropertyChanged(nameof(ColumnFilters));   // the header's filter boxes recompute their "applied / not applied" mark
        NotifyViewState();
    }

    partial void OnRawWhereChanged(string? value)
    {
        OnPropertyChanged(nameof(IsFilterDirty));
        OnPropertyChanged(nameof(NotAppliedText));
    }

    // ---- loading, empty and failed states of the grid ----

    /// <summary>First load in progress (or about to start): nothing to show yet, say so.</summary>
    public bool ShowInitialLoad => (IsLoading || IsDeferred) && Rows.Count == 0 && ErrorMessage is null;

    /// <summary>A reload is running over rows that are still shown: dim them and ignore the pointer.</summary>
    public bool IsReloading => IsLoading && Rows.Count > 0;

    /// <summary>Loaded fine, filtered, and nothing matches.</summary>
    public bool ShowNoMatches => !IsLoading && Rows.Count == 0 && HasActiveFilters && _applied is not null && ErrorMessage is null;

    /// <summary>Loaded fine, not filtered, and the table is empty.</summary>
    public bool ShowEmptyTable => !IsLoading && Rows.Count == 0 && !HasActiveFilters && _applied is not null && ErrorMessage is null;

    /// <summary>The load failed and there is nothing to show instead.</summary>
    public bool ShowLoadFailed => !IsLoading && Rows.Count == 0 && ErrorMessage is not null && _applied is null;

    /// <summary>The last load failed (or the connection is gone) while older rows are still shown: they may be out of date.</summary>
    public bool ShowStaleRows => Rows.Count > 0 && !IsLoading && (_loadFailed || NeedsReload);

    /// <summary>The tab is busy writing or reading: the thin progress bar runs.</summary>
    public bool IsBusy => IsLoading || IsSaving;

    /// <summary>Text of the pending-changes bar's primary button.</summary>
    public string SaveButtonText => IsSaving ? "Saving…" : "Review and save…";

    private void NotifyViewState()
    {
        OnPropertyChanged(nameof(ShowInitialLoad));
        OnPropertyChanged(nameof(IsReloading));
        OnPropertyChanged(nameof(ShowNoMatches));
        OnPropertyChanged(nameof(ShowEmptyTable));
        OnPropertyChanged(nameof(ShowLoadFailed));
        OnPropertyChanged(nameof(ShowStaleRows));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(SaveButtonText));
    }

    partial void OnIsLoadingChanged(bool value) => NotifyViewState();

    partial void OnErrorMessageChanged(string? value) => NotifyViewState();

    partial void OnNeedsReloadChanged(bool value) => NotifyViewState();

    partial void OnIsSavingChanged(bool value) => NotifyViewState();

    // The grid shows Rows: a replaced collection moves the empty state at once. Rows added or dropped by editing go through
    // NotifyPending (the view-model must not subscribe to the collection: the grid is meant to see only a private mirror of it).
    partial void OnRowsChanged(ObservableCollection<RowViewModel> value) => NotifyViewState();

    // ---- pending changes ----

    /// <summary>Loaded rows with a changed cell.</summary>
    public int PendingEditedCount => _pending.Count(r => !r.IsNew && !r.IsDeleted);

    /// <summary>New rows that will be inserted.</summary>
    public int PendingAddedCount => _pending.Count(r => r.IsNew && !r.IsDeleted);

    /// <summary>Loaded rows marked for deletion.</summary>
    public int PendingDeletedCount => _pending.Count(r => r.IsDeleted && !r.IsNew);

    /// <summary>"2 edited · 1 added · 1 deleted": what the pending changes are made of.</summary>
    public string PendingSummary
    {
        get
        {
            var parts = new List<string>(3);
            if (PendingEditedCount > 0) parts.Add($"{PendingEditedCount} edited");
            if (PendingAddedCount > 0) parts.Add($"{PendingAddedCount} added");
            if (PendingDeletedCount > 0) parts.Add($"{PendingDeletedCount} deleted");
            return string.Join(" · ", parts);
        }
    }

    private void NotifyPendingBreakdown()
    {
        OnPropertyChanged(nameof(PendingEditedCount));
        OnPropertyChanged(nameof(PendingAddedCount));
        OnPropertyChanged(nameof(PendingDeletedCount));
        OnPropertyChanged(nameof(PendingSummary));
        OnPropertyChanged(nameof(SaveButtonText));
        NotifyViewState();   // a new row or a dropped one may fill or empty the grid
    }

    /// <summary>Why the edit buttons are limited (read-only connection, a view, no key), or null when editing is fully possible. Shown as the badge's tooltip.</summary>
    public string? EditBadgeTip => EditBadge is null ? null : EditBlockedReason();
}
