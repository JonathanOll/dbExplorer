using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Model;
using DbExplorer.Core.Plans;
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

    /// <summary>Asks where to export a result (<paramref name="extension"/> without the dot, e.g. "csv"); null when cancelled or unsupported.</summary>
    Task<string?> PickExportAsync(string suggestedName, string extension) => Task.FromResult<string?>(null);
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
/// <param name="FindSession">The session of a connection if it is already open, or null. Never connects: the autocomplete uses it
/// to read the loaded schema of a restored tab.</param>
/// <param name="Library">The script library: with it (and a dialog), the first save of a script asks for a name and a library folder.</param>
/// <param name="FileUsed">Told the path of every script file opened or saved (recent scripts, library refresh).</param>
public sealed record ScriptTabHooks(
    Func<CancellationToken, Task<Session?>> ResolveSession,
    IScriptFiles? Files = null,
    ScriptSessionStore? Store = null,
    StatusModel? Status = null,
    IEditDialogs? Dialogs = null,
    GroupSwitchHooks? Switch = null,
    Func<Guid, Session?>? FindSession = null,
    ScriptLibrary? Library = null,
    Action<string>? FileUsed = null);

/// <summary>
/// A SQL script editor on one connection. Runs the statement under the caret / the selection (Ctrl+Enter) or the
/// whole script (F5) off the UI thread, collects one <see cref="StatementResultViewModel"/> per executed statement and
/// stops at the first error. Cancelling never throws. Unsaved text is kept in a <see cref="ScriptSessionStore"/>.
/// </summary>
public sealed partial class ScriptTabViewModel : TabViewModel, IPlanCompareHost
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
    [NotifyPropertyChangedFor(nameof(ErrorTitle), nameof(ErrorHint))]
    [ObservableProperty] private string? _errorMessage;
    [NotifyPropertyChangedFor(nameof(ErrorTitle), nameof(ErrorHint))]
    [ObservableProperty] private bool _needsReconnect;

    /// <summary>The error message is a caution (the connection was reset), not a failure: the banner is amber.</summary>
    [NotifyPropertyChangedFor(nameof(ErrorTitle), nameof(ErrorHint))]
    [ObservableProperty] private bool _errorIsWarning;

    /// <summary>Bold first line of the run-level banner, or null when the message says everything.</summary>
    public string? ErrorTitle => NeedsReconnect ? "Connection lost" : ErrorIsWarning ? "Connection reset" : null;

    /// <summary>Dim line under the message that says what the user can do.</summary>
    public string? ErrorHint => NeedsReconnect
        ? "Reconnecting reopens the connection of this tab: open transactions, temp tables and variables are gone."
        : null;

    partial void OnErrorMessageChanged(string? value) => ErrorIsWarning = false;   // a caution says so explicitly (ShowReset)

    // ---- what the run is doing and did ----

    /// <summary>When the running script or group run started (the view shows the elapsed time); null when nothing runs.</summary>
    [ObservableProperty] private DateTime? _runStartedAt;

    /// <summary>"Running statement 2 of 5" / "Running on 3 databases" while something runs; null otherwise.</summary>
    [ObservableProperty] private string? _runProgressText;

    /// <summary>One line under the toolbar about the last run when it has several statements, failed or was cancelled; null otherwise.</summary>
    [NotifyPropertyChangedFor(nameof(HasRunSummary), nameof(RunSummaryIsSuccess))]
    [ObservableProperty] private string? _runSummary;

    /// <summary>The summary is about a failed run (red icon).</summary>
    [NotifyPropertyChangedFor(nameof(RunSummaryIsSuccess))]
    [ObservableProperty] private bool _runSummaryIsError;

    /// <summary>The summary is about a cancelled run (neutral icon).</summary>
    [NotifyPropertyChangedFor(nameof(RunSummaryIsSuccess))]
    [ObservableProperty] private bool _runSummaryIsCancelled;

    /// <summary>The summary is about a run that went through (green icon).</summary>
    public bool RunSummaryIsSuccess => HasRunSummary && !RunSummaryIsError && !RunSummaryIsCancelled;

    public bool HasRunSummary => RunSummary is not null;

    /// <summary>Why the run buttons are disabled right now (null when they are enabled): the tooltip says it.</summary>
    public string? RunDisabledReason =>
        IsRunning ? "A script is running. Stop it first."
        : IsGroupRunning ? "A group run is in progress. Stop it first."
        : IsSwitching ? "Switching database. Try again in a moment."
        : null;

    /// <summary>The tab's connection refuses writes (a lock badge in the toolbar).</summary>
    public bool IsReadOnly => Connection.ReadOnly;

    /// <summary>"Shop (local) · SQLite · localhost:5432 / shop": what the connection badge's tooltip says.</summary>
    public string ConnectionDescription
    {
        get
        {
            var c = Connection;
            var where = string.IsNullOrWhiteSpace(c.Host) ? c.Database : $"{c.Host}{(c.Port is { } port ? ":" + port : "")} / {c.Database}";
            var text = $"{c.Name} · {c.Kind}" + (string.IsNullOrWhiteSpace(where) ? "" : $" · {where}");
            return c.ReadOnly ? text + " · read-only" : text;
        }
    }

    // The statements of the last ordinary run and the text they came from (the result tabs point into it).
    private IReadOnlyList<ScriptStatement> _lastStatements = [];
    private string _runText = "";
    private bool _lastRunWasGroup;
    private PlanMode _lastPlanRequest;   // what the last run asked for explicitly (Ctrl+L), not the toggle
    private bool _lastGroupWhole;
    private int _totalStatements;
    private bool _estimatedRun;   // the run in progress (or the last one) asked for the estimated plan: nothing ran

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
    /// What the autocomplete may offer: the schema already loaded for this tab's connection. Null when the connection is not
    /// open (or its schema is not loaded): then only keywords and functions are offered. Never connects, never queries.
    /// </summary>
    public ICompletionSchema? CompletionSchema =>
        (_session ?? _hooks.FindSession?.Invoke(Connection.Id)) is { } session ? new TableListSchema(session.Schema) : null;

    /// <summary>
    /// Called when a fresh editor is attached (the tab control recreates the view on every tab switch): a new editor has
    /// no selection, so an old range must not linger and make Ctrl+Enter run a hidden selection.
    /// </summary>
    public void EditorAttached() => Selection = null;

    /// <summary>Maximum rows kept per result set, between <see cref="MinRowLimit"/> and <see cref="MaxRowLimit"/>.</summary>
    public int RowLimit
    {
        get => _rowLimit;
        set
        {
            if (SetProperty(ref _rowLimit, Math.Clamp(value, MinRowLimit, MaxRowLimit)))
            {
                OnPropertyChanged(nameof(RowLimitText));
                OnPropertyChanged(nameof(IsDefaultRowLimit));
            }
        }
    }

    /// <summary>The row limit as the toolbar button says it: "1,000 rows".</summary>
    public string RowLimitText => StatementResultViewModel.RowsText(RowLimit);

    public bool IsDefaultRowLimit => RowLimit == DefaultRowLimit;

    /// <summary>Limits offered by the row limit menu.</summary>
    public static IReadOnlyList<int> RowLimitPresets { get; } = [100, 500, 1_000, 5_000, 10_000, 100_000];

    /// <summary>
    /// "Raise limit" of a truncated result: the limit grows tenfold (at most <see cref="MaxRowLimit"/>) and what ran last
    /// runs again, here or on the group. Says so when the limit is already at its maximum.
    /// </summary>
    public Task RaiseRowLimitAndRerunAsync()
    {
        if (RowLimit >= MaxRowLimit)
        {
            _status.Info($"The row limit is already at its maximum ({MaxRowLimit:N0}). Narrow the query with WHERE or TOP.");
            return Task.CompletedTask;
        }
        RowLimit = (int)Math.Min((long)RowLimit * 10, MaxRowLimit);
        return _lastRunWasGroup ? RunOnGroupAsync(_lastGroupWhole) : RunAsync(_lastStatements, _lastPlanRequest);
    }

    /// <summary>
    /// Copy of a result set: the text to put on the clipboard (header and the <paramref name="selected"/> rows, or all rows when
    /// none are selected), tab separated.
    /// </summary>
    public string CopyText(ResultSetViewModel set, IReadOnlyList<object?[]>? selected)
    {
        var rows = selected is { Count: > 0 } ? selected : set.Rows;
        _status.Info($"Copied {StatementResultViewModel.RowsText(rows.Count)} with the column names.");
        return ResultExport.ToTsv(set.Columns, rows);
    }

    /// <summary>
    /// Export of a result set to a CSV or JSON file chosen by the user: the <paramref name="selected"/> rows, or all rows when none
    /// are selected. Says what was written (and that it is limited to the row limit) in the status bar; never throws.
    /// </summary>
    public async Task ExportAsync(ResultSetViewModel set, ExportFormat format, IReadOnlyList<object?[]>? selected)
    {
        if (_hooks.Files is not { } files)
        {
            _status.Error("Cannot export: no file dialog available.");
            return;
        }
        var extension = format == ExportFormat.Csv ? "csv" : "json";
        var baseName = FilePath is { } path ? Path.GetFileNameWithoutExtension(path) : $"Script {_number}";
        var name = $"{baseName} result{(set.Index > 1 ? " " + set.Index : "")}.{extension}";
        var target = await files.PickExportAsync(name, extension);
        if (target is null) return;

        var rows = selected is { Count: > 0 } ? selected : set.Rows;
        try
        {
            var text = format == ExportFormat.Csv ? ResultExport.ToCsv(set.Columns, rows) : ResultExport.ToJson(set.Columns, rows);
            // CSV gets a byte order mark so a spreadsheet reads accents right; JSON must not have one.
            await File.WriteAllTextAsync(target, text, new System.Text.UTF8Encoding(format == ExportFormat.Csv));
        }
        catch (Exception e)
        {
            _status.Error($"Cannot export to {target}: {e.Message}");
            return;
        }
        _status.Info($"Exported {StatementResultViewModel.RowsText(rows.Count)} to {target}"
                     + (set.Truncated && selected is not { Count: > 0 } ? $" (the result is limited to the first {RowLimit:N0} rows)." : "."));
    }

    /// <summary>Raised when the view should select a range of the text and bring it into view (Go to statement).</summary>
    public event Action<int, int>? RevealRequested;

    /// <summary>
    /// "Go to statement" of a result: selects the statement in the editor. The tab points into the text of the run, so it
    /// says so, and does nothing, when the script was edited since.
    /// </summary>
    public void GoToStatement(StatementResultViewModel result)
    {
        if (!result.HasRange) return;
        if (_runText != Text)
        {
            _status.Info("The script changed since this run, so the statement cannot be located. Run it again.");
            return;
        }
        RevealRequested?.Invoke(result.StartOffset, result.EndOffset);
    }

    /// <summary>The statement results still point at the text in the editor (it was not edited since the run).</summary>
    public bool ResultsMatchText => _runText == Text;

    /// <summary>1-based line of <paramref name="offset"/> in <paramref name="text"/>.</summary>
    private static int LineOf(string text, int offset)
    {
        var line = 1;
        for (var i = 0; i < Math.Min(offset, text.Length); i++)
            if (text[i] == '\n') line++;
        return line;
    }

    /// <summary>Results of the last run, one per executed statement.</summary>
    public ObservableCollection<StatementResultViewModel> Results { get; } = new();

    /// <summary>The result tab selected in the view (the latest, or the failed one).</summary>
    [ObservableProperty] private StatementResultViewModel? _selectedResult;

    /// <summary>
    /// What the result tab strip shows: <see cref="Results"/> and, after a run with <see cref="StatisticsEnabled"/>, the
    /// Statistics and Messages tabs behind them. <see cref="Results"/> never holds those two.
    /// </summary>
    public ObservableCollection<IResultTab> ResultTabs { get; } = new();

    /// <summary>
    /// The plan kept as the baseline of a comparison. It lives here, not in <see cref="ResultTabs"/>, because every run clears the
    /// result tabs: the next run's plan tab offers to compare with it.
    /// </summary>
    [ObservableProperty] private PlanCompareSource? _pinnedPlan;

    IEnumerable<PlanResultTab> IPlanCompareHost.PlanTabs => ResultTabs.OfType<PlanResultTab>();

    public void PinPlan(PlanCompareSource? plan)
    {
        PinnedPlan = plan;
        foreach (var tab in ResultTabs.OfType<PlanResultTab>()) tab.RefreshPin();
    }

    /// <summary>The tab selected in the strip; follows <see cref="SelectedResult"/> when that is a statement's result.</summary>
    [ObservableProperty] private IResultTab? _selectedResultTab;

    partial void OnSelectedResultChanged(StatementResultViewModel? value)
    {
        if (value is not null || SelectedResultTab is StatementResultViewModel) SelectedResultTab = value;
    }

    partial void OnSelectedResultTabChanged(IResultTab? value)
    {
        if (value is StatementResultViewModel result) SelectedResult = result;
    }

    // ---- execution statistics ----

    /// <summary>
    /// The "Statistics" toggle: the next runs set STATISTICS IO / TIME on the tab's connection and show the Statistics and
    /// Messages tabs. Per tab, not remembered across restarts. Only has an effect on SQL Server (<see cref="CanUseStatistics"/>).
    /// </summary>
    [ObservableProperty] private bool _statisticsEnabled;

    /// <summary>The tab's connection is SQL Server, the only engine with STATISTICS IO / TIME.</summary>
    public bool CanUseStatistics => Connection.Kind == DbKind.SqlServer;

    // ---- execution plans ----

    /// <summary>
    /// "Actual plan" toggle: every run also returns the plan the statements ran with (SQL Server: STATISTICS XML, PostgreSQL:
    /// EXPLAIN ANALYZE, whose rows are not returned). Not for SQLite, which has only the estimated plan. Per tab, not remembered.
    /// </summary>
    [NotifyPropertyChangedFor(nameof(ActualPlanTip))]
    [ObservableProperty] private bool _actualPlanEnabled;

    /// <summary>The engine can give the plan a statement really ran with.</summary>
    public bool CanUseActualPlan => Connection.Kind != DbKind.Sqlite;

    public string ActualPlanTip => Connection.Kind switch
    {
        DbKind.Postgres => "Run the statements and show the plan they ran with, with real rows and times (EXPLAIN ANALYZE: the rows themselves are not returned)",
        _ => "Run the statements and show the plan they ran with, with real row counts and times, next to the results",
    };

    /// <summary>Ctrl+L: the plan the optimizer would use for the selection or the statement at the caret. Nothing is run.</summary>
    [RelayCommand]
    public Task ShowEstimatedPlanAsync() => RunAsync(StatementsAtCaretOrSelection(Connection.Kind), PlanMode.Estimated);

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(Connection))
        {
            OnPropertyChanged(nameof(CanUseStatistics));
            OnPropertyChanged(nameof(CanUseActualPlan));
            OnPropertyChanged(nameof(ActualPlanTip));
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(ConnectionDescription));
        }
        else if (e.PropertyName == nameof(IsSwitching)) OnPropertyChanged(nameof(RunDisabledReason));
    }

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
            if (end > start)
                return ScriptSplitter.Split(text[start..end], kind)
                    .Select(s => s with { Start = s.Start + start, End = s.End + start }).ToList();   // offsets in the whole script
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

    private async Task RunAsync(IReadOnlyList<ScriptStatement> statements, PlanMode planRequest = PlanMode.None)
    {
        if (IsRunning)
        {
            _status.Info("A script is already running. Stop it first.");
            return;
        }
        if (IsGroupRunning)
        {
            _status.Info("A group run is in progress. Stop it first.");
            return;
        }
        if (IsSwitching)
        {
            _status.Info("Switching database. Try again in a moment.");
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
        RunSummary = null;
        _lastStatements = statements;
        _lastRunWasGroup = false;
        _lastPlanRequest = planRequest;
        _runText = Text;    // the result tabs point into this text
        _totalStatements = statements.Count;
        RunStartedAt = DateTime.Now;
        RunProgressText = statements.Count > 1 ? $"Running statement 1 of {statements.Count}" : "Running…";
        Results.Clear();
        ResultTabs.Clear();
        SelectedResult = null;
        SelectedResultTab = null;
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
            var kind = session.Config.Kind;
            var planMode = PlanCapture.Effective(kind, planRequest != PlanMode.None ? planRequest : ActualPlanEnabled ? PlanMode.Actual : PlanMode.None);
            _estimatedRun = planMode == PlanMode.Estimated;
            // An estimated plan runs nothing, so there is nothing to count or time.
            var statistics = StatisticsEnabled && kind == DbKind.SqlServer && !_estimatedRun;
            var options = new ScriptOptions(maxRows, session.Config.ReadOnly, kind, statistics, planMode);
            var plans = new List<(QueryPlan Plan, PlanStatement Statement)>();

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
            await foreach (var received in channel.Reader.ReadAllAsync(ct))
            {
                ran++;
                var (r, plan) = PlanCapture.Extract(received, kind, planMode);
                if (plan is not null)
                    foreach (var st in plan.Statements)
                    {
                        st.ScriptStatement = ran;
                        plans.Add((plan, st));
                    }

                // An estimated plan replaces the statement's tab: nothing ran, so it has no result to show.
                if (!(_estimatedRun && plan is not null))
                {
                    var vm = new StatementResultViewModel(ran, r, maxRows) { StartLine = LineOf(_runText, r.Statement.Start) };
                    Results.Add(vm);
                    ResultTabs.Add(vm);
                    SelectedResult = vm;
                }
                if (ran < _totalStatements) RunProgressText = $"Running statement {ran + 1} of {_totalStatements}";
            }

            if (await producer is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
            if (statistics && Results.Count > 0) AddStatisticsTabs();
            if (planMode != PlanMode.None && plans.Count > 0)
            {
                var planTab = new PlanResultTab(plans, planMode, kind) { Host = this };
                ResultTabs.Add(planTab);
                // Estimated: the plan is the answer. Actual: the rows stay in front, the plan is one click away.
                if (_estimatedRun || Results.Count == 0) SelectedResultTab = planTab;
            }
            else if (planMode != PlanMode.None && ran > 0 && Results.All(r => !r.HasError))
            {
                _status.Info("The server returned no plan for this statement.");
            }

            Report(ran, script.Reopened, script.ConnectionReset, plans.Count);
        }
        catch (OperationCanceledException)
        {
            _status.Info(ran == 0 ? "Cancelled." : $"Cancelled after {Plural(ran, "statement")}.");
            SetRunSummary(cancelled: true);
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
            RunStartedAt = null;
            RunProgressText = null;
            IsRunning = false;
        }
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";

    /// <summary>
    /// The line under the toolbar about a run that had several statements or was cancelled: how far it got, and what was not
    /// run. A single statement that worked or failed says everything in its own tab, so it gets none.
    /// </summary>
    private void SetRunSummary(bool cancelled)
    {
        var total = _totalStatements;
        var ran = Results.Count;
        var failed = Results.LastOrDefault(r => r.HasError);
        var ms = (long)Results.Sum(r => r.Duration.TotalMilliseconds);
        RunSummaryIsCancelled = cancelled;
        RunSummaryIsError = !cancelled && failed is not null;
        if (cancelled)
        {
            var notRun = total - ran;
            RunSummary = ran == 0
                ? "Run cancelled before the first statement finished."
                : $"Run cancelled after {ran} of {Plural(total, "statement")}" + (notRun > 0 ? $" · {notRun:N0} not run" : "") + ".";
        }
        else if (total <= 1)
        {
            RunSummary = null;
        }
        else if (_estimatedRun && failed is null)
        {
            RunSummary = $"Estimated plan for {Plural(total, "statement")} · nothing was run";
        }
        else if (failed is not null)
        {
            var notRun = total - ran;
            RunSummary = $"Stopped at statement {failed.Index} of {total:N0}" + (notRun > 0 ? $" · {notRun:N0} not run" : "")
                         + $" · {ms:N0} ms";
        }
        else
        {
            RunSummary = $"{Plural(total, "statement")} completed · {ms:N0} ms";
        }
    }

    /// <summary>Appends the Statistics and Messages tabs built from the messages of the statements that ran (the failed one included).</summary>
    private void AddStatisticsTabs()
    {
        var (stats, messages) = GridResultTab.Build(
            Results.Select(r => new StatementMessages(null, r.Index, r.Messages)).ToList());
        ResultTabs.Add(stats);
        ResultTabs.Add(messages);
    }

    public const string SessionResetNote =
        "(The connection of this tab was lost or reset: it was reopened, so open transactions, temp tables and variables are gone.)";

    private void Report(int ran, bool reopened, bool reset, int planCount = 0)
    {
        var note = reopened ? " " + SessionResetNote : "";
        if (Results.LastOrDefault(r => r.HasError) is { } failed)
        {
            SelectedResult = failed;
            _status.Error($"Statement {failed.Index} failed: {failed.Error}{note}");
        }
        else if (_estimatedRun)
        {
            _status.Info((planCount == 0 ? "No plan was returned." : planCount == 1 ? "Estimated plan ready, nothing was run." : $"Estimated plans ready for {planCount} statements, nothing was run.") + note);
        }
        else
        {
            var ms = (long)Results.Sum(r => r.Duration.TotalMilliseconds);
            _status.Info((ran == 1 ? Results[0].StatusText : $"{ran} statements completed in {ms:N0} ms.") + note);
        }

        SetRunSummary(cancelled: false);
        if (reset) ShowReset();
    }

    /// <summary>The connection was found unusable at the end of this run (aborted statement): said loudly, in this run's status.</summary>
    private void ShowReset()
    {
        ErrorMessage = ScriptConnection.ResetMessage;
        ErrorIsWarning = true;   // a caution: the statements may well have worked
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

    /// <summary>
    /// Ctrl+S: writes the script to <paramref name="path"/>, else to its file, else (never saved) to the library through the
    /// "Save script" dialog, or to the file the user picks when there is no library or the user asks for another location.
    /// </summary>
    [RelayCommand]
    public async Task SaveAsync(string? path = null)
    {
        path ??= FilePath;
        string? reserved = null;   // an empty library file taken for this save: removed again when the save does not happen
        if (path is null)
        {
            if (_hooks.Library is { } library && _hooks.Dialogs is { } dialogs)
            {
                var dialog = new SaveScriptViewModel(library, $"Script {_number}");
                if (!await dialogs.ShowSaveScriptAsync(dialog)) return;
                path = dialog.UseFilePicker ? null : dialog.SavedPath;
                reserved = path;
                if (path is null && !dialog.UseFilePicker) return;
            }
            if (path is null && !await PickSavePathAsync(p => path = p)) return;
        }
        try
        {
            await WriteAsync(path!);
        }
        finally
        {
            // Cancelled or failed after the dialog: do not leave an empty script behind in the library.
            if (reserved is not null && !string.Equals(FilePath, reserved, StringComparison.OrdinalIgnoreCase)) DropReservation(reserved);
        }
    }

    /// <summary>"Save as…": always asks for a file with the platform picker, then saves there (the tab follows the new file).</summary>
    [RelayCommand]
    public async Task SaveAsAsync()
    {
        string? path = null;
        if (!await PickSavePathAsync(p => path = p)) return;
        await WriteAsync(path!);
    }

    private async Task<bool> PickSavePathAsync(Action<string> chosen)
    {
        if (_hooks.Files is not { } files)
        {
            _status.Error("Cannot save: no file dialog available.");
            return false;
        }
        var picked = await files.PickSaveAsync(FilePath is { } current ? Path.GetFileName(current) : $"Script {_number}.sql");
        if (picked is null) return false;
        chosen(picked);
        return true;
    }

    private void DropReservation(string path)
    {
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: 0 }) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // an empty file stays: harmless
        }
    }

    private async Task WriteAsync(string path)
    {
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
        _hooks.FileUsed?.Invoke(path);
    }

    /// <summary>
    /// The file of this tab was renamed or moved (<paramref name="newPath"/>) or deleted (null): the tab follows it, so the next save
    /// goes to the file's new place instead of recreating the old one. After a delete the text stays as unsaved work.
    /// </summary>
    public void FollowFile(string? newPath)
    {
        FilePath = newPath;
        if (newPath is not null) return;
        _savedText = "";
        IsDirty = !string.IsNullOrWhiteSpace(Text);
        if (IsDirty) SchedulePersist();
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
        _hooks.FileUsed?.Invoke(path);
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
