using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.ViewModels;

/// <summary>File pickers of the script editor; the window layer implements them, tests fake them.</summary>
public interface IScriptFiles
{
    /// <summary>Asks where to save a script; null when cancelled.</summary>
    Task<string?> PickSaveAsync(string suggestedName);

    /// <summary>Asks which script file to open; null when cancelled.</summary>
    Task<string?> PickOpenAsync();
}

/// <summary>What a script tab needs from the shell.</summary>
/// <param name="ResolveSession">Returns the live session of the tab's connection (connecting when needed), or null
/// when the connection no longer exists. Used for restored tabs (no session yet) and after Disconnect.</param>
/// <param name="Files">File pickers; without them a script without a file cannot be saved.</param>
/// <param name="Store">Where unsaved text is kept across restarts; null disables that.</param>
/// <param name="Status">Shared status bar.</param>
/// <param name="Dialogs">Asks Save / Discard / Cancel when a script with unsaved text is closed. Without it such a tab
/// refuses to close (its text would be lost without asking).</param>
/// <param name="Switch">Lets the tab switch between the members of its connection's group; without it the tab never switches.
/// When given, connecting resolves the session through it too, so it follows the connection the tab was switched to.</param>
public sealed record ScriptTabHooks(
    Func<CancellationToken, Task<Session?>> ResolveSession,
    IScriptFiles? Files = null,
    ScriptSessionStore? Store = null,
    StatusModel? Status = null,
    IEditDialogs? Dialogs = null,
    GroupSwitchHooks? Switch = null);

/// <summary>
/// A SQL script editor on one connection. Runs the statement under the caret / the selection (Ctrl+Enter) or the
/// whole script (F5) off the UI thread, collects one <see cref="StatementResultViewModel"/> per executed statement and
/// stops at the first error. Cancelling never throws. Unsaved text is kept in a <see cref="ScriptSessionStore"/>.
/// </summary>
public sealed partial class ScriptTabViewModel : TabViewModel
{
    public const int DefaultRowLimit = 1000;
    public const int MinRowLimit = 1;
    public const int MaxRowLimit = 100_000;
    public const string ConnectionClosedMessage = "Connection closed — reconnect";

    private readonly ScriptTabHooks _hooks;
    private readonly StatusModel _status;
    private readonly int _number;
    private readonly object _persistGate = new();
    private Session? _session;
    private System.Text.Encoding? _encoding;   // encoding of the file on disk; null = a new file (UTF-8, no BOM)
    private string? _savedText;      // text on disk (or "" for a new script); null = unknown, so always dirty
    private int _rowLimit = DefaultRowLimit;
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _persistCts;
    private volatile bool _closed;
    private volatile bool _persistFailed;   // the last autosave write threw
    private volatile bool _discarding;  // the user chose Discard while closing: the autosave must not be written (again)

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private int _caretOffset;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _needsReconnect;

    /// <param name="session">The live session, or null (restored tab): it is resolved through the hooks on first run.</param>
    /// <param name="number">The N of "Script N" in the title.</param>
    /// <param name="restored">A script saved by <see cref="ScriptSessionStore"/>: its id, text and file are taken over.</param>
    public ScriptTabViewModel(
        ConnectionConfig connection, Session? session, ScriptTabHooks hooks, int number, SavedScript? restored = null)
        : base(connection)
    {
        _session = session;
        _hooks = hooks;
        _status = hooks.Status ?? new StatusModel();
        _number = number;
        Id = restored?.TabId ?? Guid.NewGuid();
        _savedText = "";
        if (restored is not null)
        {
            _filePath = restored.FilePath;
            _savedText = restored.FilePath is null ? "" : TryReadFile(restored.FilePath, out _encoding);
            if (restored.Text is null && _savedText is null)
            {
                // Nothing unsaved and the file cannot be read (the shell already told the user): a new script, never bound to
                // that path, so neither the autosave nor Ctrl+S can touch the file.
                _filePath = null;
                _encoding = null;
                _savedText = "";
            }
            _text = restored.Text ?? _savedText ?? "";   // nothing unsaved: the file as it is now
            _isDirty = _text != _savedText;
        }
    }

    /// <summary>Identity of the tab, key of its persisted copy.</summary>
    public Guid Id { get; }

    /// <summary>How long after the last edit the text is persisted (about a second).</summary>
    public TimeSpan PersistDelay { get; set; } = TimeSpan.FromSeconds(1);

    public override string Title
    {
        get
        {
            var name = FilePath is { } path ? Path.GetFileName(path) : $"Script {_number} · {Connection.Name}";
            return IsDirty ? "• " + name : name;
        }
    }

    /// <summary>The selected range (start inclusive, end exclusive) in <see cref="Text"/>; null or empty when nothing is selected.</summary>
    public (int Start, int End)? Selection { get; set; }

    /// <summary>
    /// Called when a fresh editor is attached (the tab control recreates the view on every tab switch): a new editor has
    /// no selection, so an old range must not linger and make Ctrl+Enter run a hidden selection.
    /// </summary>
    public void EditorAttached() => Selection = null;

    /// <summary>Maximum rows kept per result set, between <see cref="MinRowLimit"/> and <see cref="MaxRowLimit"/>.</summary>
    public int RowLimit
    {
        get => _rowLimit;
        set => SetProperty(ref _rowLimit, Math.Clamp(value, MinRowLimit, MaxRowLimit));
    }

    /// <summary>Results of the last run, one per executed statement.</summary>
    public ObservableCollection<StatementResultViewModel> Results { get; } = new();

    /// <summary>The result tab selected in the view (the latest, or the failed one).</summary>
    [ObservableProperty] private StatementResultViewModel? _selectedResult;

    partial void OnTextChanged(string value)
    {
        _discarding = false;   // edited again: whatever was said about discarding no longer applies
        IsDirty = value != _savedText;
        SchedulePersist();
    }

    partial void OnIsDirtyChanged(bool value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasUnsavedWork));
    }

    public override string IconKey => "IconScript";

    public override bool HasUnsavedWork => IsDirty;

    partial void OnFilePathChanged(string? value) => OnPropertyChanged(nameof(Title));

    // ---- running ----

    /// <summary>
    /// Ctrl+Enter: the selection when there is one (run as selected, split by ';' / GO), else the statement the caret is in,
    /// delimited by ';', GO lines and blank lines (<see cref="ScriptSplitter.StatementAtCaret"/>).
    /// </summary>
    [RelayCommand]
    public Task RunStatementAsync() => RunAsync(StatementsAtCaretOrSelection(Connection.Kind));

    /// <summary>
    /// The statements Ctrl+Enter runs: the selection when there is one, else the statement under the caret. Shared with the
    /// group run (Ctrl+Shift+Enter), so both follow exactly the same rules.
    /// </summary>
    private IReadOnlyList<ScriptStatement> StatementsAtCaretOrSelection(DbKind kind)
    {
        var text = Text;
        if (Selection is { } sel)
        {
            var start = Math.Clamp(Math.Min(sel.Start, sel.End), 0, text.Length);
            var end = Math.Clamp(Math.Max(sel.Start, sel.End), 0, text.Length);
            if (end > start) return ScriptSplitter.Split(text[start..end], kind);
        }

        // No selection: a blank line also ends a statement (T-SQL needs no ';'), so only the one under the caret runs.
        var at = ScriptSplitter.StatementAtCaret(text, kind, CaretOffset);
        return at is null ? [] : [at];
    }

    /// <summary>F5: every statement of the script.</summary>
    [RelayCommand]
    public Task RunScriptAsync() => RunAsync(ScriptSplitter.Split(Text, Connection.Kind));

    /// <summary>Cancels the running script, or the running group run (button or Ctrl+Break). Safe to call at any time.</summary>
    [RelayCommand]
    public void Cancel()
    {
        try { _runCts?.Cancel(); }
        catch (ObjectDisposedException) { /* the run just finished */ }
        CancelGroup();
    }

    private async Task RunAsync(IReadOnlyList<ScriptStatement> statements)
    {
        if (IsRunning)
        {
            _status.Info("A script is already running — cancel it first.");
            return;
        }
        if (IsGroupRunning)
        {
            _status.Info("A group run is in progress — cancel it first.");
            return;
        }
        if (IsSwitching)
        {
            _status.Info("Switching database — try again in a moment.");
            return;
        }
        if (statements.Count == 0)
        {
            _status.Info("Nothing to run.");
            return;
        }

        IsRunning = true;   // set before the first await: the caller sees it at once
        GroupRun = null;    // the results of this run replace those of a group run
        ErrorMessage = null;
        NeedsReconnect = false;
        Results.Clear();
        SelectedResult = null;
        using var cts = _runCts = new CancellationTokenSource();
        var ct = cts.Token;
        var ran = 0;
        ScriptConnection? started = null;   // set once the database work was handed to the pool
        Task? startedWork = null;
        try
        {
            // Cancel returns control at once, but the cancelled statement may still be winding down on the tab's connection:
            // the next run waits for it (the connection is shared by the runs of this tab) instead of failing.
            if (_lastProducer is { IsCompleted: false } winding)
            {
                _status.Info("Waiting for the cancelled statement to stop…");
                await winding.WaitAsync(ct);
            }

            if (_closed) return;   // closed while waiting: a connection created now would never be released
            var session = _session ?? await ResolveSessionAsync(ct);
            if (session is null || _closed) return;

            var maxRows = RowLimit;
            // The connection's current flag (it can be toggled while connected) and its kind drive the guard.
            var options = new ScriptOptions(maxRows, session.Config.ReadOnly, session.Config.Kind);

            // Everything that touches the database runs on the thread pool: some providers (SQLite) execute
            // synchronously, so the UI thread must never be the one calling them. Results flow back through a channel.
            var script = ConnectionFor(session);
            var channel = System.Threading.Channels.Channel.CreateUnbounded<StatementResult>();
            var producer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var r in script.RunAsync(statements, options, ct))
                        channel.Writer.TryWrite(r);
                    return null;
                }
                catch (Exception e)
                {
                    return e;
                }
                finally
                {
                    channel.Writer.Complete();
                }
            }, CancellationToken.None);
            _lastProducer = startedWork = producer;
            started = script;

            // Reading with the token lets Cancel return control at once even when the provider cannot interrupt a
            // running statement (SQLite finishes it in the background; its late results are dropped).
            await foreach (var r in channel.Reader.ReadAllAsync(ct))
            {
                var vm = new StatementResultViewModel(++ran, r, maxRows);
                Results.Add(vm);
                SelectedResult = vm;
            }

            if (await producer is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);

            Report(ran, script.Reopened, script.ConnectionReset);
        }
        catch (OperationCanceledException)
        {
            _status.Info(ran == 0 ? "Cancelled." : $"Cancelled after {ran} statement(s).");
            ReportResetWhenTheCancelledWorkEnds(started, startedWork);
        }
        catch (ObjectDisposedException) when (_closed)
        {
            // The tab was closed while the run was starting (its connection is released by Close): nothing to report.
        }
        catch (ObjectDisposedException)
        {
            _session = null;   // the next run resolves a live one; Reconnect does it explicitly
            _script?.Dispose();
            _script = null;
            ErrorMessage = ConnectionClosedMessage;
            NeedsReconnect = true;
            _status.Error(ConnectionClosedMessage);
        }
        catch (Exception e)
        {
            ErrorMessage = ErrorText.Describe(e);
            _status.Error(ErrorMessage);
        }
        finally
        {
            _runCts = null;
            IsRunning = false;
        }
    }

    public const string SessionResetNote =
        "(The connection of this tab was lost or reset: it was reopened, so open transactions, temp tables and variables are gone.)";

    private void Report(int ran, bool reopened, bool reset)
    {
        var note = reopened ? " " + SessionResetNote : "";
        if (Results.LastOrDefault(r => r.HasError) is { } failed)
        {
            SelectedResult = failed;
            _status.Error($"Statement {failed.Index} failed: {failed.Error}{note}");
        }
        else
        {
            _status.Info((ran == 1 ? Results[0].StatusText : $"{ran} statements ran.") + note);
        }

        if (reset) ShowReset();
    }

    /// <summary>The connection was found unusable at the end of this run (aborted statement): said loudly, in this run's status.</summary>
    private void ShowReset()
    {
        ErrorMessage = ScriptConnection.ResetMessage;
        _status.Error(ScriptConnection.ResetMessage);
    }

    /// <summary>
    /// A cancelled run returns at once while its work finishes in the background; when that work ends with a reset connection
    /// (an open transaction was lost) the user is told then, back on the UI thread.
    /// </summary>
    private void ReportResetWhenTheCancelledWorkEnds(ScriptConnection? script, Task? work)
    {
        if (script is null || work is null) return;
        var context = SynchronizationContext.Current;
        _ = work.ContinueWith(_ =>
        {
            if (!script.ConnectionReset || _closed || !ReferenceEquals(script, _script)) return;   // not about a connection the tab left (switch)
            if (context is null) ShowReset();
            else context.Post(_ => ShowReset(), null);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // ---- the tab's own connection ----

    private ScriptConnection? _script;
    private Task? _lastProducer;   // the database work of the latest run; it can outlive a cancelled run

    /// <summary>
    /// The dedicated connection of this tab for <paramref name="session"/>: created by the first run (nothing is opened
    /// here), kept for the later runs so a manual transaction or a temp table survives, replaced when the tab moves to
    /// another session (after a reconnect). Released by <see cref="Close"/> and when the session is closed.
    /// </summary>
    private ScriptConnection ConnectionFor(Session session)
    {
        if (_script is { } current && ReferenceEquals(current.Session, session)) return current;
        _script?.Dispose();
        return _script = new ScriptConnection(session);
    }

    /// <summary>
    /// The live session, resolved through the hooks; null (with <see cref="ErrorMessage"/> set) when there is none. A slow
    /// resolve can straddle a group switch: when the tab moved to another connection (or a switch is in progress) meanwhile,
    /// the outcome, success or failure, is about a connection the tab is leaving and is discarded (null) WITHOUT touching the
    /// session or the error state.
    /// </summary>
    private async Task<Session?> ResolveSessionAsync(CancellationToken ct)
    {
        var id = Connection.Id;
        bool Stale() => Connection.Id != id || IsSwitching;
        try
        {
            // WaitAsync: Cancel returns at once even when the connect itself cannot be interrupted (it may finish in the background).
            var resolving = ResolveHook(id, ct);
            // After a cancel the connect carries on unobserved; if it fails later nobody awaits it, so observe that failure here.
            _ = resolving.ContinueWith(
                static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var live = await resolving.WaitAsync(ct);
            if (Stale()) return null;
            if (live is null)
            {
                ErrorMessage = "The connection no longer exists — close this tab.";
                _status.Error(ErrorMessage);
                return null;
            }
            return _session = live;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception e)
        {
            if (Stale()) return null;
            ErrorMessage = "Could not connect: " + ErrorText.Describe(e);
            _status.Error(ErrorMessage);
            return null;
        }
    }

    /// <summary>Asks the shell for the live session of <paramref name="connectionId"/> (the tab's current connection follows a switch).</summary>
    private Task<Session?> ResolveHook(Guid connectionId, CancellationToken ct) =>
        _hooks.Switch is { } sw ? sw.ResolveSession(connectionId, ct) : _hooks.ResolveSession(ct);

    // ---- switching to another member of the group ----

    protected override GroupSwitchHooks? GroupHooks => _hooks.Switch;

    protected override void ReportSwitchProblem(string message) => _status.Info(message);

    protected override string? SwitchRefusal =>
        IsGroupRunning ? "Cannot switch database while a group run is in progress — cancel it first." : null;

    /// <summary>
    /// Moves the script to another database of the group: the text and the results stay, the next run uses the new
    /// connection. The target is connected first (through the tree), so an unreachable member is reported now and the tab
    /// stays as it was. Then the tab's dedicated connection to the old database is released (an open transaction on it is
    /// rolled back, exactly as when the tab closes) and the old session is not used again. Refused while a script runs.
    /// </summary>
    protected override async Task<bool> SwitchCoreAsync(ConnectionConfig target)
    {
        if (IsRunning)
        {
            _status.Info("Cannot switch database while a script is running — cancel it first.");
            return false;
        }
        if (_reconnecting)
        {
            _status.Info("Cannot switch database while reconnecting — try again when it has finished.");
            return false;
        }

        Session? live;
        try
        {
            live = await ResolveHook(target.Id, CancellationToken.None);
        }
        catch (Exception e)
        {
            ErrorMessage = $"Could not connect to {target.Name}: {ErrorText.Describe(e)} The tab stays on {Connection.Name}.";
            _status.Error(ErrorMessage);
            return false;
        }

        if (live is null)
        {
            ErrorMessage = $"{target.Name} no longer exists. The tab stays on {Connection.Name}.";
            _status.Error(ErrorMessage);
            return false;
        }
        var oldName = Connection.Name;
        var leftTransaction = _script is { } held && await held.HasOpenTransactionAsync();   // probed before the release rolls it back
        if (_closed || IsRunning || IsGroupRunning) return false;   // closed meanwhile (nothing may be created now) or a run slipped in

        var old = _script;
        _script = null;
        old?.Dispose();   // rolls an open transaction back
        _session = live;
        Connection = live.Config;
        ErrorMessage = null;
        NeedsReconnect = false;
        if (ShouldPersist) SchedulePersist();   // the persisted copy names the connection
        _status.Info($"Switched to {live.Config.Name}: the next run uses it."
                     + (leftTransaction
                         ? $" The connection to {oldName} was released: a transaction left open there was rolled back (a later COMMIT will not commit it)."
                         : ""));
        return true;
    }

    /// <summary>Re-resolves the live session of the connection after "Connection closed".</summary>
    [RelayCommand]
    public async Task ReconnectAsync()
    {
        if (IsRunning || IsSwitching || _reconnecting) return;   // while switching it would reinstall the old session
        var id = Connection.Id;
        _reconnecting = true;   // a switch is refused until this has finished
        try
        {
            ErrorMessage = null;
            NeedsReconnect = false;
            var session = await ResolveSessionAsync(CancellationToken.None);
            if (Connection.Id != id || IsSwitching) return;   // the tab moved on: this outcome is not about its connection
            if (session is null) NeedsReconnect = true;
        }
        finally
        {
            _reconnecting = false;
        }
    }

    private bool _reconnecting;

    [RelayCommand]
    public void DismissError() => ErrorMessage = null;

    // ---- files ----

    /// <summary>Ctrl+S: writes the script to <paramref name="path"/>, else to its file, else to the file the user picks.</summary>
    [RelayCommand]
    public async Task SaveAsync(string? path = null)
    {
        path ??= FilePath;
        if (path is null)
        {
            if (_hooks.Files is not { } files)
            {
                _status.Error("Cannot save: no file dialog available.");
                return;
            }
            path = await files.PickSaveAsync($"Script {_number}.sql");
            if (path is null) return;
        }

        var text = Text;
        var converted = false;
        try
        {
            var (bytes, used, changed) = ScriptFileEncoding.Encode(text, _encoding);
            await File.WriteAllBytesAsync(path, bytes);
            _encoding = used;
            converted = changed;
        }
        catch (Exception e)
        {
            _status.Error($"Cannot save {path}: {e.Message}");
            return;
        }

        FilePath = path;
        _savedText = text;
        IsDirty = Text != _savedText;
        DiscardPersisted(keepOpen: true);
        if (IsDirty) SchedulePersist();   // edited while the file was being written
        _status.Info(converted
            ? $"Saved {path} (converted to UTF-8: the text has characters the file's encoding cannot hold)"
            : $"Saved {path}");
    }

    /// <summary>Loads a file into the editor, replacing the text. A file that cannot be read leaves the tab as it was.</summary>
    public async Task OpenAsync(string path)
    {
        string text;
        System.Text.Encoding encoding;
        try
        {
            (text, encoding) = ScriptFileEncoding.Decode(await File.ReadAllBytesAsync(path));
        }
        catch (Exception e)
        {
            _status.Error($"Cannot open {path}: {e.Message}");
            return;
        }

        _encoding = encoding;
        _savedText = text;
        FilePath = path;
        Text = text;
        CaretOffset = 0;
        Selection = null;
        IsDirty = false;
        DiscardPersisted(keepOpen: true);
        _status.Info($"Opened {path}");
    }

    private static string? TryReadFile(string path, out System.Text.Encoding? encoding)
    {
        try
        {
            var (text, detected) = ScriptFileEncoding.Decode(File.ReadAllBytes(path));
            encoding = detected;
            return text;
        }
        catch (Exception)
        {
            encoding = null;
            return null;
        }
    }

    // ---- keeping unsaved text across restarts ----

    private bool ShouldPersist => IsDirty && !(string.IsNullOrWhiteSpace(Text) && FilePath is null);

    private void SchedulePersist()
    {
        if (_hooks.Store is null || _closed) return;
        CancelPersistTimer();
        var cts = _persistCts = new CancellationTokenSource();
        var delay = PersistDelay;
        _ = Task.Delay(delay, cts.Token).ContinueWith(
            t => { if (!t.IsCanceled) PersistNow(); },
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void CancelPersistTimer()
    {
        var old = Interlocked.Exchange(ref _persistCts, null);
        try { old?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Writes (or, when there is nothing unsaved, deletes) the persisted copy now. Called on tab change and exit.</summary>
    public void FlushPersist()
    {
        CancelPersistTimer();
        PersistNow();
    }

    private void PersistNow()
    {
        if (_hooks.Store is not { } store) return;
        lock (_persistGate)
        {
            if (_closed) return;
            try
            {
                if (ShouldPersist && !_discarding)
                {
                    store.Save(Id, Connection.Id, Text, FilePath);
                    _persistFailed = false;
                }
                else
                {
                    store.Delete(Id);
                }
            }
            catch (Exception)
            {
                // best effort: losing the autosave must never disturb editing. It is remembered, though: closing the
                // window must not rely on a copy that could not be written (see ConfirmExitAsync).
                _persistFailed = true;
            }
        }
    }

    private void DiscardPersisted(bool keepOpen)
    {
        CancelPersistTimer();
        lock (_persistGate)
        {
            if (!keepOpen) _closed = true;
            try { _hooks.Store?.Delete(Id); }
            catch (Exception) { }
        }
    }

    public override bool NeedsCloseConfirmation => ShouldPersist;

    /// <summary>
    /// Closing with unsaved text asks Save / Discard / Cancel. An empty new script or one equal to its file closes
    /// silently. Save writes the file (asking where when there is none; cancelling that picker, or a failed write,
    /// counts as Cancel). Discard says yes and stops the autosave from being written again, so the discarded text is
    /// not brought back at the next start even when the window closes without going through <see cref="Close"/>.
    /// </summary>
    public override async Task<bool> ConfirmCloseAsync()
    {
        _discarding = false;
        if (!ShouldPersist) return true;
        if (_hooks.Dialogs is not { } dialogs)
        {
            _status.Error("This script has unsaved changes: save it first (Ctrl+S).");
            return false;
        }

        var name = FilePath is { } path ? Path.GetFileName(path) : $"Script {_number}";
        var choice = await dialogs.AskSaveDiscardAsync(
            "Unsaved script", $"{name} ({Connection.Name}) has unsaved changes. Save them first?");
        switch (choice)
        {
            case SaveChoice.Save:
                await SaveAsync();
                return !IsDirty && FilePath is not null;   // picker cancelled, write failed or edited meanwhile: keep the tab
            case SaveChoice.Discard:
                _discarding = true;
                FlushPersist();   // deletes the autosave now
                return true;
            default:
                return false;
        }
    }

    /// <summary>The user chose Discard while the window was closing: the tab stays in the list until the app ends but must not be restored.</summary>
    public bool IsDiscarded => _discarding;

    /// <summary>The autosave can keep the unsaved text: there is a store and the last write into it worked.</summary>
    private bool AutosaveWorks => _hooks.Store is not null && !_persistFailed;

    /// <summary>
    /// Closing the WINDOW does not ask about a dirty script: its autosave is written now and brings it back at the next
    /// start. Safety net: when that copy cannot be kept (no store, or a write failed) the user is asked like for a tab close.
    /// </summary>
    public override async Task<bool> ConfirmExitAsync()
    {
        if (!ShouldPersist) return true;
        FlushPersist();   // surfaces a failing store now rather than at the very end
        return AutosaveWorks || await ConfirmCloseAsync();
    }

    public override bool NeedsExitConfirmation => ShouldPersist && !AutosaveWorks;

    /// <summary>The close this tab agreed to was abandoned: it stays open, so its unsaved text is kept on disk again.</summary>
    public override void CloseAborted()
    {
        if (!_discarding) return;
        _discarding = false;
        FlushPersist();
    }

    /// <summary>
    /// The tab was closed by the user: stops the run, forgets the persisted copy for good and releases the tab's connection
    /// (a transaction still open on it is rolled back).
    /// </summary>
    public void Close()
    {
        DiscardPersisted(keepOpen: false);
        Cancel();
        CloseGroupRun();
        _script?.Dispose();
        _script = null;
    }
}
