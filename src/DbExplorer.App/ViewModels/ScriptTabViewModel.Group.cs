using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// Run on group (Ctrl+Shift+Enter: statement / selection, Ctrl+Shift+F5: whole script): the same text runs on every member of
/// the tab's group, in parallel, READ-ONLY for every member whatever its own flag says, and the results are shown member by
/// member with an All and a Differences view (<see cref="GroupRunViewModel"/>).
/// </summary>
/// <remarks>
/// Connection ownership (one coherent model): the tab owns NO member connection between runs. Every group run creates one
/// fresh dedicated <see cref="ScriptConnection"/> per member (through the session resolved by the tree) and hands it to the
/// <see cref="GroupRunner"/>, which owns it and disposes it as soon as that member has ended. So a member connection lives
/// exactly as long as its member's work: tab close cancels the run (the runner disposes as members end, and Close disposes
/// whatever is still tracked, a second Dispose is harmless), Disconnect of a member closes its session, which disposes its
/// registered dedicated connections (that member then reads "failed"), and a change of the group's membership affects only the
/// next run, which snapshots the members when it starts. The tab's own <c>_script</c> connection (manual transactions) is never
/// used by a group run: a group run can neither see nor disturb it.
/// </remarks>
public sealed partial class ScriptTabViewModel
{
    /// <summary>How long a "Cancel all" lets the runner finish by itself (cancelled members end at once) before the run is abandoned.</summary>
    internal static TimeSpan GroupCancelGrace { get; set; } = TimeSpan.FromSeconds(2);

    private readonly object _groupGate = new();
    private List<ScriptConnection> _groupConnections = [];
    private CancellationTokenSource? _groupCts;
    private GroupRunner? _groupRunner;
    private int _groupGeneration;

    /// <summary>The status panel and results of the latest group run (or its refusal); null until one ran, and again after an ordinary run.</summary>
    [ObservableProperty] private GroupRunViewModel? _groupRun;

    /// <summary>True while a group run is in progress: ordinary runs and database switches are refused.</summary>
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(IsBusy))]
    [ObservableProperty] private bool _isGroupRunning;

    /// <summary>An ordinary run can start (no run of any kind is active).</summary>
    public bool CanRun => !IsRunning && !IsGroupRunning;

    /// <summary>Something is running (an ordinary run or a group run): the progress bar and the Cancel button follow it.</summary>
    public bool IsBusy => IsRunning || IsGroupRunning;

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(RunDisabledReason));
    }

    partial void OnIsGroupRunningChanged(bool value) => OnPropertyChanged(nameof(RunDisabledReason));

    /// <summary>Closes the group run panel (the results of an ordinary run, if any, are shown again). Not while it still runs.</summary>
    [RelayCommand]
    public void CloseGroupRunPanel()
    {
        if (!IsGroupRunning) GroupRun = null;
    }

    /// <summary>The connections the latest group run opened (for tests: each one is disposed once its member ended).</summary>
    internal IReadOnlyList<ScriptConnection> GroupConnectionsOpened
    {
        get { lock (_groupGate) return [.. _groupConnections]; }
    }

    /// <summary>Ctrl+Shift+Enter: the selection / the statement under the caret, on every database of the group.</summary>
    [RelayCommand]
    public Task RunStatementOnGroupAsync() => RunOnGroupAsync(wholeScript: false);

    /// <summary>Ctrl+Shift+F5: the whole script on every database of the group.</summary>
    [RelayCommand]
    public Task RunScriptOnGroupAsync() => RunOnGroupAsync(wholeScript: true);

    /// <summary>Cancel all: every member is cancelled (the button, and Ctrl+Break while a group run is active). Safe at any time.</summary>
    [RelayCommand]
    public void CancelGroup()
    {
        try { _groupCts?.Cancel(); }
        catch (ObjectDisposedException) { /* the run just finished */ }
    }

    /// <summary>Cancels one member of the running group run; the others go on. No-op when none runs.</summary>
    public void CancelGroupMember(Guid connectionId) => GroupRun?.CancelMember(connectionId);

    /// <summary>
    /// Runs the statement under the caret / the selection (<paramref name="wholeScript"/> false, the rules of Ctrl+Enter) or the
    /// whole script on every member of the active group. Nothing runs, and nobody is contacted, when the tab is not in a
    /// group (it never falls back to an ordinary run: the user asked for a read-only comparison), when a switch, an ordinary
    /// run or another group run is active, when there is nothing to run, or when any statement writes. Never throws.
    /// </summary>
    public async Task RunOnGroupAsync(bool wholeScript)
    {
        if (_closed) return;
        if (!HasGroupSwitch || _hooks.Switch is not { } hooks)
        {
            _status.Info("This script's connection is not in a group with other databases: nothing was run.");
            return;
        }
        if (IsGroupRunning)
        {
            _status.Info("A group run is already in progress. Stop it first.");
            return;
        }
        if (IsRunning)
        {
            _status.Info("A script is already running. Stop it first.");
            return;
        }
        if (IsSwitching)
        {
            _status.Info("Switching database. Try again in a moment.");
            return;
        }
        if (_reconnecting)
        {
            _status.Info("Reconnecting. Try again when it has finished.");
            return;
        }

        var members = GroupMembers;   // the snapshot this run is about: later membership changes only affect the next run
        var kind = members[0].Kind;
        var statements = wholeScript ? ScriptSplitter.SplitForRun(Text, kind) : StatementsAtCaretOrSelection(kind);
        if (statements.Count == 0)
        {
            _status.Info("Nothing to run.");
            return;
        }

        // The pre-check says it nicely; the runner's own guard is the authoritative one and refuses the same run.
        if (statements.Any(s => ReadOnlyGuard.IsWrite(s.Text, kind)))
        {
            GroupRun = GroupRunViewModel.CreateRefused(GroupRunViewModel.ReadOnlyNotice);
            _status.Info(GroupRunViewModel.ReadOnlyNotice + ".");
            return;
        }

        IsGroupRunning = true;   // set before the first await: the caller sees it at once
        ErrorMessage = null;
        RunSummary = null;
        _lastRunWasGroup = true;
        _lastGroupWhole = wholeScript;
        _runText = Text;   // the member tabs point into this text
        RunStartedAt = DateTime.Now;
        RunProgressText = members.Count == 1 ? "Running on 1 database" : $"Running on {members.Count} databases";
        var generation = ++_groupGeneration;
        using var cts = _groupCts = new CancellationTokenSource();
        var ct = cts.Token;
        var maxRows = RowLimit;
        var statistics = StatisticsEnabled && CanUseStatistics;
        var run = GroupRun = new GroupRunViewModel(members, maxRows, CancelGroup, id => _groupRunner?.CancelMember(id));
        lock (_groupGate) _groupConnections = [];

        // One resolve per member, started here on the UI thread: it goes through the tree, whose node state is UI state.
        var sessions = members.ToDictionary(m => m.Id, m => StartResolve(hooks, m.Id, ct));
        // One runner per run (concurrent runs on one instance are unsafe); it owns the connections the delegate hands out.
        var runner = _groupRunner = new GroupRunner((config, token) => OpenMemberConnectionAsync(sessions, config, token));
        // Created here, on the UI thread, so it posts back to it; it never throws and ignores reports of an older run.
        var progress = new SafeProgress<MemberProgress>(p =>
        {
            if (generation == _groupGeneration && !_closed) run.Apply(p);
        });

        Task<IReadOnlyList<MemberResult>>? work = null;
        try
        {
            // Everything that touches a database starts on the pool: SQLite executes synchronously, so the UI thread must never
            // be the one that opens or runs it.
            work = Task.Run(
                () => runner.RunAsync(members, statements, new GroupRunOptions(maxRows, Statistics: statistics), progress, ct), CancellationToken.None);
            IReadOnlyList<MemberResult>? results;
            try
            {
                results = await work.WaitAsync(ct);   // Cancel all returns control at once, as for an ordinary run
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancelled members end at once: wait a moment so the results of those that finished are shown. A member whose
                // provider cannot interrupt its statement does not hold the panel: the run is abandoned (the runner still
                // disposes its connections when it ends, nothing else uses them).
                var winner = await Task.WhenAny(work, Task.Delay(GroupCancelGrace));
                results = winner == work && work.IsCompletedSuccessfully ? work.Result : null;
            }

            if (_closed) return;
            if (results is null)
            {
                run.Detach("Group run cancelled: some members were still stopping and are left out.");
                _status.Info("Group run cancelled.");
                return;
            }

            var prepared = await Task.Run(() => GroupRunViewModel.Prepare(results, statements.Count, maxRows, statistics), CancellationToken.None);
            if (_closed || generation != _groupGeneration) return;
            run.Complete(prepared);
            _status.Info(Summary(run));
        }
        catch (GroupRunRefusedException refused)
        {
            run.Refuse(refused.Message);
            _status.Info(refused.Message);
        }
        catch (Exception e) when (!_closed)
        {
            run.Fail($"The group run failed: {ErrorText.Describe(e)}");
            _status.Error($"The group run failed: {ErrorText.Describe(e)}");
        }
        catch (Exception)
        {
            // closed meanwhile: nothing to report
        }
        finally
        {
            if (work is { IsCompleted: false }) _ = work.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (generation == _groupGeneration)
            {
                _groupRunner = null;
                _groupCts = null;
                RunStartedAt = null;
                RunProgressText = null;
            }
            IsGroupRunning = false;
        }
    }

    private static string Summary(GroupRunViewModel run)
    {
        var done = run.Members.Count(m => m.IsDone);
        var failed = run.Members.Count(m => m.IsFailed);
        var cancelled = run.Members.Count(m => m.IsCancelled);
        var text = $"Group run: {done} of {run.Members.Count} databases done";
        if (failed > 0) text += $", {failed} failed";
        if (cancelled > 0) text += $", {cancelled} cancelled";
        return text + ".";
    }

    private static Task<Session?> StartResolve(GroupSwitchHooks hooks, Guid id, CancellationToken ct)
    {
        Task<Session?> task;
        try { task = hooks.ResolveSession(id, ct); }
        catch (Exception e) { task = Task.FromException<Session?>(e); }
        // A member the run no longer waits for (cancelled) may fail later: nobody awaits it, so observe that here.
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <summary>
    /// The <see cref="GroupRunner"/>'s <c>connectionFor</c>: a NEW dedicated connection per call (never the tab's own
    /// <c>_script</c>), which the runner owns and disposes when the member ends (see the remarks of this class). Waiting for
    /// the session honours <paramref name="token"/>, so a member whose connect hangs can be cancelled.
    /// </summary>
    private async Task<ScriptConnection> OpenMemberConnectionAsync(
        Dictionary<Guid, Task<Session?>> sessions, ConnectionConfig config, CancellationToken token)
    {
        var session = await sessions[config.Id].WaitAsync(token).ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"{config.Name} no longer exists.");
        token.ThrowIfCancellationRequested();
        var connection = new ScriptConnection(session);
        lock (_groupGate) _groupConnections.Add(connection);
        if (_closed)
        {
            connection.Dispose();   // the tab closed while connecting: nothing may stay open
            throw new OperationCanceledException(token);
        }
        return connection;
    }

    /// <summary>The tab is closed: cancels the group run and releases every connection it opened (the runner does too, as members end).</summary>
    private void CloseGroupRun()
    {
        CancelGroup();
        List<ScriptConnection> open;
        lock (_groupGate) open = [.. _groupConnections];
        foreach (var connection in open)
        {
            try { connection.Dispose(); }
            catch (Exception) { /* already released by the runner */ }
        }
    }
}
