using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using DbExplorer.Core.Upgrade;

namespace DbExplorer.App.ViewModels;

/// <summary>Where the Upgrade dialog is: choosing the sprint, previewing its scripts, confirming, running, or done.</summary>
public enum UpgradePhase { Setup, Preview, Confirm, Running, Finished }

/// <summary>One entry of the "or folder" list: a version folder with its script count, e.g. <c>v0860-sprint 143 (27 scripts)</c>.</summary>
public sealed record UpgradeFolderChoice(string Label, DbUpdateFolder Folder);

/// <summary>A warning of a script as a badge: its kind as text, the reason and line as the tooltip.</summary>
public sealed record UpgradeWarningBadge(string Text, string Tip);

/// <summary>A warning of a ticked script, as listed in the confirmation: <c>script: Kind - reason (line n)</c>.</summary>
public sealed record UpgradeWarningLine(string ScriptName, ScriptWarning Warning)
{
    public string Text => $"{ScriptName}: {Warning.Kind} - {Warning.Reason} (line {Warning.Line})";
}

/// <summary>How a run ended, for the colour and wording of the outcome banner.</summary>
public enum UpgradeOutcome { None, Success, Partial, Failed, Cancelled }

/// <summary>The scripts of one folder in the preview (the folder name is shown once, then its scripts).</summary>
public sealed record UpgradeFolderGroup(string Folder, IReadOnlyList<UpgradeScriptRow> Rows);

/// <summary>One script of a finished database, as listed under its "Details".</summary>
public sealed record UpgradeScriptLine(string Name, ScriptOutcomeState State, string Text)
{
    public bool IsDone => State == ScriptOutcomeState.Done;
    public bool IsFailed => State == ScriptOutcomeState.Failed;
    public bool IsOther => State is ScriptOutcomeState.Cancelled or ScriptOutcomeState.Skipped;
}

/// <summary>One script of the preview: its checkbox, its labels and its warnings.</summary>
public sealed partial class UpgradeScriptRow : ObservableObject
{
    private readonly Action? _changed;

    internal UpgradeScriptRow(UpgradeScriptItem item, Action? changed)
    {
        Item = item;
        _changed = changed;
        _isSelected = !item.Script.IsProduction && item.LoadError is null;
        WarningBadges = [.. item.Warnings.Select(w => new UpgradeWarningBadge(KindText(w.Kind), $"{w.Reason} (line {w.Line})"))];
    }

    internal UpgradeScriptItem Item { get; }

    /// <summary>The file name.</summary>
    public string Name => Item.Script.FileName;

    /// <summary>The version folder the file lives in.</summary>
    public string Folder => Item.Script.FolderName;

    /// <summary>Folder and file, as the run and the log name the script.</summary>
    public string DisplayName => Item.DisplayName;

    public bool IsProduction => Item.Script.IsProduction;

    /// <summary>The <c>Production&lt;n&gt;</c> label of a script that targets specific servers; null otherwise.</summary>
    public string? ProductionTag => Item.Script.ProductionTag;

    /// <summary>Ticked scripts are run. Production scripts start unticked; a script with a load error cannot be ticked.</summary>
    [ObservableProperty] private bool _isSelected;

    public IReadOnlyList<ScriptWarning> Warnings => Item.Warnings;

    public IReadOnlyList<UpgradeWarningBadge> WarningBadges { get; }

    /// <summary>The script carries at least one "Long-running" warning.</summary>
    public bool IsLongRunning => Item.Warnings.Any(w => w.Kind == ScriptWarningKind.LongRunning);

    /// <summary>One line with every reason, e.g. <c>Creates index IX_a (line 3)</c>.</summary>
    public string WarningText => string.Join("; ", Item.Warnings.Select(w => $"{w.Reason} (line {w.Line})"));

    /// <summary>Why the script cannot be played (unreadable, <c>GO n</c>); null when it can.</summary>
    public string? LoadError => Item.LoadError;

    public bool HasLoadError => Item.LoadError is not null;

    public bool CanSelect => Item.LoadError is null;

    partial void OnIsSelectedChanged(bool value) => _changed?.Invoke();

    private static string KindText(ScriptWarningKind kind) => kind == ScriptWarningKind.Index ? "Index" : "Long-running";
}

/// <summary>One database of the run: its state, the script it is on and the text of its status line.</summary>
public sealed partial class UpgradeMemberViewModel : ObservableObject
{
    private readonly Action<Guid>? _cancel;
    private bool _userCancelled;

    internal UpgradeMemberViewModel(ConnectionConfig connection, string name, Action<Guid>? cancel)
    {
        Id = connection.Id;
        Name = name;
        _cancel = cancel;
    }

    public Guid Id { get; }

    /// <summary>The connection name, made unique within the run ("name (2)").</summary>
    public string Name { get; }

    [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsRunning), nameof(IsDone), nameof(IsFailed), nameof(IsCancelled),
        nameof(CanCancel), nameof(StatusText))]
    [ObservableProperty] private MemberState _state = MemberState.Pending;

    /// <summary>1-based index of the script being played (the last one reached once the database ended); 0 while waiting.</summary>
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [ObservableProperty] private int _scriptIndex;

    [NotifyPropertyChangedFor(nameof(StatusText))]
    [ObservableProperty] private int _scriptCount;

    [NotifyPropertyChangedFor(nameof(StatusText))]
    [ObservableProperty] private string? _scriptName;

    [NotifyPropertyChangedFor(nameof(StatusText))]
    [ObservableProperty] private string? _error;

    /// <summary>Scripts that completed on this database.</summary>
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [ObservableProperty] private int _scriptsDone;

    [NotifyPropertyChangedFor(nameof(StatusText))]
    [ObservableProperty] private TimeSpan? _duration;

    public bool IsPending => State == MemberState.Pending;
    public bool IsRunning => State == MemberState.Running;
    public bool IsDone => State == MemberState.Done;
    public bool IsFailed => State == MemberState.Failed;
    public bool IsCancelled => State == MemberState.Cancelled;

    // ---- what the card shows while running and after ----

    /// <summary>0..1 through the scripts: the ones already played over all of them (1 once the database is done).</summary>
    public double ProgressValue => State == MemberState.Done ? 1 : ScriptCount > 0 ? Math.Clamp((ScriptIndex - 1.0) / ScriptCount, 0, 1) : 0;

    /// <summary>Running but the script count is not known yet: the bar is indeterminate.</summary>
    public bool IsIndeterminate => IsRunning && ScriptCount == 0;

    /// <summary><c>script 3 of 12</c>; empty before the first script.</summary>
    public string ProgressText => ScriptCount > 0 && ScriptIndex > 0 ? $"script {ScriptIndex} of {ScriptCount}" : "";

    /// <summary>The script being played, for the mono line under the name.</summary>
    public string CurrentScript => ScriptName ?? "";

    /// <summary>The database failed with a message: the card shows it in the error colour, selectable.</summary>
    public bool HasError => IsFailed && Error is not null;

    /// <summary>The failure as the status line words it (<c>failed at script: ...</c>).</summary>
    public string ErrorText => IsFailed ? FailedText() : "";

    /// <summary><c>12 scripts · 4.2 s</c> once done.</summary>
    public string DoneSummary => IsDone ? DoneText()["done - ".Length..].Replace(" - ", " · ") : "";

    /// <summary>The state as a word for the card's right edge.</summary>
    public string StateLabel => State switch
    {
        MemberState.Pending => "Waiting",
        MemberState.Running => "Running",
        MemberState.Done => "Done",
        MemberState.Cancelled => "Cancelled",
        _ => "Failed",
    };

    /// <summary>The scripts of the finished database with their outcome (listed under "Details").</summary>
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    [ObservableProperty] private IReadOnlyList<UpgradeScriptLine> _scriptLines = [];

    public bool HasDetails => ScriptLines.Count > 0;

    /// <summary>The "Details" list is open.</summary>
    [ObservableProperty] private bool _isDetailsOpen;

    /// <summary>Something to know about the database that is not an error (it was listed twice and ran once).</summary>
    [NotifyPropertyChangedFor(nameof(HasNote))]
    [ObservableProperty] private string? _note;

    public bool HasNote => Note is not null;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(State) or nameof(ScriptIndex) or nameof(ScriptCount) or nameof(ScriptName) or nameof(Error)
            or nameof(ScriptsDone) or nameof(Duration))
        {
            OnPropertyChanged(nameof(ProgressValue));
            OnPropertyChanged(nameof(IsIndeterminate));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(CurrentScript));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ErrorText));
            OnPropertyChanged(nameof(DoneSummary));
            OnPropertyChanged(nameof(StateLabel));
        }
    }

    /// <summary>The database has not ended: its Cancel button is live.</summary>
    public bool CanCancel => State is MemberState.Pending or MemberState.Running;

    /// <summary><c>waiting</c>, <c>running: script 3 of 12 - name</c>, <c>done - 12 scripts - 4.2 s</c>, <c>failed at script: error</c>, <c>cancelled</c>.</summary>
    public string StatusText => State switch
    {
        MemberState.Pending => "waiting",
        MemberState.Running => ScriptIndex > 0 ? $"running: script {ScriptIndex} of {ScriptCount} - {ScriptName}" : "running",
        MemberState.Done => DoneText(),
        MemberState.Cancelled => "cancelled",
        _ => FailedText(),
    };

    private string DoneText()
    {
        var text = $"done - {ScriptsDone} {(ScriptsDone == 1 ? "script" : "scripts")}";
        return Duration is { } d ? $"{text} - {d.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s" : text;
    }

    private string FailedText()
    {
        if (Error is null) return "failed";
        // The runner's error already starts with the script name ("script: batch k of n: message"); a connection failure does not.
        return ScriptName is not null && Error.StartsWith(ScriptName, StringComparison.Ordinal) ? $"failed at {Error}" : $"failed: {Error}";
    }

    /// <summary>Cancels this database only (a waiting one never starts, a running one is interrupted).</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancel?.Invoke(Id);

    partial void OnStateChanged(MemberState value) => CancelCommand.NotifyCanExecuteChanged();

    /// <summary>A progress report: advisory, ignored once the user cancelled this database (the final result stays authoritative).</summary>
    internal void Apply(UpgradeProgress p)
    {
        if (_userCancelled) return;
        State = p.State;
        ScriptIndex = p.ScriptIndex;
        ScriptCount = p.ScriptCount;
        ScriptName = p.ScriptName ?? ScriptName;
        Error = p.Error;
        if (p.State == MemberState.Done) ScriptsDone = p.ScriptIndex;
    }

    /// <summary>The Cancel button: the line says "cancelled" at once, without waiting for the runner.</summary>
    internal void MarkCancelled()
    {
        if (!CanCancel) return;
        _userCancelled = true;
        State = MemberState.Cancelled;
    }

    /// <summary>The runner's final word for this database (authoritative: overwrites everything).</summary>
    internal void Complete(UpgradeResult result)
    {
        _userCancelled = false;
        var reached = result.Scripts.Where(s => s.State != ScriptOutcomeState.Skipped).ToList();
        ScriptCount = result.Scripts.Count;
        ScriptIndex = reached.Count;
        ScriptName = reached.Count > 0 ? reached[^1].Name : null;
        ScriptsDone = result.Scripts.Count(s => s.State == ScriptOutcomeState.Done);
        Duration = result.Duration;
        Error = result.Error;
        State = result.State;
        Note = result.Note;
        ScriptLines = [.. result.Scripts.Select(s => new UpgradeScriptLine(s.Name, s.State, LineText(s)))];
    }

    private static string LineText(ScriptOutcome s) => s.State switch
    {
        ScriptOutcomeState.Done => $"{s.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s",
        ScriptOutcomeState.Failed => s.Error ?? "failed",
        ScriptOutcomeState.Cancelled => "cancelled",
        _ => "not run",
    };

    /// <summary>A failed database goes back to waiting for a retry: nothing of its first attempt stays on the card.</summary>
    internal void ResetForRetry()
    {
        _userCancelled = false;
        ScriptIndex = 0;
        ScriptCount = 0;
        ScriptName = null;
        Error = null;
        ScriptsDone = 0;
        Duration = null;
        Note = null;
        ScriptLines = [];
        IsDetailsOpen = false;
        State = MemberState.Pending;
    }

    /// <summary>The run was abandoned before this database reported its end.</summary>
    internal void Abandon()
    {
        if (!CanCancel) return;
        _userCancelled = true;
        State = MemberState.Cancelled;
    }
}

/// <summary>
/// The Upgrade dialog: choose a sprint (or one folder) and a target, preview the scripts with their warnings, confirm, then watch
/// the run database by database. <c>Setup -> Preview -> Confirm -> Running -> Finished</c>, all in one window. Everything that
/// touches the outside is injected (the folder catalog, the sessions, the clipboard), so the flow is testable; the write path is
/// the separate <see cref="UpgradeRunner"/>, never the read-only group run.
/// </summary>
public sealed partial class UpgradeViewModel : ObservableObject
{
    /// <summary>How long "Cancel all" lets the runner end by itself (cancelled databases end at once) before the run is abandoned.</summary>
    internal static TimeSpan CancelGrace { get; set; } = TimeSpan.FromSeconds(5);

    private readonly DbUpdateCatalog _catalog;
    private readonly Func<Guid, CancellationToken, Task<Session?>> _resolveSession;
    private readonly Func<string, Task>? _copyText;
    private readonly bool _requireSqlServer;
    private readonly Func<Func<ConnectionConfig, CancellationToken, Task<DbConnection>>, UpgradeRunner> _createRunner;

    private UpgradePlan? _plan;
    private UpgradeTarget? _confirmedTarget;
    private CancellationTokenSource? _runCts;
    private UpgradeRunner? _runner;
    private int _generation;
    private bool _ignoreProgress;

    /// <param name="catalog">The scanned dbupdate folder.</param>
    /// <param name="targets">Where the upgrade can be played: the current database first, then the groups of it.</param>
    /// <param name="resolveSession">Gives the live session of a connection (through the tree); null when it no longer exists.</param>
    /// <param name="copyText">Puts the log on the clipboard.</param>
    /// <param name="requireSqlServer">The scripts are T-SQL: the view model refuses other engines before anything is opened (off in tests that use SQLite files).</param>
    /// <param name="createRunner">Builds the runner from the connection factory; null = the production runner, which accepts SQL Server only (tests pass a SQLite-capable one).</param>
    public UpgradeViewModel(
        DbUpdateCatalog catalog,
        IReadOnlyList<UpgradeTarget> targets,
        Func<Guid, CancellationToken, Task<Session?>> resolveSession,
        Func<string, Task>? copyText = null,
        bool requireSqlServer = true,
        Func<Func<ConnectionConfig, CancellationToken, Task<DbConnection>>, UpgradeRunner>? createRunner = null)
    {
        _catalog = catalog;
        Targets = targets;
        _resolveSession = resolveSession;
        _copyText = copyText;
        _requireSqlServer = requireSqlServer;
        _createRunner = createRunner ?? (open => new UpgradeRunner(open));
        _selectedTarget = targets.FirstOrDefault();
        FolderChoices = [.. catalog.Folders.Reverse().Select(f =>
            new UpgradeFolderChoice($"{f.Name} ({f.Scripts.Count} {(f.Scripts.Count == 1 ? "script" : "scripts")})", f))];
    }

    // ---- setup ----

    [NotifyPropertyChangedFor(nameof(IsSetup), nameof(IsPreview), nameof(IsConfirm), nameof(IsRunning), nameof(IsFinished),
        nameof(ShowInput), nameof(ShowMembers), nameof(CanClose), nameof(HasWarnMessage), nameof(HasInfoMessage))]
    [ObservableProperty] private UpgradePhase _phase = UpgradePhase.Setup;

    [ObservableProperty] private string _sprintText = "";

    /// <summary>The version folders, newest first.</summary>
    public IReadOnlyList<UpgradeFolderChoice> FolderChoices { get; }

    [ObservableProperty] private UpgradeFolderChoice? _selectedFolder;

    public IReadOnlyList<UpgradeTarget> Targets { get; }

    [NotifyPropertyChangedFor(nameof(NeedsTypedConfirmation))]
    [ObservableProperty] private UpgradeTarget? _selectedTarget;

    /// <summary>The target list is only shown when there is a choice.</summary>
    public bool ShowTargets => Targets.Count > 1;

    /// <summary>The last thing the user should know: a bad input, a refusal, the outcome of the run.</summary>
    [NotifyPropertyChangedFor(nameof(HasWarnMessage), nameof(HasInfoMessage))]
    [ObservableProperty] private string? _message;

    /// <summary>The message is a problem or a refusal (warning banner) while the user is still choosing.</summary>
    public bool HasWarnMessage => Message is not null && Phase != UpgradePhase.Finished;

    /// <summary>The message is the outcome of the run (info banner).</summary>
    public bool HasInfoMessage => Message is not null && Phase == UpgradePhase.Finished;

    partial void OnSprintTextChanged(string value)
    {
        if (value.Length > 0) SelectedFolder = null;
    }

    partial void OnSelectedFolderChanged(UpgradeFolderChoice? value)
    {
        if (value is not null) SprintText = "";
    }

    public bool IsSetup => Phase == UpgradePhase.Setup;
    public bool IsPreview => Phase == UpgradePhase.Preview;
    public bool IsConfirm => Phase == UpgradePhase.Confirm;
    public bool IsRunning => Phase == UpgradePhase.Running;
    public bool IsFinished => Phase == UpgradePhase.Finished;

    /// <summary>The sprint and target pickers show before the run starts.</summary>
    public bool ShowInput => Phase is UpgradePhase.Setup or UpgradePhase.Preview;

    /// <summary>The per-database status shows while running and after.</summary>
    public bool ShowMembers => Phase is UpgradePhase.Running or UpgradePhase.Finished;

    /// <summary>The window may close: not while a run is in progress (Cancel all first).</summary>
    public bool CanClose => Phase != UpgradePhase.Running;

    partial void OnPhaseChanged(UpgradePhase value)
    {
        LoadCommand.NotifyCanExecuteChanged();
        RequestRunCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        ConfirmRunCommand.NotifyCanExecuteChanged();
        CancelAllCommand.NotifyCanExecuteChanged();
        RequestStopCommand.NotifyCanExecuteChanged();
        RequestRetryCommand.NotifyCanExecuteChanged();
        CopyLogCommand.NotifyCanExecuteChanged();
        RefreshLongToggle();
        if (value != UpgradePhase.Running)
        {
            ConfirmingStop = false;
            IsStopping = false;
        }
        foreach (var name in new[]
                 {
                     nameof(ChooseDone), nameof(ConfirmDone), nameof(InChoose), nameof(InConfirm), nameof(InRun), nameof(ShowRunReason),
                     nameof(RunDisabledReason), nameof(OutcomeIsSuccess), nameof(OutcomeIsWarn), nameof(OutcomeIsError), nameof(ShowOverall),
                     nameof(CanGoBack), nameof(ShowCopyLog), nameof(ShowRetry), nameof(RetryButtonText), nameof(RunDone), nameof(ShowStopButton), nameof(RunButtonText), nameof(IsDangerRun),
                 })
            OnPropertyChanged(name);
    }

    // ---- the steps of the dialog (the stepper in the header) ----

    /// <summary>Step 1 (scripts and target) is the current one.</summary>
    public bool InChoose => Phase is UpgradePhase.Setup or UpgradePhase.Preview;

    /// <summary>Step 2 (confirmation) is the current one.</summary>
    public bool InConfirm => Phase == UpgradePhase.Confirm;

    /// <summary>Step 3 (the run and its result) is the current one.</summary>
    public bool InRun => Phase is UpgradePhase.Running or UpgradePhase.Finished;

    public bool RunDone => Phase == UpgradePhase.Finished;

    public bool ChooseDone => Phase is UpgradePhase.Confirm or UpgradePhase.Running or UpgradePhase.Finished;
    public bool ConfirmDone => Phase is UpgradePhase.Running or UpgradePhase.Finished;

    // ---- how the run ended ----

    /// <summary>How the finished run ended: picks the colour and the icon of the outcome banner.</summary>
    [NotifyPropertyChangedFor(nameof(OutcomeIsSuccess), nameof(OutcomeIsWarn), nameof(OutcomeIsError))]
    [ObservableProperty] private UpgradeOutcome _outcome;

    private bool HasOutcomeMessage => Message is not null && Phase == UpgradePhase.Finished;

    public bool OutcomeIsSuccess => HasOutcomeMessage && Outcome == UpgradeOutcome.Success;
    public bool OutcomeIsWarn => HasOutcomeMessage && Outcome is UpgradeOutcome.Partial or UpgradeOutcome.Cancelled;
    public bool OutcomeIsError => HasOutcomeMessage && Outcome == UpgradeOutcome.Failed;

    private UpgradeOutcome OutcomeOfMembers()
    {
        var done = Members.Count(m => m.IsDone);
        var failed = Members.Count(m => m.IsFailed);
        if (failed > 0) return done > 0 ? UpgradeOutcome.Partial : UpgradeOutcome.Failed;
        return Members.Any(m => m.IsCancelled) ? UpgradeOutcome.Cancelled : UpgradeOutcome.Success;
    }

    // ---- the overall progress of the run ----

    private Stopwatch? _clock;
    private int _tickGeneration;

    /// <summary>Elapsed time of the run, <c>mm:ss</c> (updated every second while running).</summary>
    [ObservableProperty] private string _elapsedText = "00:00";

    /// <summary>The overall bar shows (while running and after).</summary>
    public bool ShowOverall => InRun;

    /// <summary><c>2 of 4 databases finished</c>.</summary>
    public string OverallText
    {
        get
        {
            var ended = Members.Count(m => !m.CanCancel);
            return $"{ended} of {Members.Count} {(Members.Count == 1 ? "database" : "databases")} finished";
        }
    }

    /// <summary>0..1 over all databases: finished ones count 1, running ones their own progress.</summary>
    public double OverallProgress => Members.Count == 0 ? 0 : Members.Sum(m => m.CanCancel ? m.ProgressValue : 1.0) / Members.Count;

    private void RefreshOverall()
    {
        OnPropertyChanged(nameof(OverallText));
        OnPropertyChanged(nameof(OverallProgress));
    }

    private async Task TickAsync(int generation)
    {
        while (generation == _tickGeneration && Phase == UpgradePhase.Running && _clock is { } clock)
        {
            ElapsedText = FormatElapsed(clock.Elapsed);
            try { await Task.Delay(500); }
            catch (Exception) { return; }
        }
    }

    private static string FormatElapsed(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    // ---- preview ----

    public ObservableCollection<UpgradeScriptRow> Rows { get; } = [];

    /// <summary><c>Ignored (not a .sql file): a, b</c> when the scan skipped files; null otherwise.</summary>
    [ObservableProperty] private string? _ignoredNote;

    /// <summary>Warnings of the ticked scripts, e.g. <c>3 warnings: 2 index, 1 long-running</c>; empty when none.</summary>
    [ObservableProperty] private string _warningSummary = "";

    /// <summary>Every warning of the ticked scripts.</summary>
    [ObservableProperty] private IReadOnlyList<UpgradeWarningLine> _selectedWarnings = [];

    /// <summary>What the user types to confirm a group run: the sprint number, or the folder code for a by-folder run.</summary>
    [NotifyPropertyChangedFor(nameof(ConfirmPrompt))]
    [ObservableProperty] private string _confirmToken = "";

    [NotifyPropertyChangedFor(nameof(ConfirmHeading))]
    [ObservableProperty] private int _selectedCount;

    [ObservableProperty] private int _productionSelectedCount;

    public bool HasRows => Rows.Count > 0;

    private UpgradeTarget? EffectiveTarget => _confirmedTarget ?? SelectedTarget;

    /// <summary>A group run is confirmed by typing the sprint number (or folder code).</summary>
    public bool NeedsTypedConfirmation => EffectiveTarget?.Group is not null || (_isRetry && RetryWarnings.Count > 0);

    public string ConfirmPrompt => $"Type {ConfirmToken} to confirm";

    public string ConfirmHeading => _isRetry
        ? $"Retrying {_retryPlan.Count} failed {(_retryPlan.Count == 1 ? "database" : "databases")}"
        : $"Upgrade {SelectedCount} {(SelectedCount == 1 ? "script" : "scripts")} on:";

    /// <summary>The databases of the target, as listed in the confirmation.</summary>
    [ObservableProperty] private IReadOnlyList<string> _confirmDatabases = [];

    [NotifyCanExecuteChangedFor(nameof(ConfirmRunCommand))]
    [NotifyPropertyChangedFor(nameof(ConfirmMatches))]
    [ObservableProperty] private string _confirmInput = "";

    partial void OnConfirmInputChanged(string value)
    {
        OnPropertyChanged(nameof(RunDisabledReason));
        OnPropertyChanged(nameof(ShowRunReason));
    }

    /// <summary>The typed confirmation matches (the green check beside the box).</summary>
    public bool ConfirmMatches => NeedsTypedConfirmation && string.Equals(ConfirmInput.Trim(), ConfirmToken, StringComparison.OrdinalIgnoreCase);

    private bool CanLoad() => Phase is UpgradePhase.Setup or UpgradePhase.Preview;

    /// <summary>Reads the scripts of the sprint (or folder) and shows them. Never throws; says why in <see cref="Message"/> when it cannot.</summary>
    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadAsync()
    {
        if (!CanLoad()) return;
        Message = null;

        IReadOnlyList<DbUpdateFolder> folders;
        string label, token;
        if (SelectedFolder is { } choice)
        {
            folders = [choice.Folder];
            label = $"Folder {choice.Folder.Name}";
            token = choice.Folder.Code;
        }
        else
        {
            if (!TryParseSprint(SprintText, out var sprint))
            {
                Reject("Enter a sprint number, for example 143.");
                return;
            }
            folders = _catalog.FoldersOfSprint(sprint);
            if (folders.Count == 0)
            {
                var nearest = _catalog.NearestSprints(sprint);
                Reject(nearest.Count == 0
                    ? "No sprint folders found."
                    : $"Sprint {sprint} was not found. Nearest sprints: {string.Join(", ", nearest)}.");
                return;
            }
            label = $"Sprint {sprint}";
            token = sprint.ToString(CultureInfo.InvariantCulture);
        }

        if (folders.Sum(f => f.Scripts.Count) == 0)
        {
            Reject(SelectedFolder is null ? $"{label} has no scripts." : $"{folders[0].Name} has no scripts.");
            return;
        }

        UpgradePlan plan;
        try
        {
            plan = await Task.Run(() => UpgradePlanner.Build(label, token, folders));
        }
        catch (Exception e)
        {
            Reject($"The scripts could not be read: {ErrorText.Describe(e)}");
            return;
        }

        _plan = plan;
        FilterText = "";   // a new list is shown whole
        ConfirmToken = plan.ConfirmToken;
        Rows.Clear();
        foreach (var item in plan.Items) Rows.Add(new UpgradeScriptRow(item, RecomputeSelection));
        Groups = [.. Rows.GroupBy(r => r.Folder).Select(g => new UpgradeFolderGroup(g.Key, [.. g]))];
        OnPropertyChanged(nameof(PlanLabel));
        OnPropertyChanged(nameof(HasRows));
        IgnoredNote = plan.Ignored.Count == 0 ? null : "Ignored (not a .sql file): " + string.Join(", ", plan.Ignored);
        RecomputeSelection();
        Phase = UpgradePhase.Preview;
        if (LoadErrorCount() is > 0 and var errors) Message = LoadErrorMessage(errors);
    }

    /// <summary>A failed load leaves no stale preview behind: nothing can be run from rows that do not match the input.</summary>
    private void Reject(string message)
    {
        _plan = null;
        Rows.Clear();
        Groups = [];
        FilterText = "";
        OnPropertyChanged(nameof(HasRows));
        IgnoredNote = null;
        RecomputeSelection();
        Phase = UpgradePhase.Setup;
        Message = message;
    }

    private static bool TryParseSprint(string text, out int sprint)
    {
        sprint = 0;
        var trimmed = text.Trim();
        return trimmed.Length > 0 && trimmed.All(char.IsAsciiDigit)
               && int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out sprint) && sprint >= 1;
    }

    private int LoadErrorCount() => Rows.Count(r => r.HasLoadError);

    private static string LoadErrorMessage(int count) =>
        $"{count} {(count == 1 ? "script cannot" : "scripts cannot")} be played (see the list): nothing can run until that is fixed.";

    private void RecomputeSelection()
    {
        var selected = Rows.Where(r => r.IsSelected).ToList();
        SelectedCount = selected.Count;
        ProductionSelectedCount = selected.Count(r => r.IsProduction);
        SelectedWarnings = [.. selected.SelectMany(r => r.Warnings.Select(w => new UpgradeWarningLine(r.DisplayName, w)))];

        var index = SelectedWarnings.Count(w => w.Warning.Kind == ScriptWarningKind.Index);
        var slow = SelectedWarnings.Count(w => w.Warning.Kind == ScriptWarningKind.LongRunning);
        var parts = new List<string>();
        if (index > 0) parts.Add($"{index} index");
        if (slow > 0) parts.Add($"{slow} long-running");
        var total = index + slow;
        WarningSummary = total == 0 ? "" : $"{total} {(total == 1 ? "warning" : "warnings")}: {string.Join(", ", parts)}";

        OnPropertyChanged(nameof(ConfirmHeading));
        OnPropertyChanged(nameof(TickedText));
        OnPropertyChanged(nameof(RunDisabledReason));
        OnPropertyChanged(nameof(ShowRunReason));
        OnPropertyChanged(nameof(RunButtonText));
        OnPropertyChanged(nameof(IsDangerRun));
        RequestRunCommand.NotifyCanExecuteChanged();
        RefreshLongToggle();
    }

    private bool CanRequestRun() => Phase == UpgradePhase.Preview && SelectedCount > 0 && Rows.All(r => !r.HasLoadError);

    /// <summary>Why the primary button is off: in Preview nothing ticked or an unreadable script, in Confirm the token is not typed yet; null when it is on.</summary>
    public string? RunDisabledReason
    {
        get
        {
            if (Phase == UpgradePhase.Confirm)
                return NeedsTypedConfirmation && !ConfirmMatches ? $"Type {ConfirmToken} in the box to enable the run." : null;
            if (Phase != UpgradePhase.Preview) return null;
            if (LoadErrorCount() is > 0 and var errors) return LoadErrorMessage(errors);
            return SelectedCount == 0 ? "Tick at least one script to continue." : null;
        }
    }

    public bool ShowRunReason => RunDisabledReason is not null;

    /// <summary><c>12 of 27 scripts ticked</c> above the script list.</summary>
    public string TickedText => $"{SelectedCount} of {Rows.Count} {(Rows.Count == 1 ? "script" : "scripts")} ticked";

    /// <summary>The scripts of the preview by version folder (the folder name once, then its files).</summary>
    [ObservableProperty] private IReadOnlyList<UpgradeFolderGroup> _groups = [];

    /// <summary>The plan's label (<c>Sprint 143</c>), for the confirmation summary.</summary>
    public string PlanLabel => _plan?.Label ?? "";

    /// <summary>The newest sprint of the catalog, as the placeholder of the sprint box.</summary>
    public string SprintHint => _catalog.Folders.Select(f => f.Sprint).Where(s => s.HasValue).Select(s => s!.Value).DefaultIfEmpty().Max() is > 0 and var sprint
        ? sprint.ToString(CultureInfo.InvariantCulture)
        : "143";

    /// <summary>Ticks every script that can be played.</summary>
    [RelayCommand]
    private void SelectAll() => SetTicks(r => r.CanSelect);

    /// <summary>Unticks every script.</summary>
    [RelayCommand]
    private void SelectNone() => SetTicks(_ => false);

    /// <summary>Ticks the scripts that do not target specific servers (the default after a load).</summary>
    [RelayCommand]
    private void SelectNonProduction() => SetTicks(r => r.CanSelect && !r.IsProduction);

    private void SetTicks(Func<UpgradeScriptRow, bool> want)
    {
        foreach (var row in Rows) row.IsSelected = want(row);
    }

    /// <summary>The confirmation button: a verb and what it does (<c>Run 9 scripts on 3 databases</c>).</summary>
    public string RunButtonText
    {
        get
        {
            if (_isRetry) return $"Retry {_retryPlan.Count} failed {(_retryPlan.Count == 1 ? "database" : "databases")}";
            var scripts = $"{SelectedCount} {(SelectedCount == 1 ? "script" : "scripts")}";
            var count = (_confirmedTarget ?? SelectedTarget)?.Members.DistinctBy(c => c.Id).Count() ?? 0;
            return count == 0 ? $"Run {scripts}" : $"Run {scripts} on {count} {(count == 1 ? "database" : "databases")}";
        }
    }

    /// <summary>The run touches several databases or Production scripts: the run button is the red one.</summary>
    public bool IsDangerRun => _isRetry || NeedsTypedConfirmation || ProductionSelectedCount > 0;

    /// <summary>Back is offered in the confirmation (it sits left of the primary button).</summary>
    public bool CanGoBack => Phase == UpgradePhase.Confirm;

    /// <summary>The log can be copied or saved (after the run).</summary>
    public bool ShowCopyLog => Phase == UpgradePhase.Finished;

    // ---- stopping a run: asked first, inside the window ----

    /// <summary>"Stop all" was pressed (or the window closed): the dialog asks before interrupting the databases.</summary>
    [NotifyPropertyChangedFor(nameof(ShowStopButton))]
    [ObservableProperty] private bool _confirmingStop;

    /// <summary>Stopping is under way (the databases are being interrupted).</summary>
    [NotifyPropertyChangedFor(nameof(ShowStopButton))]
    [ObservableProperty] private bool _isStopping;

    /// <summary>The "Stop all" button shows (not while asking, not while stopping).</summary>
    public bool ShowStopButton => IsRunning && !ConfirmingStop;

    private bool CanRequestStop() => Phase == UpgradePhase.Running && !IsStopping;

    /// <summary>Asks to stop the run: the window shows "Stop the upgrade?" with its buttons. Nothing is interrupted yet.</summary>
    [RelayCommand(CanExecute = nameof(CanRequestStop))]
    private void RequestStop()
    {
        if (Phase == UpgradePhase.Running) ConfirmingStop = true;
    }

    /// <summary>Keeps the run going after "Stop all?".</summary>
    [RelayCommand]
    private void KeepRunning() => ConfirmingStop = false;

    /// <summary>The user confirmed: every database is cancelled.</summary>
    [RelayCommand]
    private void ConfirmStop()
    {
        ConfirmingStop = false;
        CancelAll();
    }

    partial void OnMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(OutcomeIsSuccess));
        OnPropertyChanged(nameof(OutcomeIsWarn));
        OnPropertyChanged(nameof(OutcomeIsError));
    }

    partial void OnSelectedTargetChanged(UpgradeTarget? value)
    {
        OnPropertyChanged(nameof(RunButtonText));
        OnPropertyChanged(nameof(IsDangerRun));
    }

    /// <summary>
    /// "Run...": checks the ticks and the target (read-only and engine rules) and moves to the confirmation. A refusal says why in
    /// <see cref="Message"/>, stays in Preview and contacts nobody: no session is resolved, no database opened.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRequestRun))]
    private void RequestRun()
    {
        if (Phase != UpgradePhase.Preview) return;
        Message = null;

        if (LoadErrorCount() is > 0 and var errors) { Message = LoadErrorMessage(errors); return; }
        if (!Rows.Any(r => r.IsSelected)) { Message = "Tick at least one script to run."; return; }
        if (SelectedTarget is not { } target) { Message = "Choose where to run the upgrade."; return; }
        if (UpgradeRules.Refusal(target.Members, _requireSqlServer) is { } refusal) { Message = refusal; return; }

        _confirmedTarget = target;
        ConfirmInput = "";
        ConfirmDatabases = GroupRunViewModel.UniqueNames(target.Members.Select(m => m.Name));
        OnPropertyChanged(nameof(NeedsTypedConfirmation));
        OnPropertyChanged(nameof(ConfirmHeading));
        Phase = UpgradePhase.Confirm;
    }

    private bool CanBack() => Phase == UpgradePhase.Confirm;

    /// <summary>Confirm -> Preview, ticks kept; the confirmation of a retry goes back to the finished run instead.</summary>
    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back()
    {
        if (Phase != UpgradePhase.Confirm) return;
        ConfirmInput = "";
        if (_isRetry)
        {
            LeaveRetry();
            Message = _finishedMessage;
            Phase = UpgradePhase.Finished;
            return;
        }
        _confirmedTarget = null;
        OnPropertyChanged(nameof(NeedsTypedConfirmation));
        Phase = UpgradePhase.Preview;
    }

    // ---- retry the failed databases ----

    private sealed record RetryItem(ConnectionConfig Config, IReadOnlyList<UpgradeScript> Scripts);

    private sealed record RunBatch(IReadOnlyList<ConnectionConfig> Configs, IReadOnlyList<UpgradeScript> Scripts);

    private bool _isRetry;
    private IReadOnlyList<RetryItem> _retryPlan = [];
    private string? _finishedMessage;
    private UpgradeTarget? _runTarget;                      // the target of the first run (a retry is a subset of it)
    private List<UpgradeScript> _runScripts = [];           // the ticked scripts of the first run: a retry never changes them
    private readonly Dictionary<Guid, ConnectionConfig> _runConfigs = [];
    private readonly Dictionary<Guid, UpgradeResult> _results = [];
    private readonly Dictionary<Guid, HashSet<string>> _completed = [];   // scripts that ran to the end, per database, over all attempts
    private readonly HashSet<Guid> _stoppedMembers = [];    // cancelled one by one: their batch must not start them later

    /// <summary>The confirmation is for a retry of the failed databases, not for a first run.</summary>
    public bool IsRetry => _isRetry;

    /// <summary>What a retry does per database (<c>Globex: skips 2 scripts that already ran</c>), for the confirmation.</summary>
    [NotifyPropertyChangedFor(nameof(HasRetryLines))]
    [ObservableProperty] private IReadOnlyList<string> _retryLines = [];

    /// <summary>What a retry cannot be sure about (a script that ran in part, a result that was not recorded).</summary>
    [NotifyPropertyChangedFor(nameof(HasRetryWarnings))]
    [ObservableProperty] private IReadOnlyList<string> _retryWarnings = [];

    public bool HasRetryLines => RetryLines.Count > 0;
    public bool HasRetryWarnings => RetryWarnings.Count > 0;

    private int FailedCount => Members.Count(m => m.IsFailed);

    /// <summary>"Retry failed" shows only when the run ended and at least one database failed.</summary>
    public bool ShowRetry => Phase == UpgradePhase.Finished && FailedCount > 0 && _runTarget is not null;

    public string RetryButtonText => $"Retry {FailedCount} failed {(FailedCount == 1 ? "database" : "databases")}…";

    private bool CanRetry() => ShowRetry;

    /// <summary>
    /// Finished -> Confirm for the failed databases only, with the scripts that did not run to the end on each (scripts already
    /// applied there are skipped; a script that ran in part, or a database whose result was not recorded, is warned about and then
    /// needs the typed confirmation). Nothing runs until the user confirms again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void RequestRetry()
    {
        if (!ShowRetry || _runTarget is not { } runTarget) return;

        var plan = new List<RetryItem>();
        var lines = new List<string>();
        var warnings = new List<string>();
        foreach (var member in Members.Where(m => m.IsFailed))
        {
            if (!_runConfigs.TryGetValue(member.Id, out var config)) continue;
            IReadOnlyList<UpgradeScript> remaining;
            if (_completed.TryGetValue(member.Id, out var done))
            {
                remaining = [.. _runScripts.Where(x => !done.Contains(x.Name))];
                if (done.Count > 0)
                    lines.Add($"{member.Name}: skips {done.Count} {(done.Count == 1 ? "script" : "scripts")} that already ran; {remaining.Count} left to run.");
                foreach (var partial in _results.GetValueOrDefault(member.Id)?.Scripts.Where(x => x.State == ScriptOutcomeState.Failed && x.BatchesRun > 0) ?? [])
                    warnings.Add($"{member.Name}: {partial.Name} ran {partial.BatchesRun} {(partial.BatchesRun == 1 ? "batch" : "batches")} before it failed. It will run again from the start.");
            }
            else
            {
                remaining = _runScripts;   // no record of what ran there: the whole selection is kept
                warnings.Add($"Scripts that already ran on {member.Name} will run again: what it completed was not recorded.");
            }
            if (remaining.Count > 0) plan.Add(new RetryItem(config, remaining));
        }
        if (plan.Count == 0) return;

        _finishedMessage = Message;
        _isRetry = true;
        _retryPlan = plan;
        RetryLines = lines;
        RetryWarnings = warnings;
        var names = GroupRunViewModel.UniqueNames(plan.Select(i => i.Config.Name));
        _confirmedTarget = new UpgradeTarget(runTarget.Label, [.. plan.Select(i => i.Config)], runTarget.Group);
        ConfirmInput = "";
        ConfirmDatabases = names;
        Message = null;
        OnPropertyChanged(nameof(IsRetry));
        OnPropertyChanged(nameof(NeedsTypedConfirmation));
        OnPropertyChanged(nameof(ConfirmHeading));
        OnPropertyChanged(nameof(RunButtonText));
        OnPropertyChanged(nameof(IsDangerRun));
        Phase = UpgradePhase.Confirm;
    }

    private void LeaveRetry()
    {
        _isRetry = false;
        _retryPlan = [];
        _confirmedTarget = null;
        RetryLines = [];
        RetryWarnings = [];
        OnPropertyChanged(nameof(IsRetry));
        OnPropertyChanged(nameof(NeedsTypedConfirmation));
        OnPropertyChanged(nameof(ConfirmHeading));
        OnPropertyChanged(nameof(RunButtonText));
        OnPropertyChanged(nameof(IsDangerRun));
    }

    // ---- run ----

    public ObservableCollection<UpgradeMemberViewModel> Members { get; } = [];

    /// <summary>The copyable text log of the run, set when it ends.</summary>
    [ObservableProperty] private string _log = "";

    private bool CanConfirmRun() =>
        Phase == UpgradePhase.Confirm
        && (!NeedsTypedConfirmation || string.Equals(ConfirmInput.Trim(), ConfirmToken, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Plays the ticked scripts on the target, 4 databases at a time. The sessions are asked for here, on the calling (UI) thread
    /// (the tree's node state is UI state), then the run itself goes to the pool. Never throws.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConfirmRun))]
    private async Task ConfirmRunAsync()
    {
        if (!CanConfirmRun() || _confirmedTarget is not { } target) return;

        var retry = _isRetry;
        // A first run is one batch; a retry is one batch per distinct list of scripts left to run (databases that failed at the same
        // script share one), played one after the other. The same runner rules apply to each.
        List<RunBatch> batches;
        if (retry)
        {
            batches = [.. _retryPlan.GroupBy(i => string.Join("\n", i.Scripts.Select(x => x.Name)))
                .Select(g => new RunBatch([.. g.Select(i => i.Config)], g.First().Scripts))];
        }
        else
        {
            var distinct = target.Members.DistinctBy(c => c.Id).ToList();   // the runner plays a database listed twice once
            var ticked = Rows.Where(r => r.IsSelected && r.Item.Text is not null)
                .Select(r => new UpgradeScript(r.DisplayName, r.Item.Text!)).ToList();
            batches = [new RunBatch(distinct, ticked)];
        }
        var configs = batches.SelectMany(x => x.Configs).ToList();
        // The runner repeats this check; making it here too means a refusal contacts nobody at all.
        if (UpgradeRules.Refusal(configs, _requireSqlServer) is { } refusal)
        {
            if (retry)
            {
                LeaveRetry();
                Message = refusal;
                Phase = UpgradePhase.Finished;
            }
            else ReturnToPreview(refusal);
            return;
        }

        if (retry)
        {
            foreach (var config in configs) Members.First(m => m.Id == config.Id).ResetForRetry();
            LeaveRetry();
        }
        else
        {
            var names = GroupRunViewModel.UniqueNames(configs.Select(c => c.Name));
            Members.Clear();
            for (var i = 0; i < configs.Count; i++) Members.Add(new UpgradeMemberViewModel(configs[i], names[i], CancelMember));
            _runTarget = target;
            _runScripts = [.. batches[0].Scripts];
            _runConfigs.Clear();
            foreach (var config in configs) _runConfigs[config.Id] = config;
            _results.Clear();
            _completed.Clear();
        }
        _stoppedMembers.Clear();
        Log = "";
        Message = null;
        var generation = ++_generation;
        _ignoreProgress = false;
        using var cts = _runCts = new CancellationTokenSource();
        var ct = cts.Token;
        _clock = Stopwatch.StartNew();
        ElapsedText = "00:00";
        RefreshOverall();
        Phase = UpgradePhase.Running;
        _ = TickAsync(++_tickGeneration);

        // Created here, on the UI thread, so it posts back to it; it never throws and ignores reports of an ended run.
        var progress = new SafeProgress<UpgradeProgress>(p =>
        {
            if (generation == _generation && !_ignoreProgress) ApplyProgress(p);
        });

        var label = $"{_plan?.Label} on {(_runTarget ?? target).Label}" + (retry ? " (failed databases retried)" : "");
        Task<IReadOnlyList<UpgradeResult>>? work = null;
        var abandoned = false;
        try
        {
            foreach (var batch in batches)
            {
                if (ct.IsCancellationRequested) break;
                var runnable = batch.Configs.Where(c => !_stoppedMembers.Contains(c.Id)).ToList();   // cancelled one by one while waiting
                if (runnable.Count == 0) continue;

                // One resolve per database, started here on the UI thread.
                var sessions = new Dictionary<Guid, Task<Session?>>();
                foreach (var config in runnable) sessions.TryAdd(config.Id, StartResolve(config.Id, ct));
                // One runner per batch (concurrent runs on one instance are unsafe); it owns the connections the delegate hands out.
                var runner = _runner = _createRunner((config, token) => OpenMemberAsync(sessions, config, token));
                var batchWork = work = Task.Run(() => runner.RunAsync(runnable, batch.Scripts, new UpgradeRunOptions(), progress, ct), CancellationToken.None);
                IReadOnlyList<UpgradeResult>? results;
                try
                {
                    results = await batchWork.WaitAsync(ct);   // Cancel all returns control at once
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Cancelled databases end at once: wait a moment so the outcome of those that finished is shown. A database whose
                    // provider cannot interrupt its statement does not hold the dialog: the run is abandoned (the runner still
                    // disposes its connections when it ends).
                    var winner = await Task.WhenAny(batchWork, Task.Delay(CancelGrace));
                    results = winner == batchWork && batchWork.IsCompletedSuccessfully ? batchWork.Result : null;
                }

                if (generation != _generation) return;
                if (results is null)
                {
                    abandoned = true;
                    break;
                }
                foreach (var result in results)
                {
                    _results[result.Connection.Id] = result;
                    if (!_completed.TryGetValue(result.Connection.Id, out var done)) _completed[result.Connection.Id] = done = [];
                    foreach (var script in result.Scripts.Where(x => x.State == ScriptOutcomeState.Done)) done.Add(script.Name);
                    Members.FirstOrDefault(m => m.Id == result.Connection.Id)?.Complete(result);
                }
            }

            if (generation != _generation) return;
            _ignoreProgress = true;
            foreach (var member in Members) member.Abandon();   // a database whose batch never started (the run was stopped)
            if (abandoned)
            {
                Log = AbandonedLog(label);
                Message = "Upgrade cancelled: some databases were still stopping and may be partly upgraded.";
                Outcome = UpgradeOutcome.Cancelled;
                EndClock();
                Phase = UpgradePhase.Finished;
                return;
            }

            var recorded = Members.Select(m => _results.GetValueOrDefault(m.Id)).OfType<UpgradeResult>().ToList();
            Log = UpgradeLog.Format(label, recorded)
                  + string.Concat(Members.Where(m => !_results.ContainsKey(m.Id)).Select(m => $"\n== {m.Name}: {m.StatusText}"));
            Message = Summary();
            Outcome = OutcomeOfMembers();
            EndClock();
            Phase = UpgradePhase.Finished;
        }
        catch (UpgradeRefusedException refused)
        {
            _ignoreProgress = true;
            if (retry)
            {
                foreach (var member in Members) member.Abandon();
                Message = refused.Message;
                Outcome = UpgradeOutcome.Failed;
                EndClock();
                Phase = UpgradePhase.Finished;
            }
            else ReturnToPreview(refused.Message);
        }
        catch (Exception e)
        {
            _ignoreProgress = true;
            foreach (var member in Members) member.Abandon();
            Log = AbandonedLog(label);
            Message = $"The upgrade failed: {ErrorText.Describe(e)}";
            Outcome = UpgradeOutcome.Failed;
            EndClock();
            Phase = UpgradePhase.Finished;
        }
        finally
        {
            if (work is { IsCompleted: false }) _ = work.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (generation == _generation)
            {
                _runner = null;
                _runCts = null;
            }
        }
    }

    private void ReturnToPreview(string message)
    {
        Members.Clear();
        _confirmedTarget = null;
        OnPropertyChanged(nameof(NeedsTypedConfirmation));
        Phase = UpgradePhase.Preview;
        Message = message;
    }

    private string Summary()
    {
        var done = Members.Count(m => m.IsDone);
        var failed = Members.Count(m => m.IsFailed);
        var cancelled = Members.Count(m => m.IsCancelled);
        var text = $"{done} of {Members.Count} {(Members.Count == 1 ? "database" : "databases")} done";
        if (failed > 0) text += $", {failed} failed";
        if (cancelled > 0) text += $", {cancelled} cancelled";
        return text + ".";
    }

    private string AbandonedLog(string label) =>
        label + string.Concat(Members.Select(m => $"\n== {m.Name}: {m.StatusText}"));

    private void ApplyProgress(UpgradeProgress p)
    {
        Members.FirstOrDefault(m => m.Id == p.ConnectionId)?.Apply(p);
        RefreshOverall();
    }

    private void EndClock()
    {
        _clock?.Stop();
        if (_clock is { } clock) ElapsedText = FormatElapsed(clock.Elapsed);
        RefreshOverall();
    }

    private Task<Session?> StartResolve(Guid id, CancellationToken ct)
    {
        Task<Session?> task;
        try { task = _resolveSession(id, ct); }
        catch (Exception e) { task = Task.FromException<Session?>(e); }
        // A database the run no longer waits for (cancelled) may fail later: nobody awaits it, so observe that here.
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <summary>
    /// The runner's connection factory: waits for the session (honouring the token, so a connect that hangs can be cancelled),
    /// re-checks the LIVE config (the read-only flag may have been ticked since the preview) and opens a plain connection.
    /// </summary>
    private static async Task<DbConnection> OpenMemberAsync(
        Dictionary<Guid, Task<Session?>> sessions, ConnectionConfig config, CancellationToken token)
    {
        var session = await sessions[config.Id].WaitAsync(token).ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"{config.Name} no longer exists.");
        if (session.Config.ReadOnly)
            throw new InvalidOperationException($"{config.Name} is read-only: not run.");
        return await session.OpenConnectionAsync(token).ConfigureAwait(false);
    }

    private bool CanCancelAll() => Phase == UpgradePhase.Running;

    /// <summary>Cancel all: every database is cancelled; the run ends within <see cref="CancelGrace"/>. Safe at any time.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelAll))]
    private void CancelAll()
    {
        if (Phase == UpgradePhase.Running) IsStopping = true;
        try { _runCts?.Cancel(); }
        catch (ObjectDisposedException) { /* the run just ended */ }
        RequestStopCommand.NotifyCanExecuteChanged();
    }

    private void CancelMember(Guid id)
    {
        if (Phase != UpgradePhase.Running || _ignoreProgress) return;
        _stoppedMembers.Add(id);
        _runner?.CancelMember(id);
        Members.FirstOrDefault(m => m.Id == id)?.MarkCancelled();
        RefreshOverall();
    }

    private bool CanCopyLog() => Phase == UpgradePhase.Finished;

    /// <summary>Puts the log of the run on the clipboard.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyLog))]
    private async Task CopyLogAsync()
    {
        if (_copyText is null) return;
        try
        {
            await _copyText(Log);
        }
        catch (Exception e)
        {
            Message = $"The log could not be copied: {ErrorText.Describe(e)}";
        }
    }
}
