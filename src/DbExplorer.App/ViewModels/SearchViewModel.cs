using System.Collections.ObjectModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.ViewModels;

public enum SearchScope { Scripts, Data }

/// <summary>The text of an open script tab, handed to the search (the unsaved text counts, not the file).</summary>
public sealed record SearchScriptSource(string Key, string DisplayName, string? Path, string Text, bool Unsaved);

/// <summary>What the search overlay needs from the shell. Everything is a delegate so tests can fake it.</summary>
/// <param name="OpenScripts">The open script tabs, read on the UI thread when a search starts.</param>
/// <param name="LibraryRoot">Folder of the script library, or null.</param>
/// <param name="Connections">The configured connections (re-read each time the overlay opens).</param>
/// <param name="Connect">Returns the live session of a connection (connecting it first), or null after reporting why.</param>
/// <param name="OpenScript">Opens the script of a hit at its line.</param>
/// <param name="OpenTable">Opens a table tab with column filters.</param>
public sealed record SearchHooks(
    Func<IReadOnlyList<SearchScriptSource>> OpenScripts,
    Func<string?> LibraryRoot,
    Func<IReadOnlyList<ConnectionConfig>> Connections,
    Func<ConnectionConfig, CancellationToken, Task<Session?>> Connect,
    Func<ScriptMatch, Task> OpenScript,
    Func<Session, TableRef, IReadOnlyDictionary<string, string>, Task> OpenTable);

/// <summary>A line of the result list: a group header (script or table) or a hit.</summary>
public abstract class SearchRow : ObservableObject
{
    public virtual bool IsHeader => false;
}

public sealed partial class SearchHeaderRow(string title, string? detail, string iconKey, bool unsaved) : SearchRow
{
    public override bool IsHeader => true;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public string IconKey { get; } = iconKey;
    public bool IsUnsaved { get; } = unsaved;
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CountText))]
    private int _count;

    public string CountText => Count == 1 ? "1 match" : $"{Count:N0} matches";
}

/// <summary>One hit. <see cref="MatchStart"/> / <see cref="MatchLength"/> mark the part of <see cref="Text"/> drawn in bold.</summary>
public sealed class SearchHitRow : SearchRow
{
    public string? LineLabel { get; init; }
    public string? Badge { get; init; }
    public required string Text { get; init; }
    public int MatchStart { get; init; }
    public int MatchLength { get; init; }
    public ScriptMatch? Script { get; init; }
    public DataMatch? Data { get; init; }
    public bool HasLine => LineLabel is not null;
    public bool HasBadge => Badge is not null;
}

/// <summary>
/// State of the search overlay (Ctrl+Alt+F): two scopes, scripts (open tabs first, then the library) and data (one connection).
/// Results stream into <see cref="Rows"/> grouped by script or table while the work runs off the UI thread: the producer writes
/// to a bounded channel and this class reads it back on the context that started the search, so the list is only touched on the UI
/// thread and a fast producer waits for the list instead of flooding it. At most <see cref="MaxResults"/> hits are kept.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    public const int MaxResults = 2000;

    /// <summary>The key prefix of a script source that is an open tab (the others are file paths).</summary>
    public const string TabKeyPrefix = "tab:";

    private static readonly string CapNotice = $"More results are not shown: only the first {MaxResults:N0} matches are listed. Narrow the search to see the others.";

    /// <summary>A table that takes longer than this is abandoned (and listed) so one huge table cannot hold the search forever.</summary>
    public static readonly TimeSpan TableBudget = TimeSpan.FromMinutes(2);

    private readonly SearchHooks _hooks;
    private readonly StatusModel? _status;
    private CancellationTokenSource? _run;
    private Session? _dataSession;

    public SearchViewModel(SearchHooks hooks, StatusModel? status = null)
    {
        _hooks = hooks;
        _status = status;
        RefreshConnections();
    }

    public ObservableCollection<SearchRow> Rows { get; } = [];
    public ObservableCollection<ConnectionConfig> Connections { get; } = [];
    public IReadOnlyList<DataMatchMode> Modes { get; } = [DataMatchMode.Contains, DataMatchMode.Exact, DataMatchMode.StartsWith];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsScripts), nameof(IsData), nameof(Placeholder), nameof(EmptyHint))]
    private SearchScope _scope;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private bool _wholeWord;
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private DataMatchMode _mode = DataMatchMode.Contains;
    [ObservableProperty] private ConnectionConfig? _selectedConnection;
    [ObservableProperty] private string _schemaFilter = "";
    [ObservableProperty] private string _tableFilter = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSearch))] private bool _isRunning;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private SearchRow? _selectedRow;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasResults), nameof(IsEmptyState))] private int _hitCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmptyState))] private bool _hasSearched;

    public bool IsScripts => Scope == SearchScope.Scripts;
    public bool IsData => Scope == SearchScope.Data;
    public bool HasResults => HitCount > 0;
    public bool CanSearch => !IsRunning;

    /// <summary>The empty state shows when nothing was found or nothing was searched yet, and nothing is running.</summary>
    public bool IsEmptyState => HitCount == 0;

    public string Placeholder => IsScripts ? "Search in scripts…" : "Find a value in the data…";

    /// <summary>What the empty list says.</summary>
    public string EmptyHint =>
        IsRunning ? "Searching…"
        : HasSearched ? (IsScripts ? "No script contains this text." : "No table holds this value.")
        : IsScripts ? "Searches the open scripts (including unsaved text) and every script of your library."
        : "Looks for the value in the columns of every table of the chosen connection.";

    /// <summary>Raised when the overlay must go away.</summary>
    public event Action? CloseRequested;

    public void Close()
    {
        StopSearch();
        CloseRequested?.Invoke();
    }

    /// <summary>Re-reads the connections (the list can change between two openings) and keeps the choice when it still exists.</summary>
    public void RefreshConnections(Guid? preferred = null)
    {
        var current = preferred ?? SelectedConnection?.Id;
        Connections.Clear();
        foreach (var c in _hooks.Connections()) Connections.Add(c);
        SelectedConnection = Connections.FirstOrDefault(c => c.Id == current) ?? Connections.FirstOrDefault();
    }

    partial void OnScopeChanged(SearchScope value) => OnOptionsChanged();
    partial void OnQueryChanged(string value) => OnOptionsChanged();
    partial void OnCaseSensitiveChanged(bool value) => OnOptionsChanged();
    partial void OnWholeWordChanged(bool value) => OnOptionsChanged();
    partial void OnUseRegexChanged(bool value) => OnOptionsChanged();
    partial void OnModeChanged(DataMatchMode value)
    {
        OnPropertyChanged(nameof(ModeIndex));
        OnOptionsChanged();
    }

    /// <summary>The match mode as the index of the combo box (Contains, Is exactly, Starts with).</summary>
    public int ModeIndex
    {
        get => (int)Mode;
        set => Mode = (DataMatchMode)Math.Clamp(value, 0, 2);
    }
    partial void OnSelectedConnectionChanged(ConnectionConfig? value) => OnOptionsChanged();
    partial void OnSchemaFilterChanged(string value) => OnOptionsChanged();
    partial void OnTableFilterChanged(string value) => OnOptionsChanged();
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(EmptyHint));
    partial void OnHasSearchedChanged(bool value) => OnPropertyChanged(nameof(EmptyHint));

    // Editing an option makes the shown results stale: Enter searches again instead of opening a row of the old search.
    private void OnOptionsChanged()
    {
        SelectedRow = null;
        ErrorMessage = IsScripts && UseRegex && Query.Length > 0 ? new ScriptSearchOptions(Query, CaseSensitive, WholeWord, true).Validate() : null;
    }

    [RelayCommand]
    private void SelectScope(SearchScope scope) => Scope = scope;

    // ---- keyboard ----

    public void MoveDown() => Move(1);
    public void MoveUp() => Move(-1);

    private void Move(int direction)
    {
        var hits = Rows.Where(r => !r.IsHeader).ToList();
        if (hits.Count == 0) return;
        var at = SelectedRow is null ? -1 : hits.IndexOf(SelectedRow);
        var next = at < 0 ? (direction > 0 ? 0 : hits.Count - 1) : Math.Clamp(at + direction, 0, hits.Count - 1);
        SelectedRow = hits[next];
    }

    /// <summary>Enter: opens the selected hit, or runs the search when no hit is selected.</summary>
    public Task ConfirmAsync() => SelectedRow is SearchHitRow hit ? OpenAsync(hit) : SearchAsync();

    /// <summary>Opens a hit (script at its line, or the table filtered on the matching value) and closes the overlay.</summary>
    public async Task OpenAsync(SearchHitRow hit)
    {
        try
        {
            if (hit.Script is { } script)
                await _hooks.OpenScript(script);
            else if (hit.Data is { } data && _dataSession is { } session)
                await _hooks.OpenTable(session, data.Table, new Dictionary<string, string> { [data.Column] = "=" + data.Value });
            else
                return;
            CloseRequested?.Invoke();   // the search itself keeps what it found: the next opening shows the same list
        }
        catch (Exception e)
        {
            _status?.Error($"Cannot open the result: {ErrorText.Describe(e)}");
        }
    }


    // ---- searching ----

    [RelayCommand]
    public void StopSearch() => _run?.Cancel();

    /// <summary>Runs the search of the current scope. Never throws: problems end up in <see cref="ErrorText"/> or the status line.</summary>
    [RelayCommand]
    public async Task SearchAsync()
    {
        if (IsRunning) return;
        if (string.IsNullOrWhiteSpace(Query))
        {
            ErrorMessage = null;
            StatusText = "Type something to search for.";
            return;
        }

        using var cts = new CancellationTokenSource();
        _run = cts;
        Rows.Clear();
        HitCount = 0;
        _groupCount = 0;
        SelectedRow = null;
        Notice = null;
        ErrorMessage = null;
        HasSearched = true;
        IsRunning = true;
        try
        {
            if (Scope == SearchScope.Scripts) await SearchScriptsAsync(cts);
            else await SearchDataAsync(cts);
        }
        catch (OperationCanceledException)
        {
            StatusText = HitCount == 0 ? "Search stopped." : $"Search stopped: {Summary()}.";
        }
        catch (Exception e)
        {
            ErrorMessage = ErrorText.Describe(e);
            StatusText = "The search failed.";
        }
        finally
        {
            IsRunning = false;
            _run = null;
            OnPropertyChanged(nameof(EmptyHint));
        }
    }

    private int _groupCount;

    private string Summary() =>
        HitCount == 0 ? "no match"
        : $"{HitCount:N0} {(HitCount == 1 ? "match" : "matches")} in {_groupCount:N0} {(IsScripts ? (_groupCount == 1 ? "script" : "scripts") : (_groupCount == 1 ? "table" : "tables"))}";

    private async Task SearchScriptsAsync(CancellationTokenSource cts)
    {
        var options = new ScriptSearchOptions(Query, CaseSensitive, WholeWord, UseRegex);
        if (options.Validate() is { } problem)
        {
            ErrorMessage = problem;
            StatusText = "";
            return;
        }

        var search = new ScriptSearch(options);
        var tabs = _hooks.OpenScripts();
        var skip = tabs.Where(t => t.Path is not null).Select(t => Path.GetFullPath(t.Path!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = _hooks.LibraryRoot();
        var channel = Channel.CreateBounded<ScriptMatch>(new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var ct = cts.Token;

        var producer = Task.Run(async () =>
        {
            try
            {
                foreach (var tab in tabs)
                    foreach (var m in search.SearchText(tab.Text, tab.Key, tab.DisplayName, tab.Path, tab.Unsaved, ct))
                        await channel.Writer.WriteAsync(m, ct);
                if (root is not null && Directory.Exists(root))
                    foreach (var m in search.SearchFolder(root, skip, ct))
                        await channel.Writer.WriteAsync(m, ct);
                channel.Writer.TryComplete();
            }
            catch (Exception e)
            {
                channel.Writer.TryComplete(e);
            }
        }, CancellationToken.None);

        StatusText = "Searching scripts…";
        SearchHeaderRow? header = null;
        string? source = null;
        try
        {
            await foreach (var m in channel.Reader.ReadAllAsync(CancellationToken.None))
            {
                if (HitCount >= MaxResults)
                {
                    Notice = CapNotice;   // a hit beyond the cap exists: say so
                    cts.Cancel();
                    break;
                }
                if (!string.Equals(source, m.Source, StringComparison.Ordinal))
                {
                    source = m.Source;
                    var isTab = m.Source.StartsWith(TabKeyPrefix, StringComparison.Ordinal);
                    var folder = isTab ? null : Path.GetDirectoryName(m.DisplayName);
                    header = new SearchHeaderRow(isTab ? m.DisplayName : Path.GetFileNameWithoutExtension(m.DisplayName),
                        string.IsNullOrEmpty(folder) ? null : folder, "IconScript", m.Unsaved);
                    Rows.Add(header);
                    _groupCount++;
                }
                header!.Count++;
                Rows.Add(new SearchHitRow
                {
                    LineLabel = m.Line.ToString(), Text = m.LineText, MatchStart = m.Start, MatchLength = m.Length, Script = m,
                });
                HitCount++;
                StatusText = $"Searching scripts… {Summary()}";
            }
        }
        finally
        {
            try { await producer; } catch (Exception) { /* reported through the channel */ }
        }

        var skipped = search.Stats.FilesSkippedTooLarge + search.Stats.FilesUnreadable + search.Stats.FilesBinary;
        var tail = skipped > 0 ? $" ({skipped} {(skipped == 1 ? "file" : "files")} skipped: too large, binary or unreadable)" : "";
        if (search.Stats.LinesTimedOut > 0) tail += $" ({search.Stats.LinesTimedOut} lines gave up: the regular expression is too slow)";
        StatusText = Summary() + tail;
    }

    private async Task SearchDataAsync(CancellationTokenSource cts)
    {
        if (SelectedConnection is not { } config)
        {
            ErrorMessage = "Pick a connection to search.";
            return;
        }

        var ct = cts.Token;
        StatusText = $"Connecting to {config.Name}…";
        var session = await _hooks.Connect(config, ct);
        if (session is null)
        {
            StatusText = "";
            ErrorMessage = $"{config.Name} is not available (see the status bar).";
            return;
        }
        _dataSession = session;

        var options = new DataSearchOptions(Query, Mode, SchemaFilter, TableFilter, TableBudget: TableBudget);
        var channel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
        var progress = new Progress(p => channel.Writer.TryWrite(p));
        DataSearchSummary? summary = null;

        var producer = Task.Run(async () =>
        {
            try
            {
                summary = await DataSearch.RunAsync(session, options, (table, hits) => channel.Writer.TryWrite((table, hits)), progress, ct);
                channel.Writer.TryComplete();
            }
            catch (Exception e)
            {
                channel.Writer.TryComplete(e);
            }
        }, CancellationToken.None);

        var total = 0;
        var done = 0;
        var capped = false;
        try
        {
            await foreach (var message in channel.Reader.ReadAllAsync(CancellationToken.None))
            {
                switch (message)
                {
                    case DataSearchProgress p:
                        total = p.TablesTotal;
                        done = p.TablesDone;
                        StatusText = total == 0 ? "No table to search." : $"Searching table {Math.Min(done + 1, total):N0} of {total:N0}"
                            + (HitCount > 0 ? $" · {Summary()}" : "");
                        break;
                    case (TableRef table, IReadOnlyList<DataMatch> hits):
                        if (capped) break;
                        var header = new SearchHeaderRow(table.Name, table.Schema, "IconTable", false) { Count = hits.Count };
                        Rows.Add(header);
                        _groupCount++;
                        foreach (var hit in hits)
                        {
                            if (HitCount >= MaxResults)
                            {
                                capped = true;
                                Notice = CapNotice;
                                cts.Cancel();
                                break;
                            }
                            Rows.Add(DataRow(hit));
                            HitCount++;
                        }
                        break;
                }
            }
        }
        finally
        {
            try { await producer; } catch (Exception) { /* reported through the channel */ }
        }

        var notes = new List<string>();
        if (summary is { TimedOut.Count: > 0 } s1)
            notes.Add($"{s1.TimedOut.Count} {(s1.TimedOut.Count == 1 ? "table" : "tables")} gave up after {TableBudget.TotalMinutes:0} minutes ({string.Join(", ", s1.TimedOut.Take(3))}{(s1.TimedOut.Count > 3 ? ", …" : "")})");
        if (summary is { Failures.Count: > 0 } s2)
            notes.Add($"{s2.Failures.Count} {(s2.Failures.Count == 1 ? "table" : "tables")} could not be read ({s2.Failures[0].Table}: {s2.Failures[0].Message})");
        var tail = notes.Count > 0 ? " · " + string.Join(" · ", notes) : "";
        StatusText = (capped ? Summary() : $"{Summary()} · {summary?.TablesSearched ?? done:N0} of {summary?.TablesTotal ?? total:N0} tables searched") + tail;
        if (summary?.Cancelled == true && !capped) StatusText = $"Search stopped: {Summary()} · {summary.TablesSearched:N0} of {summary.TablesTotal:N0} tables searched" + tail;
    }

    private static SearchHitRow DataRow(DataMatch hit)
    {
        var text = new System.Text.StringBuilder();
        var start = 0;
        var length = 0;
        foreach (var cell in hit.Preview)
        {
            if (text.Length > 0) text.Append("   ·   ");
            text.Append(cell.Column).Append(": ");
            if (cell.IsMatch)
            {
                start = text.Length;
                length = cell.Text.Length;
            }
            text.Append(cell.Text);
        }
        return new SearchHitRow { Badge = hit.Column, Text = text.ToString(), MatchStart = start, MatchLength = length, Data = hit };
    }

    private sealed class Progress(Action<DataSearchProgress> report) : IProgress<DataSearchProgress>
    {
        public void Report(DataSearchProgress value) => report(value);
    }
}
