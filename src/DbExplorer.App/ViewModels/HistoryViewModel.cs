using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Plans;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.ViewModels;

/// <summary>Which entries the history list shows besides the text search.</summary>
public enum HistoryFilter { All, Errors, ThisConnection }

/// <summary>Modal window of the query history; implemented by the window layer, faked in tests.</summary>
public interface IHistoryDialogs
{
    /// <summary>Shows the history and returns when it is closed.</summary>
    Task ShowHistoryAsync(HistoryViewModel viewModel);
}

/// <summary>One row of the history list, shaped for display.</summary>
public sealed class HistoryItemViewModel(HistoryEntry entry, DateTime now, bool connectionExists)
{
    public HistoryEntry Entry { get; } = entry;

    public Guid Id => Entry.Id;

    public string Sql => Entry.Sql;

    /// <summary>The first two non-empty lines, trimmed: what the list shows.</summary>
    public string Preview { get; } = MakePreview(entry.Sql);

    public string ConnectionName => Entry.ConnectionName;

    /// <summary>False when the connection was deleted since: "Open" then uses the active connection.</summary>
    public bool ConnectionExists { get; } = connectionExists;

    public string ChipColor => ConnectionPalette.ColorFor(Entry.ConnectionId);

    public string TimeAgo { get; } = HistoryTime.Ago(entry.At, now);

    public string ExactTime => Entry.At.ToLocalTime().ToString("ddd d MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    public string DurationText { get; } = HistoryTime.Duration(entry.DurationMs);

    public bool HasRows => Entry.Rows is not null;

    public string RowsText => Entry.Rows is { } n ? (n == 1 ? "1 row" : $"{n:N0} rows") : "";

    public bool HasError => Entry.Error is not null;

    public string? Error => Entry.Error;

    public bool IsPlan => Entry.Plan != PlanMode.None;

    public string PlanText => Entry.Plan == PlanMode.Estimated ? "Estimated plan" : "Actual plan";

    /// <summary>"Shop (local) · main" under the details: the database the statement ran on.</summary>
    public string Where => string.IsNullOrWhiteSpace(Entry.Database) ? Entry.ConnectionName : $"{Entry.ConnectionName} · {System.IO.Path.GetFileName(Entry.Database)}";

    private static string MakePreview(string sql)
    {
        var lines = sql.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(2).ToList();
        return string.Join("\n", lines);
    }
}

/// <summary>Short, human texts for times and durations of the history.</summary>
public static class HistoryTime
{
    public static string Ago(DateTime atUtc, DateTime nowUtc)
    {
        var span = nowUtc - atUtc;
        if (span < TimeSpan.FromSeconds(45)) return "just now";
        if (span < TimeSpan.FromMinutes(60)) return $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))} min ago";
        if (span < TimeSpan.FromHours(24)) return $"{(int)span.TotalHours} h ago";
        if (span < TimeSpan.FromDays(2)) return "yesterday";
        if (span < TimeSpan.FromDays(30)) return $"{(int)span.TotalDays} days ago";
        return atUtc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }

    public static string Duration(long ms) =>
        ms < 1000 ? $"{ms:N0} ms" : ms < 60_000 ? (ms / 1000.0).ToString("0.0#", CultureInfo.InvariantCulture) + " s" : $"{ms / 60_000} min {ms % 60_000 / 1000} s";
}

/// <summary>
/// The Query history window: the recorded statements, newest first, filtered live by text, errors-only or the active
/// connection. Open / Run again / Copy go through callbacks of the shell; Clear all confirms through <see cref="IDialogService"/>.
/// Filtering is a synchronous in-memory query once <see cref="LoadAsync"/> has read the file (off the UI thread).
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    public const int ListLimit = 500;

    private readonly QueryHistory _history;
    private readonly Func<Guid, bool> _connectionExists;
    private readonly Guid? _currentConnection;
    private readonly IDialogService _dialogs;
    private readonly Func<HistoryEntry, bool, Task> _open;
    private readonly Func<string, Task>? _copy;
    private readonly Func<DateTime> _now;

    /// <param name="currentConnection">The connection of the active tab, for the "This connection" filter; null disables it.</param>
    /// <param name="open">Opens an entry in a new script tab; true runs it too.</param>
    public HistoryViewModel(
        QueryHistory history, Func<Guid, bool> connectionExists, Guid? currentConnection, IDialogService dialogs,
        Func<HistoryEntry, bool, Task> open, Func<string, Task>? copy, Func<DateTime>? now = null)
    {
        _history = history;
        _connectionExists = connectionExists;
        _currentConnection = currentConnection;
        _dialogs = dialogs;
        _open = open;
        _copy = copy;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public ObservableCollection<HistoryItemViewModel> Items { get; } = [];

    [ObservableProperty] private string _searchText = "";

    [NotifyPropertyChangedFor(nameof(ShowAll), nameof(ShowErrors), nameof(ShowThisConnection))]
    [ObservableProperty] private HistoryFilter _filter = HistoryFilter.All;

    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(RunAgainCommand), nameof(CopySqlCommand), nameof(DeleteCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [ObservableProperty] private HistoryItemViewModel? _selected;

    /// <summary>True while the file is being read for the first time.</summary>
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(EmptyTitle), nameof(EmptyHint))]
    [ObservableProperty] private bool _isLoading;

    /// <summary>Why the last action did not work (shown in the status line of the window); null when fine.</summary>
    [ObservableProperty] private string? _message;

    public bool HasSelection => Selected is not null;

    public bool HasItems => Items.Count > 0;

    public bool IsEmpty => !IsLoading && Items.Count == 0;

    /// <summary>The "This connection" chip works only when a connection is active.</summary>
    public bool CanFilterByConnection => _currentConnection is not null;

    public string ThisConnectionTip => CanFilterByConnection ? "Only the statements run on the active connection" : "Open a script or table tab first: there is no active connection";

    // Chips of a segmented choice: unchecking the current one is refused (the notification puts the check back).
    public bool ShowAll { get => Filter == HistoryFilter.All; set => Choose(value, HistoryFilter.All, nameof(ShowAll)); }
    public bool ShowErrors { get => Filter == HistoryFilter.Errors; set => Choose(value, HistoryFilter.Errors, nameof(ShowErrors)); }
    public bool ShowThisConnection { get => Filter == HistoryFilter.ThisConnection; set => Choose(value, HistoryFilter.ThisConnection, nameof(ShowThisConnection)); }

    private void Choose(bool on, HistoryFilter filter, string property)
    {
        if (on) Filter = filter;
        else OnPropertyChanged(property);
    }

    private bool IsFiltered => !string.IsNullOrWhiteSpace(SearchText) || Filter != HistoryFilter.All;

    public string EmptyTitle => IsFiltered ? "No match" : "Nothing recorded yet";

    public string EmptyHint => IsFiltered
        ? "Change the search or the filter."
        : "The statements you run from a script tab appear here. Recording can be switched off in Tools > Options.";

    /// <summary>"12 statements" or "500 of many": the counter in the header.</summary>
    public string CountText => Items.Count >= ListLimit ? $"Latest {ListLimit:N0}" : Items.Count == 1 ? "1 statement" : $"{Items.Count:N0} statements";

    /// <summary>Raised when the window should close (after Open or Run again).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Reads the history file off the UI thread, then fills the list.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            await Task.Run(() => _history.Count);
        }
        finally
        {
            IsLoading = false;
        }
        Refresh();
    }

    /// <summary>Re-runs the query with the current search and filter. Keeps the selection when the entry is still listed.</summary>
    public void Refresh()
    {
        var keep = Selected?.Id;
        var rows = _history.Query(
            SearchText, Filter == HistoryFilter.ThisConnection ? _currentConnection : null, Filter == HistoryFilter.Errors, null, ListLimit);
        var now = _now();
        Items.Clear();
        foreach (var e in rows) Items.Add(new HistoryItemViewModel(e, now, _connectionExists(e.ConnectionId)));
        Selected = Items.FirstOrDefault(i => i.Id == keep) ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(CountText));
    }

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnFilterChanged(HistoryFilter value) => Refresh();

    private bool HasEntry() => Selected is not null;

    /// <summary>Opens the selected statement in a new script tab.</summary>
    [RelayCommand(CanExecute = nameof(HasEntry))]
    private Task OpenAsync() => OpenCoreAsync(run: false);

    /// <summary>Opens the selected statement in a new script tab and runs it.</summary>
    [RelayCommand(CanExecute = nameof(HasEntry))]
    private Task RunAgainAsync() => OpenCoreAsync(run: true);

    private async Task OpenCoreAsync(bool run)
    {
        if (Selected is not { } item) return;
        try
        {
            await _open(item.Entry, run);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e)
        {
            Message = $"Could not open the statement: {ErrorText.Describe(e)}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasEntry))]
    private async Task CopySqlAsync()
    {
        if (Selected is not { } item || _copy is null) return;
        try
        {
            await _copy(item.Sql);
            Message = "SQL copied.";
        }
        catch (Exception e)
        {
            Message = $"Could not copy: {ErrorText.Describe(e)}";
        }
    }

    /// <summary>Removes the selected entry and selects its neighbour.</summary>
    [RelayCommand(CanExecute = nameof(HasEntry))]
    private void Delete()
    {
        if (Selected is not { } item) return;
        var index = Items.IndexOf(item);
        _history.Delete([item.Id]);
        Items.Remove(item);
        Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>Deletes the whole history after a confirmation.</summary>
    [RelayCommand]
    private async Task ClearAllAsync()
    {
        var count = _history.Count;
        if (count == 0) return;
        var confirm = new ConfirmRequest(
            "Clear history", $"Delete all {count:N0} recorded statements?", "Clear history",
            "This cannot be undone.", DialogTone.Danger);
        if (!await _dialogs.ConfirmAsync(confirm)) return;
        _history.Clear();
        Refresh();
    }
}
