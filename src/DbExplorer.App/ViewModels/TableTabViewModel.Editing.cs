using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;
using DbExplorer.Core.Sessions;
using DeleteRowEdit = DbExplorer.Core.Querying.DeleteRow;

namespace DbExplorer.App.ViewModels;

/// <summary>Editing surface of a table tab, used by the grid (see <c>ResultGrid.Editing</c>).</summary>
public interface ITableEditActions : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>Loaded rows can be changed or deleted (a key identifies them).</summary>
    bool CanEdit { get; }

    /// <summary>New rows can be added and filled in (needs no key; false for views and read-only connections).</summary>
    bool CanInsert { get; }

    /// <summary>Stages a new value (<c>null</c> = NULL, text is validated against the column type). False when refused; <see cref="CellError"/> says why.</summary>
    bool SetCell(int row, int col, object? value);

    /// <summary>Stages NULL in every cell given; those refused are skipped and <see cref="CellError"/> keeps the first reason.</summary>
    void SetNull(IReadOnlyList<(int Row, int Column)> cells);

    string? CellError { get; }

    void DismissCellError();

    int AddRow();

    void DeleteRow(int row);

    void UndeleteRow(int row);

    /// <summary>Raised after the state of a row changed (cell staged, deleted, reverted) so the grid repaints it.</summary>
    event Action<RowViewModel>? RowChanged;
}

/// <summary>
/// Staged editing. Nothing is written until <see cref="SaveAsync"/>: cells, new rows and deletions are only marked on
/// the <see cref="RowViewModel"/>s (per-row tracking, no per-cell objects). SQL is built exclusively by
/// <see cref="EditPlanner.Plan"/>, which does not guard inserts or read-only: every path here checks <see cref="CanEdit"/> itself.
/// </summary>
public sealed partial class TableTabViewModel : ITableEditActions
{
    private readonly HashSet<RowViewModel> _pending = [];
    private AppConfig? _fallbackConfig;
    private IReadOnlyList<string>? _keyColumns;

    /// <summary>Column names, in order, of the rows on screen: pending edits are index-based and only valid against these.</summary>
    private string[]? _rowColumns;

    [ObservableProperty] private string? _cellError;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private bool _isSaving;

    public event Action<RowViewModel>? RowChanged;

    /// <summary>
    /// True when rows of this table can be edited: a key is known (primary key or virtual key), it is a table and the
    /// connection is not read-only. Re-evaluated whenever the session, schema or virtual key changes and again by every edit.
    /// </summary>
    public bool CanEdit => _keyColumns is not null;

    /// <summary>
    /// True when new rows can be added: the object is a table and the connection is not read-only. An INSERT needs no key,
    /// so a table without primary key accepts new rows (UPDATE/DELETE of loaded rows stay gated on <see cref="CanEdit"/>).
    /// </summary>
    public bool CanInsert => Info is { IsView: false } && !_session.Config.ReadOnly;

    /// <summary>Tooltip of "+ Row": what it does, or why it is disabled (shown on the disabled button too).</summary>
    public string AddRowTip => CanInsert ? "Add an empty row (inserted on save)" : InsertBlockedReason();

    /// <summary>Tooltip of "Delete row": what it does, or why it is disabled.</summary>
    public string DeleteRowTip => CanEdit ? "Mark the selected rows for deletion" : EditBlockedReason();

    private string InsertBlockedReason() =>
        IsView ? $"{Table} is a view: rows cannot be added."
        : _session.Config.ReadOnly ? "This connection is read-only: editing is disabled."
        : $"The schema of {Table} is not loaded: rows cannot be added.";

    private string EditBlockedReason() =>
        IsView ? $"{Table} is a view and cannot be edited."
        : _session.Config.ReadOnly ? "This connection is read-only: editing is disabled."
        : $"{Table} has no primary key: define a key first (Define key…) to change or delete existing rows.";

    /// <summary>The columns identifying a row for UPDATE/DELETE, or null when the table cannot be edited.</summary>
    public IReadOnlyList<string>? KeyColumns => _keyColumns;

    public bool IsReadOnlyConnection => _session.Config.ReadOnly;

    /// <summary>The table is a regular table without a primary key: the user may define a virtual key.</summary>
    public bool CanDefineKey =>
        Info is { IsView: false } info && !_session.Config.ReadOnly && !info.Columns.Any(c => c.IsPrimaryKey);

    /// <summary>Short reason for the read-only badge, or null when editing is possible.</summary>
    public string? EditBadge =>
        _session.Config.ReadOnly ? "read-only"
        : IsView ? "view (read-only)"
        : Info is null ? null
        : _keyColumns is null ? "no primary key: insert only"
        : null;

    public override string? Badge => EditBadge;

    public bool HasPendingEdits => _pending.Count > 0;

    public override bool HasUnsavedWork => HasPendingEdits;

    public override string IconKey => IsView ? "IconView" : "IconTable";

    public string PendingText => _pending.Count switch
    {
        0 => "",
        1 => "1 pending change",
        var n => $"{n} pending changes",
    };

    private AppConfig Config => _hooks.GetConfig() ?? (_fallbackConfig ??= AppConfig.Empty());

    /// <summary>Recomputes <see cref="CanEdit"/> and friends from the current session config, schema and virtual keys.</summary>
    private void RefreshEditState()
    {
        _keyColumns = Info is null ? null : KeyResolver.Resolve(Info, _session.Config, Config);
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanInsert));
        OnPropertyChanged(nameof(AddRowTip));
        OnPropertyChanged(nameof(DeleteRowTip));
        OnPropertyChanged(nameof(KeyColumns));
        OnPropertyChanged(nameof(IsReadOnlyConnection));
        OnPropertyChanged(nameof(CanDefineKey));
        OnPropertyChanged(nameof(EditBadge));
        OnPropertyChanged(nameof(EditBadgeTip));
        OnPropertyChanged(nameof(Badge));
    }

    /// <summary>Fresh check used by every path that changes or deletes LOADED rows; sets <paramref name="why"/> when not allowed right now.</summary>
    private bool CanEditNow(out string why)
    {
        RefreshEditState();
        why = "";
        if (IsView || _session.Config.ReadOnly || _keyColumns is null) why = EditBlockedReason();
        else if (IsLoading || IsSaving) why = IsSaving ? "A save is running." : "Wait for the table to finish loading.";
        return why.Length == 0;
    }

    /// <summary>Same for adding rows and for changing or dropping NEW rows: no key needed.</summary>
    private bool CanInsertNow(out string why)
    {
        RefreshEditState();
        why = "";
        if (!CanInsert) why = InsertBlockedReason();
        else if (IsLoading || IsSaving) why = IsSaving ? "A save is running." : "Wait for the table to finish loading.";
        return why.Length == 0;
    }

    /// <summary>
    /// Save gate: with a key everything pending can be written; without one only new rows can be pending (loaded rows are
    /// refused at staging), so inserting is enough.
    /// </summary>
    private bool CanSaveNow(out string why) => _keyColumns is null ? CanInsertNow(out why) : CanEditNow(out why);

    // ---- staging ----

    /// <summary>
    /// Stages <paramref name="value"/> in a cell. <c>null</c> means NULL (the explicit "Set NULL" gesture); a string is
    /// text typed by the user: it is checked against the column type (the same check Save applies) and an invalid text is
    /// refused with <see cref="CellError"/>, nothing is staged. Empty text is an empty string for text columns and is
    /// refused for other types (clear those with NULL). Typing the loaded value back un-dirties the cell.
    /// </summary>
    public bool SetCell(int row, int col, object? value)
    {
        if (row < 0 || row >= Rows.Count || col < 0 || col >= Columns.Count) return false;
        var r = Rows[row];
        if (!(r.IsNew ? CanInsertNow(out var why) : CanEditNow(out why))) return Refuse(why);
        if (r.IsDeleted) return Refuse("This row is marked for deletion: restore it before editing.");
        var column = Columns[col];

        var stored = value;
        if (value is null)
        {
            if (!column.Nullable && !r.IsNew) return Refuse($"Column '{column.Name}' does not allow NULL.");
        }
        else if (value is string text)
        {
            if (!TryValidate(column, text, out var error)) return Refuse(error);
            if (!r.IsNew && SameAsLoaded(r.Original[col], text)) stored = r.Original[col];
        }

        CellError = null;
        Notice = null;
        r.Values[col] = stored;
        UpdateRowState(r);
        return true;
    }

    public void SetNull(IReadOnlyList<(int Row, int Column)> cells)
    {
        string? refused = null;
        foreach (var (row, column) in cells)
            if (!SetCell(row, column, null)) refused ??= CellError;
        // a later cell that was accepted cleared the reason of an earlier refusal
        if (refused is not null) CellError = refused;
    }

    /// <summary>Appends an empty new row to the page (INSERT on save; cells left NULL are omitted so column defaults apply). Returns its index, -1 when refused.</summary>
    public int AddRow()
    {
        if (!CanInsertNow(out var why))
        {
            Refuse(why);
            return -1;
        }
        var row = new RowViewModel(new object?[Columns.Count], isNew: true);
        Rows.Add(row);
        Notice = null;
        CellError = null;
        _pending.Add(row);
        NotifyPending();
        RowChanged?.Invoke(row);
        return Rows.Count - 1;
    }

    /// <summary>Marks an existing row for deletion; a new, unsaved row is simply dropped.</summary>
    public void DeleteRow(int row)
    {
        if (row < 0 || row >= Rows.Count) return;
        var r = Rows[row];
        if (!(r.IsNew ? CanInsertNow(out var why) : CanEditNow(out why)))
        {
            Refuse(why);
            return;
        }
        Notice = null;
        if (r.IsNew)
        {
            Rows.RemoveAt(row);
            _pending.Remove(r);
            NotifyPending();
            return;
        }
        r.IsDeleted = true;
        UpdateRowState(r);
    }

    public void UndeleteRow(int row)
    {
        if (!CanEditNow(out var why))
        {
            Refuse(why);
            return;
        }
        if (row < 0 || row >= Rows.Count || !Rows[row].IsDeleted) return;
        Rows[row].IsDeleted = false;
        UpdateRowState(Rows[row]);
    }

    /// <summary>Drops every pending edit: cells go back to the loaded values, deletions are undone, new rows removed.</summary>
    [RelayCommand]
    public void Revert()
    {
        if (IsSaving) return;
        var rows = _pending.ToList();
        _pending.Clear();
        foreach (var r in rows)
        {
            if (r.IsNew) Rows.Remove(r);
            else r.Revert();
            RowChanged?.Invoke(r);
        }
        CellError = null;
        NotifyPending();
    }

    [RelayCommand]
    public void DismissNotice() => Notice = null;

    [RelayCommand]
    public void DismissCellError() => CellError = null;

    private bool Refuse(string message)
    {
        CellError = message;
        return false;
    }

    private void UpdateRowState(RowViewModel r)
    {
        if (r.HasChanges) _pending.Add(r);
        else _pending.Remove(r);
        NotifyPending();
        RowChanged?.Invoke(r);
    }

    /// <summary>Forgets the pending set: the rows were replaced or the changes were saved.</summary>
    private void ClearPending()
    {
        if (_pending.Count == 0) return;
        _pending.Clear();
        NotifyPending();
    }

    private void NotifyPending()
    {
        OnPropertyChanged(nameof(HasPendingEdits));
        OnPropertyChanged(nameof(HasUnsavedWork));
        OnPropertyChanged(nameof(PendingText));
        NotifyPendingBreakdown();
    }

    /// <summary>
    /// The text to show when a cell goes into edit mode: the complete value (the grid itself shows a shortened form).
    /// NULL is empty text.
    /// </summary>
    public static string EditText(object? value) => value switch
    {
        null => "",
        string s => s,
        byte[] b => "0x" + Convert.ToHexString(b),
        _ => CellFormatter.Format(value).Text,
    };

    private static bool SameAsLoaded(object? original, string text) =>
        original is not null and not byte[] && EditText(original) == text;

    /// <summary>
    /// Checks the text with the planner itself (planning an INSERT of this one value needs no key), so the inline
    /// check is exactly what Save will accept.
    /// </summary>
    private bool TryValidate(ColumnInfo column, string text, out string error)
    {
        error = "";
        try
        {
            EditPlanner.Plan(_session.Driver, Info!, [], [new InsertRow(new Dictionary<string, object?> { [column.Name] = text })]);
            return true;
        }
        catch (InvalidEditException e)
        {
            error = text.Length == 0
                ? $"Column '{column.Name}' needs a {(Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType).Name} value: empty text is not valid here. Use Set NULL (Ctrl+0) to clear the cell."
                : e.Message;
            return false;
        }
    }

    // ---- preview and save ----

    /// <summary>
    /// The statements Save would run, in order (deletes, updates, inserts), built by <see cref="EditPlanner"/>. Empty when
    /// nothing is pending or editing is not allowed. Display <c>PreviewSql</c> only: it is never executed.
    /// </summary>
    public IReadOnlyList<PlannedStatement> BuildPreview() => CanSaveNow(out _) ? BuildPlan() : [];

    private IReadOnlyList<PlannedStatement> BuildPlan()
    {
        if (Info is not { } info || _pending.Count == 0) return [];
        if (!RowsMatchColumns(Columns.Select(c => c.Name)))
            throw new InvalidOperationException(
                "The table's columns changed since these rows were loaded, so the pending edits cannot be saved safely. Discard them and reload.");

        // Without a key only new rows can be written: a pending change to a loaded row cannot be located again.
        var key = _keyColumns ?? [];
        if (_keyColumns is null && _pending.Any(r => !r.IsNew))
            throw new InvalidOperationException($"{Table} has no key: changes to existing rows cannot be saved. Discard them (Revert) or define a key.");

        var keyIndexes = key.Select(k => (Name: k, Index: FindColumn(k)
            ?? throw new InvalidOperationException($"Key column '{k}' is not among the loaded columns; reload the table."))).ToList();

        var deletes = new List<RowEdit>();
        var updates = new List<RowEdit>();
        var inserts = new List<RowEdit>();
        foreach (var row in Rows)
        {
            if (!_pending.Contains(row)) continue;
            if (row.IsNew)
            {
                if (row.IsDeleted) continue;
                var values = new Dictionary<string, object?>();
                for (var i = 0; i < Columns.Count; i++)
                    if (row.Values[i] is not null) values[Columns[i].Name] = row.Values[i];
                inserts.Add(new InsertRow(values));
                continue;
            }

            var rowKey = keyIndexes.ToDictionary(k => k.Name, k => row.Original[k.Index]);
            if (row.IsDeleted)
            {
                deletes.Add(new DeleteRowEdit(rowKey));
                continue;
            }
            var changes = new Dictionary<string, object?>();
            for (var i = 0; i < Columns.Count; i++)
                if (row.IsDirty(i)) changes[Columns[i].Name] = row.Values[i];
            if (changes.Count > 0) updates.Add(new UpdateRow(rowKey, changes));
        }
        return EditPlanner.Plan(_session.Driver, info, key, [.. deletes, .. updates, .. inserts]);
    }

    /// <summary>
    /// Ctrl+S: shows the SQL that would run (display only) and saves when the user chooses Apply. Never throws.
    /// </summary>
    [RelayCommand]
    public async Task ReviewAndSaveAsync()
    {
        if (IsSaving) return;
        if (!HasPendingEdits)
        {
            Notice = "No pending changes.";
            return;
        }
        if (!CanSaveNow(out var why))
        {
            SetError(why);
            return;
        }
        if (_hooks.Dialogs is not { } dialogs)
        {
            SetError("The SQL preview is not available here, so nothing was saved.");
            return;
        }

        IReadOnlyList<PlannedStatement> plan;
        try
        {
            plan = BuildPlan();
        }
        catch (Exception e)
        {
            SetError("Cannot build the changes: " + ErrorText.Describe(e));
            return;
        }
        if (plan.Count == 0) return;

        if (!await dialogs.ShowSqlPreviewAsync(plan.Select(p => p.PreviewSql).ToList(), Describe(_session.Config))) return;
        await SaveAsync();
    }

    private readonly ProductionApproval _productionApproval = new();

    /// <summary>
    /// Applies the pending edits in ONE transaction (see <see cref="EditApplier"/>) and reloads the page. Any failure
    /// leaves the database untouched and the pending edits in place so the user can retry. Never throws.
    /// </summary>
    public async Task SaveAsync()
    {
        if (IsSaving) return;
        if (!HasPendingEdits) return;
        if (!CanSaveNow(out var why))
        {
            SetError(why);
            return;
        }

        IReadOnlyList<PlannedStatement> plan;
        try
        {
            plan = BuildPlan();
        }
        catch (Exception e)
        {
            SetError("Cannot build the changes: " + ErrorText.Describe(e));
            return;
        }
        if (plan.Count == 0) return;

        var inserted = _pending.Count(r => r.IsNew && !r.IsDeleted);
        IsSaving = true;   // set before the confirmation: a second save must not start behind it
        using var cts = new CancellationTokenSource();
        try
        {
            if (!await _hooks.Guard.AllowAsync([_session.Config],
                    plan.Count == 1 ? "Apply 1 change to " + Table : $"Apply {plan.Count} changes to {Table}",
                    plan.Select(p => p.PreviewSql).ToList(), _productionApproval))
            {
                Notice = "Nothing was saved: the write on production was not confirmed. Your edits are kept.";
                return;
            }
            // Off the UI thread: SQLite runs synchronously, and a lock wait or a slow trigger would freeze the window.
            // No timeout: the save ends, or the user cancels it from the status bar.
            var session = _session;
            var ct = cts.Token;
            using (_hooks.Busy(plan.Count == 1 ? $"Saving 1 change to {Table.Name}…" : $"Saving {plan.Count} changes to {Table.Name}…", cts.Cancel))
            {
                await Task.Run(async () =>
                {
                    await using var connection = await session.OpenConnectionAsync(ct);
                    await EditApplier.ApplyAsync(connection, plan, ct);
                }, ct);
            }
        }
        catch (Exception) when (cts.IsCancellationRequested)
        {
            // EditApplier rolls back on any failure, a cancel included; a commit that won the race would not have thrown.
            Notice = "Save cancelled: nothing was saved (rolled back). Your edits are kept.";
            return;
        }
        catch (StaleRowException)
        {
            SetError("A row you edited no longer exists or its key changed: nothing was saved. Your edits are kept — reload to see the current data.");
            NeedsReload = true;
            return;
        }
        catch (AmbiguousRowException e)
        {
            SetError($"The key of an edited row matches {e.Affected} rows: nothing was written — define a more specific key. Your edits are kept.");
            NeedsReload = true;
            return;
        }
        catch (RowCountUnknownException e)
        {
            SetError(e.Message + " Your edits are kept.");
            return;
        }
        catch (ObjectDisposedException)
        {
            SetClosed();
            return;
        }
        catch (Exception e)
        {
            SetError("Nothing was saved (rolled back): " + ErrorText.Describe(e));
            return;
        }
        finally
        {
            IsSaving = false;
        }

        ClearPending();
        CellError = null;
        var query = _applied ?? CurrentInputs(Sort);
        var reloaded = await RunAsync(query, PageIndex);
        if (reloaded && Rows.Count == 0 && PageIndex > 0) reloaded = await RunAsync(query, PageIndex - 1);
        if (reloaded)
        {
            Notice = plan.Count == 1 ? "Saved 1 statement." : $"Saved {plan.Count} statements.";
            if (inserted > 0 && NewRowsMayBeElsewhere(query))
                Notice += inserted == 1
                    ? " The new row may be on another page or hidden by the filters or sort."
                    : " The new rows may be on another page or hidden by the filters or sort.";
            return;
        }

        // Saved, but the page could not be re-read: the rows on screen are what was typed, so show them as plain
        // loaded rows instead of leaving stale "pending" highlights.
        Rows = new System.Collections.ObjectModel.ObservableCollection<RowViewModel>(
            Rows.Where(r => !r.IsDeleted).Select(r => new RowViewModel(r.Values)));
        ErrorMessage = "The changes were saved, but the page could not be reloaded: " + (ErrorMessage ?? "unknown error");
        NeedsReload = true;
    }

    /// <summary>Where a save goes, for messages and the SQL preview: "name (host:port / database)".</summary>
    private static string Describe(ConnectionConfig c)
    {
        var host = string.IsNullOrWhiteSpace(c.Host) ? null : c.Port is { } port ? $"{c.Host}:{port}" : c.Host;
        return host is null ? $"{c.Name} ({c.Database})" : $"{c.Name} ({host} / {c.Database})";
    }

    /// <summary>The page that comes back after a save is the same query: a new row lands wherever the database puts it, not at the end of the page.</summary>
    private bool NewRowsMayBeElsewhere(AppliedQuery query) =>
        HasNextPage || PageIndex > 0 || query.Filters.Count > 0 || query.RawWhere is not null || query.Sort is not null;

    private bool RowsMatchColumns(IEnumerable<string> names) => _rowColumns is null || names.SequenceEqual(_rowColumns);

    // ---- leaving with pending edits ----

    /// <summary>
    /// Entry guard of every navigation (page, filters, sort, refresh, reload): true when it may go ahead. With pending
    /// edits the user chooses Save (through the SQL preview), Discard or Cancel.
    /// </summary>
    private Task<bool> ConfirmLeaveAsync() => HasPendingEdits || IsSaving ? ConfirmLeaveCoreAsync() : Task.FromResult(true);

    private async Task<bool> ConfirmLeaveCoreAsync()
    {
        if (IsSaving) return false;
        switch (await AskPendingAsync())
        {
            case SaveChoice.Discard:
                Revert();
                return true;
            case SaveChoice.Save:
                await ReviewAndSaveAsync();
                return !HasPendingEdits;
            default:
                return false;
        }
    }

    /// <summary>Asks Save / Discard / Cancel about the pending edits (Cancel, with an error message, when no dialog is available).</summary>
    private async Task<SaveChoice> AskPendingAsync()
    {
        if (_hooks.Dialogs is not { } dialogs)
        {
            SetError("This table has pending edits: save or revert them first.");
            return SaveChoice.Cancel;
        }
        return await dialogs.AskSaveDiscardAsync(
            "Pending edits",
            $"{PendingText} in {Connection.Name} · {Table.Name} {(_pending.Count == 1 ? "is" : "are")} not saved. Save them first?");
    }

    /// <summary>
    /// After a closed connection: gets the live session and switches to it WITHOUT touching the rows, but only when its
    /// table has exactly the columns the pending rows were loaded with. Otherwise the edits stay and nothing changes.
    /// </summary>
    private async Task<bool> ReconnectKeepingEditsAsync()
    {
        if (await ResolveLiveSessionAsync() is not { } live) return false;
        if (!SessionManager.SameDatabase(_session.Config, live.Config))
        {
            // The connection was edited (host, database, login...) after these rows were loaded: they came from another
            // database and must not be written to the new target.
            SetError($"The connection {Connection.Name} now points to a different server or database ({Describe(live.Config)}) than the one these rows were loaded from " +
                     $"({Describe(_session.Config)}), so the pending edits cannot be saved. Discard them (Revert) and reload. Nothing was saved.");
            NeedsReload = true;
            return false;
        }
        var info = live.Schema.FirstOrDefault(t => t.Ref == Table)
                   ?? live.Schema.FirstOrDefault(t => string.Equals(t.Ref.Name, Table.Name, StringComparison.OrdinalIgnoreCase)
                                                      && string.Equals(t.Ref.Schema, Table.Schema, StringComparison.OrdinalIgnoreCase));
        if (info is null || !RowsMatchColumns(info.Columns.OrderBy(c => c.Ordinal).Select(c => c.Name)))
        {
            SetError("The table's columns changed since these rows were loaded, so the pending edits cannot be saved safely. " +
                     "Discard them (Revert) and reload. Nothing was saved.");
            NeedsReload = true;
            return false;
        }
        UseSession(live);
        if (ErrorMessage == ConnectionClosedMessage) ErrorMessage = null;
        NeedsReload = false;
        return true;
    }

    public override Task<bool> ConfirmCloseAsync() => ConfirmLeaveAsync();

    public override bool NeedsCloseConfirmation => HasPendingEdits || IsSaving;

    // ---- virtual key ----

    /// <summary>Opens the "Define key" dialog and applies the choice.</summary>
    [RelayCommand]
    public async Task DefineKeyAsync()
    {
        if (!CanDefineKey || Info is not { } info || _hooks.Dialogs is not { } dialogs) return;
        var current = Config.VirtualKeys.GetValueOrDefault(Connection.Id)?.GetValueOrDefault(info.Ref.ToString());
        var dialog = new VirtualKeyDialogViewModel(info, _session.Driver.Kind, current);
        if (await dialogs.ShowVirtualKeyDialogAsync(dialog) is { } columns) SetVirtualKey(columns);
    }

    /// <summary>
    /// Defines the key of a table without primary key (<c>["*"]</c> = all columns), stores it in
    /// <c>AppConfig.VirtualKeys[connection][table]</c> through the config persistence, and re-evaluates
    /// <see cref="CanEdit"/>. When the config cannot be saved the key still applies for this session and the user is told.
    /// On SQL Server/PostgreSQL columns without equality (text, ntext, image, xml, json...) are left out: "*" is then
    /// stored as the explicit list of the usable columns.
    /// </summary>
    public void SetVirtualKey(IReadOnlyList<string> columns)
    {
        if (Info is not { } info) return;
        if (info.IsView || _session.Config.ReadOnly || info.Columns.Any(c => c.IsPrimaryKey))
        {
            SetError(info.IsView ? $"{Table} is a view and cannot be edited."
                : _session.Config.ReadOnly ? "This connection is read-only: editing is disabled."
                : $"{Table} has a primary key: a virtual key is not needed.");
            return;
        }

        var kind = _session.Driver.Kind;
        string[] stored;
        if (columns.Contains("*"))
        {
            var all = info.Columns.OrderBy(c => c.Ordinal).ToList();
            stored = all.All(c => VirtualKeyDialogViewModel.IsKeyable(c, kind))
                ? ["*"]
                : all.Where(c => VirtualKeyDialogViewModel.IsKeyable(c, kind)).Select(c => c.Name).ToArray();
        }
        else
        {
            var resolved = new List<string>();
            foreach (var name in columns)
            {
                var column = info.Columns.FirstOrDefault(c => c.Name == name)
                    ?? info.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (column is null)
                {
                    SetError($"Column '{name}' does not exist in {Table}.");
                    return;
                }
                if (!VirtualKeyDialogViewModel.IsKeyable(column, kind))
                {
                    SetError($"Column '{column.Name}' ({column.DataType}) cannot be compared with \"=\" and cannot be part of a key.");
                    return;
                }
                if (!resolved.Contains(column.Name)) resolved.Add(column.Name);
            }
            stored = resolved.ToArray();
        }
        if (stored.Length == 0)
        {
            SetError("A key needs at least one column.");
            return;
        }

        bool saved;
        if (_hooks.SetVirtualKey is { } set)
        {
            saved = set(Connection.Id, info.Ref, stored);
        }
        else
        {
            var config = Config;
            if (!config.VirtualKeys.TryGetValue(Connection.Id, out var perTable))
                config.VirtualKeys[Connection.Id] = perTable = new Dictionary<string, string[]>();
            perTable[info.Ref.ToString()] = stored;
            saved = false;
        }

        ErrorMessage = null;
        RefreshEditState();
        Notice = saved
            ? "Key saved."
            : "This key is applied for this session only: the config could not be saved, so it will be lost when the app closes.";
    }
}
