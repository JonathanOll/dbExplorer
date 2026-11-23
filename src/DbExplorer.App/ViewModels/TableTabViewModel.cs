using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.ViewModels;

/// <summary>What a table tab needs from the shell.</summary>
/// <param name="ResolveSession">Returns the live session of the tab's connection (reopening it when it was closed),
/// or null when the connection no longer exists. Used by Reload after Disconnect or a config edit.</param>
/// <param name="OpenTable">Opens a table in a new tab, optionally with column filters (<c>column -&gt; filter text</c>).</param>
/// <param name="Dialogs">Prompts of the editing flow (SQL preview, save/discard, virtual key). Without it nothing can be saved.</param>
/// <param name="GetConfig">The live application config (virtual keys live in it). Without it a private empty one is used.</param>
/// <param name="SetVirtualKey">Stores a virtual key in the config and saves it; false when it could not be saved (kept for this session).</param>
public sealed record TableTabHooks(
    Func<CancellationToken, Task<Session?>> ResolveSession,
    Func<Session, TableRef, IReadOnlyDictionary<string, string>?, Task> OpenTable,
    IEditDialogs? Dialogs = null,
    Func<AppConfig>? GetConfig = null,
    Func<Guid, TableRef, string[], bool>? SetVirtualKey = null);

/// <summary>
/// A table (or view) opened in its own tab: one page of rows at a time, with per-column filters, a raw WHERE,
/// server-side sort, on-demand count and foreign-key navigation. All queries run server-side through
/// <see cref="TableDataLoader"/>; a failing query leaves the previous rows in place and shows <see cref="ErrorMessage"/>.
/// </summary>
public sealed partial class TableTabViewModel : TabViewModel, ITableGridActions
{
    public const int PageSize = 200;
    public const string ConnectionClosedMessage = "Connection closed — reopen the table";

    /// <summary>The query behind the rows on screen: what paging, refresh and count reuse.</summary>
    private sealed record AppliedQuery(IReadOnlyList<ColumnFilter> Filters, string? RawWhere, SortSpec? Sort);

    private readonly TableHookState _hooks;
    private Session _session;
    private AppliedQuery? _applied;
    private CancellationTokenSource? _loadCts;
    private int _loadVersion;

    [ObservableProperty] private ObservableCollection<RowViewModel> _rows = new();
    [ObservableProperty] private IReadOnlyList<ColumnInfo> _columns = [];
    [ObservableProperty] private IReadOnlyList<ResultColumn> _gridColumns = [];
    [ObservableProperty] private TableInfo? _info;
    [ObservableProperty] private StructureViewModel _structure = StructureViewModel.Empty;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _needsReload;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private int _pageIndex;
    [ObservableProperty] private bool _hasNextPage;
    [ObservableProperty] private long? _totalCount;
    [ObservableProperty] private string? _rawWhere;
    [ObservableProperty] private SortSpec? _sort;
    [ObservableProperty] private string _elapsedText = "";

    public TableTabViewModel(Session session, TableRef table, TableTabHooks? hooks = null) : base(session.Config)
    {
        _session = session;
        _hooks = new TableHookState(hooks);
        Table = table;
        UseSession(session);
    }

    public TableRef Table { get; }

    public override string Title => $"{Connection.Name} · {Table.Name}";

    public bool IsView => Info?.IsView ?? false;

    /// <summary>
    /// Filter-box text per column name (see <see cref="FilterParser"/> for the syntax). Not observable: after changing
    /// it in code call <see cref="NotifyFiltersChanged"/> so the filter row repaints. Nothing is queried until
    /// <see cref="ApplyFiltersAsync"/> runs.
    /// </summary>
    public Dictionary<string, string> ColumnFilters { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised (with the column index) when something asks the view to scroll to and select a column.</summary>
    public event Action<int>? FocusColumnRequested;

    /// <summary>Zero-based index of the column in <see cref="Columns"/> (and in every row's values), or null.</summary>
    public int? FindColumn(string name)
    {
        var columns = Columns;
        for (var i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    /// <summary>Asks the view to scroll to and select the column (used by the column jump). False when there is none.</summary>
    public bool FocusColumn(string name)
    {
        if (FindColumn(name) is not { } index) return false;
        FocusColumnRequested?.Invoke(index);
        return true;
    }

    /// <summary>Asks the view to scroll to the column at <paramref name="index"/>.</summary>
    public void RequestFocusColumn(int index)
    {
        if (index >= 0 && index < Columns.Count) FocusColumnRequested?.Invoke(index);
    }

    public string RangeText
    {
        get
        {
            if (Rows.Count == 0) return "No rows";
            var from = PageIndex * PageSize + 1;
            var text = $"Rows {from:N0}–{from + Rows.Count - 1:N0}";
            return TotalCount is { } total ? $"{text} of {total:N0}" : text;
        }
    }

    // ---- loading and paging ----

    /// <summary>
    /// Runs the query described by the filter boxes, the raw WHERE and the current sort, from the first page.
    /// Also the initial load. Never throws: failures set <see cref="ErrorMessage"/> and keep the previous rows.
    /// </summary>
    public async Task LoadAsync()
    {
        if (!await ConfirmLeaveAsync()) return;
        await RunAsync(CurrentInputs(Sort), 0);
    }

    /// <summary>Same as <see cref="LoadAsync"/>: explicit apply (Enter in a filter box, the apply button).</summary>
    [RelayCommand]
    public Task ApplyFiltersAsync() => LoadAsync();

    /// <summary>Re-runs the query behind the rows on screen, on the same page (the filter boxes are not re-read).</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_applied is not { } q)
        {
            await LoadAsync();
            return;
        }
        if (!await ConfirmLeaveAsync()) return;
        await RunAsync(q, PageIndex);
    }

    [RelayCommand]
    public async Task NextPage()
    {
        if (!HasNextPage || _applied is not { } q || !await ConfirmLeaveAsync()) return;
        await RunAsync(q, PageIndex + 1);
    }

    [RelayCommand]
    public async Task PrevPage()
    {
        if (PageIndex <= 0 || _applied is not { } q || !await ConfirmLeaveAsync()) return;
        await RunAsync(q, PageIndex - 1);
    }

    /// <summary>Sort on the column: none → ascending → descending → none. Switching column starts ascending.</summary>
    [RelayCommand]
    public async Task ToggleSortAsync(string column)
    {
        if (FindColumn(column) is not { } index || !await ConfirmLeaveAsync()) return;
        var name = Columns[index].Name;
        SortSpec? next = Sort switch
        {
            { } s when string.Equals(s.Column, name, StringComparison.OrdinalIgnoreCase) =>
                s.Descending ? null : new SortSpec(name, true),
            _ => new SortSpec(name, false),
        };
        var basis = _applied ?? CurrentInputs(null);
        await RunAsync(basis with { Sort = next }, 0);
    }

    /// <summary>
    /// Total rows matching the query on screen. Runs only when asked (never on load); returns -1 when it failed
    /// (the reason is in <see cref="ErrorMessage"/>).
    /// </summary>
    [RelayCommand]
    public async Task<long> CountAsync()
    {
        if (Info is not { } info)
        {
            SetError(TableMissingMessage());
            return -1;
        }

        var applied = _applied ?? CurrentInputs(Sort);
        var session = _session;
        try
        {
            var total = await TableDataLoader.CountAsync(session, ToQuery(info, applied, 0), CancellationToken.None);
            if (ReferenceEquals(_session, session) && (_applied is null || ReferenceEquals(_applied, applied)))
            {
                TotalCount = total;
            }
            return total;
        }
        catch (ObjectDisposedException)
        {
            SetClosed();
            return -1;
        }
        catch (Exception e)
        {
            SetError(ErrorText.Describe(e));
            return -1;
        }
    }

    // ---- filters ----

    public string GetFilter(string column) => ColumnFilters.GetValueOrDefault(column) ?? "";

    public void SetFilter(string column, string text)
    {
        if (string.IsNullOrEmpty(text)) ColumnFilters.Remove(column);
        else ColumnFilters[column] = text;
    }

    /// <summary>Repaints the filter row after <see cref="ColumnFilters"/> was changed in code.</summary>
    public void NotifyFiltersChanged() => OnPropertyChanged(nameof(ColumnFilters));

    /// <summary>
    /// Filters the column on the value of the cell (<c>=value</c>, or <c>null</c> for a NULL cell) and applies, keeping
    /// the other filters. Binary cells cannot be filtered on.
    /// </summary>
    public async Task FilterByCellAsync(int rowIndex, string column)
    {
        if (rowIndex < 0 || rowIndex >= Rows.Count || FindColumn(column) is not { } index) return;
        if (!await ConfirmLeaveAsync()) return;   // before the filter box changes: a cancelled apply must leave it alone
        string text;
        try
        {
            text = CellFormatter.FilterText(Rows[rowIndex].Values[index], Columns[index].Name);
        }
        catch (FilterException e)
        {
            SetError(e.Message);
            return;
        }

        ColumnFilters[Columns[index].Name] = text;
        NotifyFiltersChanged();
        await ApplyFiltersAsync();
    }

    // ---- foreign keys ----

    public bool IsForeignKeyColumn(string column) => ForeignKeyFor(column) is not null;

    public ForeignKeyInfo? ForeignKeyFor(string column) =>
        Info?.ForeignKeys.FirstOrDefault(f => f.Columns.Contains(column, StringComparer.OrdinalIgnoreCase));

    public Task FollowForeignKeyAsync(int rowIndex, string column) =>
        ForeignKeyFor(column) is { } fk ? FollowForeignKeyAsync(rowIndex, fk) : Task.CompletedTask;

    /// <summary>
    /// Opens a new tab on the referenced table filtered on the referenced columns (<c>=value</c> for each key part of
    /// this row). Does nothing when any key part is NULL (no referenced row exists).
    /// </summary>
    public async Task FollowForeignKeyAsync(int rowIndex, ForeignKeyInfo fk)
    {
        if (rowIndex < 0 || rowIndex >= Rows.Count || fk.Columns.Count != fk.RefColumns.Count) return;
        var row = Rows[rowIndex];
        var filters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var i = 0; i < fk.Columns.Count; i++)
            {
                if (FindColumn(fk.Columns[i]) is not { } index || row.Values[index] is not { } value) return;
                filters[fk.RefColumns[i]] = CellFormatter.FilterText(value, fk.Columns[i]);
            }
            await OpenTableAsync(fk.RefTable, filters);
        }
        catch (Exception e)
        {
            SetError(ErrorText.Describe(e));
        }
    }

    // ---- reload after the connection was closed ----

    /// <summary>
    /// Re-resolves the live session of this connection (reopening it if needed), refreshes the schema info and reruns
    /// the query on screen. The way out of "Connection closed".
    /// With pending edits the user is asked FIRST and nothing changes until they answered: Cancel leaves the tab exactly
    /// as it was; Discard drops the edits and reloads; Save reconnects (only when the table's columns are the ones the
    /// pending rows were loaded with, otherwise the save is refused and the edits stay) and saves through the preview.
    /// </summary>
    [RelayCommand]
    public async Task ReloadAsync()
    {
        if (IsSaving) return;   // a save is in flight: asking to discard would continue into a reload underneath it
        if (HasPendingEdits)
        {
            switch (await AskPendingAsync())
            {
                case SaveChoice.Discard:
                    Revert();
                    break;
                case SaveChoice.Save:
                    if (!await ReconnectKeepingEditsAsync()) return;
                    await ReviewAndSaveAsync();   // reloads the page itself on success
                    return;
                default:
                    return;   // Cancel: nothing changes at all
            }
        }

        if (await ResolveLiveSessionAsync() is not { } live) return;
        UseSession(live);
        if (_applied is { } q) await RunAsync(q, PageIndex);
        else await RunAsync(CurrentInputs(Sort), 0);
    }

    /// <summary>The live session of the connection, or null after setting the error state (and the Reload button).</summary>
    private async Task<Session?> ResolveLiveSessionAsync()
    {
        IsLoading = true;
        try
        {
            var live = await _hooks.ResolveSessionAsync();
            if (live is null)
            {
                SetError("The connection no longer exists — close this tab.");
                NeedsReload = true;
            }
            return live;
        }
        catch (Exception e)
        {
            SetError("Could not reconnect: " + ErrorText.Describe(e));
            NeedsReload = true;
            return null;
        }
        finally
        {
            IsLoading = false;
        }
    }
    [RelayCommand]
    public void DismissError() => ErrorMessage = null;

    // ---- internals ----

    private AppliedQuery CurrentInputs(SortSpec? sort)
    {
        var filters = ColumnFilters
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => new ColumnFilter(kv.Key, kv.Value))
            .ToList();
        return new AppliedQuery(filters, string.IsNullOrWhiteSpace(RawWhere) ? null : RawWhere, sort);
    }

    private static TableQuery ToQuery(TableInfo info, AppliedQuery q, int page) =>
        new(info, q.Filters, q.RawWhere, q.Sort is null ? [] : [q.Sort], page * PageSize, PageSize);

    /// <summary>Runs the query and replaces the rows. True when the page was loaded. Never asks about pending edits: callers do.</summary>
    private async Task<bool> RunAsync(AppliedQuery query, int page)
    {
        if (Info is not { } info)
        {
            SetError(TableMissingMessage());
            return false;
        }

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var version = ++_loadVersion;
        var session = _session;
        IsLoading = true;
        try
        {
            var result = await TableDataLoader.LoadPageAsync(session, ToQuery(info, query, page), PageSize, cts.Token);
            if (version != _loadVersion) return false;   // a newer request replaced this one

            ApplyReaderColumns(result.ColumnNames);
            _rowColumns = Columns.Select(c => c.Name).ToArray();
            var rows = new ObservableCollection<RowViewModel>();
            foreach (var values in result.Rows) rows.Add(new RowViewModel(values));

            var queryChanged = _applied is null || !SameQuery(_applied, query);
            _applied = query;
            Sort = query.Sort;
            Rows = rows;
            ClearPending();
            PageIndex = page;
            HasNextPage = result.HasMore;
            if (queryChanged) TotalCount = null;
            ErrorMessage = null;
            NeedsReload = false;
            ElapsedText = $"{result.Duration.TotalMilliseconds:N0} ms";
            OnPropertyChanged(nameof(RangeText));
            return true;
        }
        catch (OperationCanceledException) when (version != _loadVersion || cts.IsCancellationRequested)
        {
            // superseded by a newer request
        }
        catch (ObjectDisposedException)
        {
            if (version == _loadVersion) SetClosed();
        }
        catch (Exception e)
        {
            if (version == _loadVersion) SetError(ErrorText.Describe(e));
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
        return false;
    }

    private static bool SameQuery(AppliedQuery a, AppliedQuery b) =>
        a.RawWhere == b.RawWhere && Equals(a.Sort, b.Sort) && a.Filters.SequenceEqual(b.Filters);

    partial void OnTotalCountChanged(long? value) => OnPropertyChanged(nameof(RangeText));

    partial void OnPageIndexChanged(int value) => OnPropertyChanged(nameof(RangeText));

    partial void OnInfoChanged(TableInfo? value)
    {
        OnPropertyChanged(nameof(IsView));
        OnPropertyChanged(nameof(IconKey));
        OnPropertyChanged(nameof(CanDefineKey));
    }

    /// <summary>Takes <paramref name="session"/> as the live one and re-reads this table's schema info from it.</summary>
    private void UseSession(Session session)
    {
        _session = session;
        var info = session.Schema.FirstOrDefault(t => t.Ref == Table)
                   ?? session.Schema.FirstOrDefault(t => string.Equals(t.Ref.Name, Table.Name, StringComparison.OrdinalIgnoreCase)
                                                         && string.Equals(t.Ref.Schema, Table.Schema, StringComparison.OrdinalIgnoreCase));
        Info = info;
        Columns = info is null ? [] : info.Columns.OrderBy(c => c.Ordinal).ToList();
        GridColumns = BuildGridColumns();
        Structure = info is null ? StructureViewModel.Empty : new StructureViewModel(info, t => OpenTableAsync(t, null));
        RefreshEditState();
    }

    /// <summary>
    /// The result's column names are the truth for the row layout. When the schema in memory is stale (a column was
    /// added or dropped meanwhile) the column list is rebuilt from the reader instead of misaligning cells.
    /// </summary>
    private void ApplyReaderColumns(IReadOnlyList<string> names)
    {
        var current = Columns;
        if (names.Count == current.Count && names.Select((n, i) => string.Equals(n, current[i].Name, StringComparison.OrdinalIgnoreCase)).All(x => x))
            return;

        Columns = names.Select((name, i) =>
            current.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is { } known
                ? known with { Ordinal = i }
                : new ColumnInfo(name, "", typeof(object), true, false, i)).ToList();
        GridColumns = BuildGridColumns();
    }

    private IReadOnlyList<ResultColumn> BuildGridColumns() =>
        Columns.Select(c => new ResultColumn(
            c.Name, c.DataType, c.ClrType, c.IsPrimaryKey,
            ForeignKeyFor(c.Name) is { } fk ? StructureViewModel.Describe(fk) : null)).ToList();

    private Task OpenTableAsync(TableRef table, IReadOnlyDictionary<string, string>? filters) =>
        _hooks.OpenTableAsync(_session, table, filters);

    private string TableMissingMessage() =>
        $"Table '{Table}' was not found in the connection's schema. Refresh the schema and reopen it.";

    private void SetError(string message) => ErrorMessage = message;

    private void SetClosed()
    {
        ErrorMessage = ConnectionClosedMessage;
        NeedsReload = true;
    }

    /// <summary>Null-safe wrapper over the optional hooks.</summary>
    private sealed class TableHookState(TableTabHooks? hooks)
    {
        public Task<Session?> ResolveSessionAsync() =>
            hooks is null ? Task.FromResult<Session?>(null) : hooks.ResolveSession(CancellationToken.None);

        public Task OpenTableAsync(Session session, TableRef table, IReadOnlyDictionary<string, string>? filters) =>
            hooks is null ? Task.CompletedTask : hooks.OpenTable(session, table, filters);

        public IEditDialogs? Dialogs => hooks?.Dialogs;

        public Func<Guid, TableRef, string[], bool>? SetVirtualKey => hooks?.SetVirtualKey;

        public AppConfig? GetConfig() => hooks?.GetConfig?.Invoke();
    }
}
