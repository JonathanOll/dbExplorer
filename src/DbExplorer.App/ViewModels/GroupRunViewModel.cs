using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.ViewModels;

/// <summary>One line of the status panel of a group run: a member, its state, rows, time and error.</summary>
public sealed partial class MemberStatusViewModel : ObservableObject
{
    private readonly Action<Guid>? _cancel;
    private readonly Action<Guid>? _select;

    internal MemberStatusViewModel(ConnectionConfig connection, string name, Action<Guid>? cancel, Action<Guid>? select = null)
    {
        Id = connection.Id;
        Name = name;
        ConnectionName = connection.Name;
        _cancel = cancel;
        _select = select;
    }

    public Guid Id { get; }

    /// <summary>The name shown for the member: the connection name, made unique within the run ("name (2)").</summary>
    public string Name { get; }

    public string ConnectionName { get; }

    [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsRunning), nameof(IsDone), nameof(IsFailed), nameof(IsCancelled),
        nameof(CanCancel), nameof(StatusText), nameof(ShortStatusText), nameof(StateName), nameof(HasResultTab))]
    [ObservableProperty] private MemberState _state = MemberState.Pending;

    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ShortStatusText))]
    [ObservableProperty] private int? _rows;

    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ShortStatusText), nameof(Ms))]
    [ObservableProperty] private TimeSpan? _duration;

    [NotifyPropertyChangedFor(nameof(StatusText))]
    [ObservableProperty] private string? _error;

    public bool IsPending => State == MemberState.Pending;
    public bool IsRunning => State == MemberState.Running;
    public bool IsDone => State == MemberState.Done;
    public bool IsFailed => State == MemberState.Failed;
    public bool IsCancelled => State == MemberState.Cancelled;

    /// <summary>The member has not ended: its Cancel button is live.</summary>
    public bool CanCancel => State is MemberState.Pending or MemberState.Running;

    /// <summary>"waiting", "running…", "12 rows · 40 ms", the error, or "cancelled".</summary>
    public string StatusText => State switch
    {
        MemberState.Pending => "waiting",
        MemberState.Running => "running…",
        MemberState.Done => $"{RowsText} · {MsText} ms",
        MemberState.Cancelled => "cancelled",
        _ => Error ?? "failed",
    };

    /// <summary>The line on the member's chip: short, the full error is in the tooltip and in the member's tab.</summary>
    public string ShortStatusText => State switch
    {
        MemberState.Pending => "Waiting…",
        MemberState.Running => "Running…",
        MemberState.Done => $"{RowsText} · {MsText} ms",
        MemberState.Cancelled => "Cancelled",
        _ => "Failed",
    };

    /// <summary>The state as a word (spoken name of the chip).</summary>
    public string StateName => State switch
    {
        MemberState.Pending => "waiting",
        MemberState.Running => "running",
        MemberState.Done => "done",
        MemberState.Cancelled => "cancelled",
        _ => "failed",
    };

    /// <summary>The member has ended, so it has a result tab to open.</summary>
    public bool HasResultTab => State is MemberState.Done or MemberState.Failed or MemberState.Cancelled;

    /// <summary>Opens the member's own result tab (a click on its chip).</summary>
    [RelayCommand(CanExecute = nameof(HasResultTab))]
    private void Select() => _select?.Invoke(Id);

    private string RowsText => Rows is 1 ? "1 row" : $"{(Rows ?? 0).ToString("N0", CultureInfo.InvariantCulture)} rows";

    /// <summary>Time spent on the member in milliseconds, once it ended; null before.</summary>
    public long? Ms => Duration is { } d ? (long)d.TotalMilliseconds : null;

    private string MsText => (Ms ?? 0).ToString(CultureInfo.InvariantCulture);

    /// <summary>Cancels this member only (a pending one never starts, a running one is interrupted).</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancel?.Invoke(Id);

    partial void OnStateChanged(MemberState value)
    {
        CancelCommand.NotifyCanExecuteChanged();
        SelectCommand.NotifyCanExecuteChanged();
    }

    internal void Set(MemberState state, int? rows, TimeSpan? duration, string? error)
    {
        State = state;
        Rows = rows;
        Duration = duration;
        Error = error;
    }
}

/// <summary>A tab of the results of a group run: "All" (the merged rows) or the result of one member.</summary>
public sealed class GroupResultTab(GroupRunViewModel run, string title, StatementResultViewModel? member, GridResultTab? grid = null)
{
    public GroupRunViewModel Run { get; } = run;

    public string Title { get; } = title;

    /// <summary>The member's own result; null for the "All" tab.</summary>
    public StatementResultViewModel? Member { get; } = member;

    /// <summary>The Statistics or Messages grid of a run with statistics on; null for the other tabs.</summary>
    public GridResultTab? Grid { get; } = grid;

    public bool IsAll => Member is null && Grid is null;

    public bool IsGrid => Grid is not null;

    public bool IsMember => Member is not null;

    public bool HasError => Member?.HasError == true;

    /// <summary>Spoken name of the tab, with a failure said in words.</summary>
    public string AutomationName => HasError ? $"Failed: {Title}" : Title;
}

/// <summary>
/// The state of a group run shown below the editor: one status line per member, one result tab per member plus an "All" tab
/// that merges the rows (leading Database column) or, with <see cref="ShowDifferences"/>, only the rows that differ.
/// The run itself is driven by <see cref="ScriptTabViewModel.RunOnGroupAsync"/>. Progress reports are advisory (they may
/// arrive late, from another thread's post); <see cref="Complete"/> is authoritative and overwrites every status line.
/// </summary>
public sealed partial class GroupRunViewModel : ObservableObject
{
    public const string ReadOnlyNotice = "Group runs are read-only; run writes on one database at a time";

    private Prepared? _prepared;
    private bool _detached;

    public GroupRunViewModel(IReadOnlyList<ConnectionConfig> members, int maxRows, Action? cancelAll = null, Action<Guid>? cancelMember = null)
    {
        MaxRows = maxRows;
        _cancelAll = cancelAll;
        _cancelMember = cancelMember;
        var names = UniqueNames(members.Select(m => m.Name));
        for (var i = 0; i < members.Count; i++)
        {
            var member = new MemberStatusViewModel(members[i], names[i], CancelMember, SelectMember);
            member.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MemberStatusViewModel.StatusText)) OnPropertyChanged(nameof(SummaryText));
            };
            Members.Add(member);
        }
        IsRunning = members.Count > 0;
    }

    private readonly Action? _cancelAll;
    private readonly Action<Guid>? _cancelMember;

    public int MaxRows { get; }

    public ObservableCollection<MemberStatusViewModel> Members { get; } = new();

    /// <summary>The result of each member for the compared statement (one entry per member, failed ones carry their error).</summary>
    public ObservableCollection<StatementResultViewModel> PerMemberResults { get; } = new();

    /// <summary>"All" first, then one tab per member.</summary>
    public ObservableCollection<GroupResultTab> Tabs { get; } = new();

    [ObservableProperty] private GroupResultTab? _selectedTab;

    /// <summary>True from the start of the run until <see cref="Complete"/> / <see cref="Detach"/> / a refusal.</summary>
    [NotifyPropertyChangedFor(nameof(CanShowDifferences), nameof(DifferencesTip), nameof(SummaryText))]
    [ObservableProperty] private bool _isRunning;

    /// <summary>Everything that needs the user's attention about this run (one line per notice), or null.</summary>
    [ObservableProperty] private string? _notice;

    /// <summary>The notices that are about the comparison itself ("compares statement 3", "identical rows"): an info band.</summary>
    [ObservableProperty] private IReadOnlyList<string> _infoNotices = [];

    /// <summary>The notices that say something was left out or refused: a warning band.</summary>
    [ObservableProperty] private IReadOnlyList<string> _warnNotices = [];

    public bool HasInfoNotices => InfoNotices.Count > 0;

    public bool HasWarnNotices => WarnNotices.Count > 0;

    partial void OnNoticeChanged(string? value)
    {
        var lines = value is null
            ? []
            : value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        InfoNotices = lines.Where(IsInfoLine).ToList();
        WarnNotices = lines.Where(l => !IsInfoLine(l)).ToList();
        OnPropertyChanged(nameof(HasInfoNotices));
        OnPropertyChanged(nameof(HasWarnNotices));
    }

    private static bool IsInfoLine(string line) =>
        (line.StartsWith("The script has ", StringComparison.Ordinal) && line.Contains("compare statement", StringComparison.Ordinal))
        || line.Contains("returned identical rows", StringComparison.Ordinal);

    /// <summary>"3 of 3 databases done · 24 ms", or how many have finished while the run goes on.</summary>
    public string SummaryText
    {
        get
        {
            var total = Members.Count;
            var ended = Members.Count(m => !m.CanCancel);
            if (IsRunning) return $"{ended} of {total} databases finished";
            var text = $"{Members.Count(m => m.IsDone)} of {total} databases done";
            if (Members.Count(m => m.IsFailed) is > 0 and var failed) text += $" · {failed} failed";
            if (Members.Count(m => m.IsCancelled) is > 0 and var cancelled) text += $" · {cancelled} cancelled";
            var slowest = Members.Where(m => m.Ms is not null).Select(m => m.Ms!.Value).DefaultIfEmpty(-1).Max();
            return slowest >= 0 ? text + $" · {slowest.ToString("N0", CultureInfo.InvariantCulture)} ms" : text;
        }
    }

    /// <summary>The "All" tab shows every row (the counterpart of <see cref="ShowDifferences"/>, for the segmented control).</summary>
    public bool ShowAllRows
    {
        get => !ShowDifferences;
        set => ShowDifferences = !value;
    }

    /// <summary>How many rows differ across the members; null until the run is complete or when fewer than two members returned rows.</summary>
    public int? DifferenceCount => _prepared is { Differences.Included: >= 2 } p ? p.Differences.Result.Rows.Count : null;

    /// <summary>The members returned identical rows (nothing differs and nothing was cut at the row limit).</summary>
    public bool IsIdentical => _prepared is { Differences.Identical: true };

    /// <summary>"4 differ", "Identical" or null: the chip next to the segmented control.</summary>
    public string? DifferenceChipText => IsIdentical ? "Identical" : DifferenceCount is { } n and > 0 ? $"{n:N0} differ" : null;

    public bool HasDifferenceChip => DifferenceChipText is not null;

    /// <summary>Selects the result tab of a member (a click on its chip).</summary>
    public void SelectMember(Guid id)
    {
        if (Members.FirstOrDefault(m => m.Id == id) is not { } member) return;
        if (Tabs.FirstOrDefault(t => t.IsMember && t.Title == member.Name) is { } tab) SelectedTab = tab;
    }

    /// <summary>The "All" tab shows only the rows that are not identical across the members, with a "Present in" column.</summary>
    [ObservableProperty] private bool _showDifferences;

    /// <summary>What the "All" tab shows now: the merged rows, or the differences (see <see cref="ShowDifferences"/>).</summary>
    [ObservableProperty] private MergedResult? _all;

    [ObservableProperty] private IReadOnlyList<ResultColumn> _allColumns = [];

    [ObservableProperty] private IReadOnlyList<object?[]> _allRows = [];

    [ObservableProperty] private string _allStatus = "";

    public bool HasResults => Tabs.Count > 0;

    public bool CanShowDifferences => !IsRunning && _prepared is not null;

    /// <summary>The tooltip of the "Differences" segment: what it does, or why it is off.</summary>
    public string DifferencesTip =>
        IsRunning ? "Available once every database has finished"
        : _prepared is null ? "Nothing to compare: the run ended without results"
        : "Only the rows that are not identical in all databases, with the databases that have them";

    partial void OnShowDifferencesChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowAllRows));
        ShowView();
    }

    /// <summary>The run was refused before anything was contacted: no member runs, the notice says why.</summary>
    public static GroupRunViewModel CreateRefused(string notice) =>
        new([], ScriptTabViewModel.DefaultRowLimit) { Notice = notice };

    // ---- progress and completion ----

    /// <summary>Applies a progress report to its member line. Ignored once the run is complete or detached.</summary>
    public void Apply(MemberProgress progress)
    {
        if (_detached || !IsRunning) return;
        if (Members.FirstOrDefault(m => m.Id == progress.ConnectionId) is not { } member) return;
        member.Set(progress.State, progress.Rows ?? member.Rows, progress.Duration ?? member.Duration, progress.Error);
    }

    /// <summary>
    /// Cancels one member (its Cancel button): the runner is told, and the line says "cancelled" at once, without waiting for
    /// the runner's report (which stays authoritative). The other members go on.
    /// </summary>
    public void CancelMember(Guid id)
    {
        if (_detached || !IsRunning) return;
        _cancelMember?.Invoke(id);
        MarkMemberCancelled(id);
    }

    private void MarkMemberCancelled(Guid id)
    {
        if (Members.FirstOrDefault(m => m.Id == id) is { CanCancel: true } member)
            member.Set(MemberState.Cancelled, null, null, null);
    }

    /// <summary>
    /// The run was abandoned (Cancel all and the runner did not end in time): members that had not ended read "cancelled" and
    /// late reports are ignored. The runner still disposes its connections when it ends.
    /// </summary>
    public void Detach(string? notice = null)
    {
        _detached = true;
        foreach (var member in Members.Where(m => m.CanCancel)) member.Set(MemberState.Cancelled, null, null, null);
        IsRunning = false;
        if (notice is not null) Notice = notice;
        OnPropertyChanged(nameof(CanShowDifferences));
        OnPropertyChanged(nameof(DifferencesTip));
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>The run failed as a whole (not a member): shown as a notice, the members that did not end read "cancelled".</summary>
    public void Fail(string message) => Detach(message);

    /// <summary>The run was refused by the runner (mixed kinds, nothing to run, a write): no member ran.</summary>
    public void Refuse(string message)
    {
        _detached = true;
        Members.Clear();
        IsRunning = false;
        Notice = message;
    }

    /// <summary>Everything <see cref="Complete(Prepared)"/> shows; pure data, built off the UI thread.</summary>
    public sealed record Prepared(
        IReadOnlyList<(Guid Id, MemberState State, int? Rows, TimeSpan Duration, string? Error)> Outcomes,
        IReadOnlyList<(string Title, StatementResultViewModel Result)> MemberResults,
        AllView Merged,
        AllView Differences,
        GridResultTab? Statistics = null,
        GridResultTab? Messages = null);

    /// <summary>What the "All" tab shows in one mode: columns, rows and the notices that go with it.</summary>
    public sealed record AllView(MergedResult Result, IReadOnlyList<string> Notices, string Status, int Included = 0, bool Identical = false);

    /// <summary>
    /// Merges and compares the results of the members. Pure: no UI state is touched, so it can run on the pool.
    /// Only members that Succeeded with the compared statement and returned columns feed Merge and Differences; the others
    /// are left out and named in the notices. The compared statement is the last one of the script that returned columns
    /// for at least one member (the only one, for Ctrl+Shift+Enter): the same statement for every member.
    /// </summary>
    public static Prepared Prepare(IReadOnlyList<MemberResult> results, int statementCount, int maxRows, bool statistics = false)
    {
        var names = UniqueNames(results.Select(r => r.Connection.Name));
        var compared = ComparedStatement(results, statementCount);

        var outcomes = new List<(Guid, MemberState, int?, TimeSpan, string?)>();
        var tabs = new List<(string, StatementResultViewModel)>();
        var included = new List<MemberSet>();
        var exclusions = new List<string>();
        var truncated = new List<string>();

        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var name = names[i];
            var rows = r.Statements.Sum(s => s.Sets.Sum(set => set.Rows.Count));
            outcomes.Add((r.Connection.Id, r.State, rows, r.Duration, r.Error));

            var statement = PickStatement(r, compared);
            tabs.Add((name, new StatementResultViewModel(compared + 1, statement, maxRows, name)));

            if (r.State == MemberState.Failed)
                exclusions.Add($"{name} failed and is left out of All and Differences.");
            else if (r.State == MemberState.Cancelled)
                exclusions.Add($"{name} was cancelled and is left out of All and Differences.");
            else if (r.State == MemberState.Done && r.Statements.Count > compared
                     && r.Statements[compared] is { Error: null } ok && ok.Sets.Count > 0 && ok.Sets[0].Columns.Count > 0)
            {
                included.Add(new MemberSet(name, ok.Sets[0]));
                if (ok.Sets[0].Truncated) truncated.Add(name);
            }
            else
                exclusions.Add($"{name} returned no columns for the compared statement and is left out of All and Differences.");
        }

        var shared = new List<string>();
        if (statementCount > 1)
            shared.Add($"The script has {statementCount} statements: All and Differences compare statement {compared + 1}.");
        shared.AddRange(exclusions);

        // Statistics: the messages of EVERY statement of every member (also a failed or cancelled one: what ran before), one
        // table with a leading Database column so the IO of the databases can be compared.
        GridResultTab? statsTab = null, messagesTab = null;
        if (statistics)
        {
            var sources = new List<StatementMessages>();
            for (var i = 0; i < results.Count; i++)
                for (var s = 0; s < results[i].Statements.Count; s++)
                    sources.Add(new StatementMessages(names[i], s + 1, results[i].Statements[s].Messages ?? []));
            (statsTab, messagesTab) = GridResultTab.Build(sources);
        }

        return new Prepared(
            outcomes, tabs,
            Build(included, shared, truncated, differences: false, maxRows),
            Build(included, shared, truncated, differences: true, maxRows),
            statsTab, messagesTab);
    }

    private static AllView Build(List<MemberSet> included, List<string> shared, List<string> truncated, bool differences, int maxRows)
    {
        var notices = new List<string>(shared);
        MergedResult result;
        if (included.Count == 0)
        {
            notices.Add("No member returned rows to compare.");
            result = new MergedResult([differences ? GroupResults.PresentInColumn : GroupResults.DatabaseColumn], [], [], false);
        }
        else
        {
            result = differences ? GroupResults.Differences(included) : GroupResults.Merge(included);
            notices.AddRange(result.Warnings);
            if (result.Columns.Count <= 1)
            {
                notices.Add("The members have no column in common: there is nothing to show.");
                result = result with { Rows = [] };
            }
            else if (differences)
            {
                foreach (var name in truncated)
                    notices.Add($"{name}: rows were cut at the row limit ({maxRows}); differences may be false.");
                if (included.Count < 2)
                    notices.Add("Only one member returned rows: there is nothing to compare it with.");
                else if (result.Rows.Count == 0 && truncated.Count == 0)
                    notices.Add(included.Count == 2
                        ? "The 2 members returned identical rows."
                        : $"All {included.Count} members returned identical rows.");
            }
            else
            {
                foreach (var name in truncated) notices.Add($"{name}: showing only the first {maxRows} rows (row limit).");
            }
        }

        var databases = included.Count == 1 ? "1 database" : $"{included.Count} databases";
        var status = differences
            ? $"{Rows(result.Rows.Count)} that differ across {databases}"
            : $"{Rows(result.Rows.Count)} from {databases}";
        var identical = differences && included.Count >= 2 && result.Rows.Count == 0 && truncated.Count == 0 && result.Columns.Count > 1;
        return new AllView(result, notices, status, included.Count, identical);
    }

    /// <summary>Shows a prepared outcome: overwrites every status line (authoritative), builds the tabs and the All view.</summary>
    public void Complete(Prepared prepared)
    {
        _prepared = prepared;
        _detached = false;
        foreach (var (id, state, rows, duration, error) in prepared.Outcomes)
            Members.FirstOrDefault(m => m.Id == id)?.Set(state, rows, duration, error);

        PerMemberResults.Clear();
        Tabs.Clear();
        Tabs.Add(new GroupResultTab(this, "All", null));
        foreach (var (title, result) in prepared.MemberResults)
        {
            PerMemberResults.Add(result);
            Tabs.Add(new GroupResultTab(this, title, result));
        }
        if (prepared.Statistics is { } statsTab) Tabs.Add(new GroupResultTab(this, statsTab.Header, null, statsTab));
        if (prepared.Messages is { } messagesTab) Tabs.Add(new GroupResultTab(this, messagesTab.Header, null, messagesTab));

        IsRunning = false;
        ShowView();
        SelectedTab = Tabs[0];
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(CanShowDifferences));
        OnPropertyChanged(nameof(DifferencesTip));
        OnPropertyChanged(nameof(DifferenceCount));
        OnPropertyChanged(nameof(IsIdentical));
        OnPropertyChanged(nameof(DifferenceChipText));
        OnPropertyChanged(nameof(HasDifferenceChip));
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>Convenience for tests and callers that are already on the UI thread.</summary>
    public void Complete(IReadOnlyList<MemberResult> results, int statementCount, bool statistics = false) =>
        Complete(Prepare(results, statementCount, MaxRows, statistics));

    private void ShowView()
    {
        if (_prepared is not { } prepared) return;
        var view = ShowDifferences ? prepared.Differences : prepared.Merged;
        All = view.Result;
        AllColumns = view.Result.Columns.Select(c => new ResultColumn(c)).ToList();
        AllRows = view.Result.Rows;
        AllStatus = view.Status;
        Notice = view.Notices.Count == 0 ? null : string.Join(Environment.NewLine, view.Notices);
    }

    /// <summary>Cancel all: every member is cancelled (the runner ends them at once; see ScriptTabViewModel.CancelGroup).</summary>
    [RelayCommand]
    private void CancelAll() => _cancelAll?.Invoke();

    // ---- helpers ----

    /// <summary>
    /// Display names that are unique within the run (case-insensitive): a repeated name gets " (2)", " (3)"…
    /// (<see cref="MemberSet.Database"/> is the key Merge and Differences use to say where a row is).
    /// </summary>
    public static IReadOnlyList<string> UniqueNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        // A name that really is in the list is never given to another entry as its suffixed form.
        var original = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(list.Count);
        foreach (var name in list)
        {
            if (taken.Add(name))
            {
                result.Add(name);
                continue;
            }
            for (var n = 2; ; n++)
            {
                var candidate = $"{name} ({n})";
                if (original.Contains(candidate) || !taken.Add(candidate)) continue;
                result.Add(candidate);
                break;
            }
        }
        return result;
    }

    private static int ComparedStatement(IReadOnlyList<MemberResult> results, int statementCount)
    {
        for (var i = statementCount - 1; i >= 0; i--)
            if (results.Any(r => r.State == MemberState.Done && r.Statements.Count > i
                                 && r.Statements[i].Sets is { Count: > 0 } sets && sets[0].Columns.Count > 0))
                return i;
        return Math.Max(0, statementCount - 1);
    }

    /// <summary>The member's result for the compared statement, else the statement that failed, else a stand-in saying why there is none.</summary>
    private static StatementResult PickStatement(MemberResult member, int compared)
    {
        if (member.Statements.Count > compared) return member.Statements[compared];
        if (member.Statements.LastOrDefault(s => s.Error is not null) is { } failed) return failed;
        var error = member.State switch
        {
            MemberState.Cancelled => "Cancelled.",
            MemberState.Failed => member.Error ?? "Failed.",
            _ => member.Error,
        };
        return new StatementResult(new ScriptStatement("", 0, 0), [], null, member.Duration, error);
    }

    private static string Rows(long n) => n == 1 ? "1 row" : $"{n.ToString("N0", CultureInfo.InvariantCulture)} rows";
}

/// <summary>
/// An <see cref="IProgress{T}"/> that never throws and runs its handler on the UI thread: it captures the synchronization
/// context it was created on and posts to it. Without a context (tests, no UI) the handler runs inline, one at a time.
/// The runner calls progress from the thread pool and does not guard against a throwing callback.
/// </summary>
internal sealed class SafeProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly object _gate = new();

    public void Report(T value)
    {
        try
        {
            if (_context is null) Invoke(value);
            else _context.Post(_ => Invoke(value), null);
        }
        catch (Exception)
        {
            // Progress is advisory: the final result of the run is authoritative.
        }
    }

    private void Invoke(T value)
    {
        try
        {
            lock (_gate) handler(value);
        }
        catch (Exception)
        {
            // A failing handler must never escape into the runner (or crash the UI thread).
        }
    }
}
