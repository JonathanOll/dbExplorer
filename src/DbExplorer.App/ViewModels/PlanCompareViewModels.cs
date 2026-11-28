using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Plans;

namespace DbExplorer.App.ViewModels;

/// <summary>A statement's plan kept as a side of a comparison: the pinned baseline, or another statement of the run.</summary>
public sealed record PlanCompareSource(string Label, QueryPlan Plan, PlanStatement Statement)
{
    /// <summary>"cost 12.4 · 1.5 s · 6 operators": tells two pinned plans apart in the menu.</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (Statement.HasCost) parts.Add("cost " + PlanFormat.Cost(Statement.TotalCost));
            if (Statement.ExecutionTimeMs is { } t) parts.Add(PlanFormat.Ms(t));
            var count = Statement.Nodes.Count();
            parts.Add(count == 1 ? "1 operator" : $"{count} operators");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>What the plan tab needs from the script tab that holds it: the pinned baseline, which survives a new run.</summary>
public interface IPlanCompareHost
{
    PlanCompareSource? PinnedPlan { get; }

    /// <summary>The plan tabs of the last run.</summary>
    IEnumerable<PlanResultTab> PlanTabs { get; }

    /// <summary>Keeps <paramref name="plan"/> as the baseline of later runs; null drops it.</summary>
    void PinPlan(PlanCompareSource? plan);
}

/// <summary>One line of the "Compare with" menu.</summary>
public sealed record PlanCompareChoice(string Label, string Detail, PlanCompareSource Source, bool IsPinned);

/// <summary>A figure of the header band: "Total cost 12.4 → 3.1 -75%", coloured and iconed by what it means.</summary>
public sealed record PlanCompareChip(string Name, string Before, string After, string Change, PlanVerdict Verdict, string IconKey)
{
    public bool IsBetter => Verdict == PlanVerdict.Better;

    public bool IsWorse => Verdict == PlanVerdict.Worse;

    /// <summary>A neutral move (row counts, operator count).</summary>
    public bool IsChanged => Verdict == PlanVerdict.Changed;

    public bool IsSame => Verdict == PlanVerdict.Same;

    public string Values => Before == After ? Before : $"{Before} → {After}";

    public string ToolTipText => $"{Name}: {Before} → {After}" + (Change.Length > 0 ? $" ({Change})" : "") + VerdictWord;

    public string VerdictWord => Verdict switch { PlanVerdict.Better => ", better", PlanVerdict.Worse => ", worse", PlanVerdict.Changed => ", changed", _ => ", no change" };

    public string AutomationName => ToolTipText;

    internal static PlanCompareChip Of(PlanMetricDelta m) => new(m.Name, m.BeforeText, m.AfterText, m.ChangeText, m.Verdict, IconOf(m));

    internal static string IconOf(PlanMetricDelta m) => m.Change switch { < 0 when m.Verdict != PlanVerdict.Same => "IconSortDown", > 0 when m.Verdict != PlanVerdict.Same => "IconSortUp", _ => "IconRemove" };
}

/// <summary>A line of the Differences list: a group header, or a name with its value before, after and the change.</summary>
public sealed record PlanDifferenceRow(string Name, string Before, string After, string Change, PlanVerdict Verdict, bool IsHeader)
{
    public bool IsEntry => !IsHeader;

    public bool IsBetter => Verdict == PlanVerdict.Better;

    public bool IsWorse => Verdict == PlanVerdict.Worse;

    public bool HasChange => Change.Length > 0;
}

/// <summary>
/// Two plans side by side: the baseline and the new one, the operators matched by <see cref="PlanComparer"/>. Selecting an operator
/// on one side selects its counterpart on the other and lists what differs between them.
/// </summary>
public sealed partial class PlanCompareViewModel : ObservableObject
{
    private bool _syncing;

    public PlanCompareViewModel(PlanCompareSource baseline, PlanCompareSource candidate, Action? close = null)
    {
        BaselineSource = baseline;
        CandidateSource = candidate;
        Diff = PlanComparer.Compare(baseline.Statement, candidate.Statement);
        Baseline = new PlanStatementViewModel(baseline.Plan, baseline.Statement, 1, 0);
        Candidate = new PlanStatementViewModel(candidate.Plan, candidate.Statement, 1, 0);
        CloseCommand = new RelayCommand(() => close?.Invoke());

        Decorate();
        Chips = Diff.Statement.Select(PlanCompareChip.Of).ToList();
        Summary = Diff.Summary;
        Overall = Diff.Overall;
        _stacked = Math.Max(Baseline.CanvasWidth, Candidate.CanvasWidth) > 520;

        Baseline.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlanStatementViewModel.SelectedNode)) Sync(Baseline, Candidate, fromBaseline: true); };
        Candidate.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlanStatementViewModel.SelectedNode)) Sync(Candidate, Baseline, fromBaseline: false); };
        SelectPair(FirstInteresting());
    }

    public PlanCompareSource BaselineSource { get; }

    public PlanCompareSource CandidateSource { get; }

    public PlanDiff Diff { get; }

    public PlanStatementViewModel Baseline { get; }

    public PlanStatementViewModel Candidate { get; }

    public string BaselineLabel => BaselineSource.Label;

    public string CandidateLabel => CandidateSource.Label;

    public string BaselineDetail => BaselineSource.Detail;

    public string CandidateDetail => CandidateSource.Detail;

    public IReadOnlyList<PlanCompareChip> Chips { get; }

    /// <summary>"Cost 12.4 → 3.1 (-75%); Clustered Index Scan on Orders replaced by Index Seek on Orders".</summary>
    public string Summary { get; }

    public PlanVerdict Overall { get; }

    public bool IsBetter => Overall == PlanVerdict.Better;

    public bool IsWorse => Overall == PlanVerdict.Worse;

    public bool IsNeutral => Overall is PlanVerdict.Same or PlanVerdict.Changed;

    public string OverallWord => Overall switch { PlanVerdict.Better => "Better", PlanVerdict.Worse => "Worse", _ => Diff.IsIdentical ? "Identical" : "Similar" };

    public int AddedCount => Diff.Added.Count();

    public int RemovedCount => Diff.Removed.Count();

    /// <summary>"3 operators got cheaper, 1 got dearer, 1 added, 0 removed": a legend for the badges on the cards.</summary>
    public string Legend
    {
        get
        {
            var better = Diff.Matched.Count(p => p.Verdict == PlanVerdict.Better);
            var worse = Diff.Matched.Count(p => p.Verdict == PlanVerdict.Worse);
            return $"{better} better · {worse} worse · {AddedCount} added · {RemovedCount} removed";
        }
    }

    public RelayCommand CloseCommand { get; }

    /// <summary>The two plans are stacked (baseline over the new one) rather than side by side: a plan is far wider than tall.</summary>
    [ObservableProperty] private bool _stacked;

    /// <summary>The list of differences is shown.</summary>
    [ObservableProperty] private bool _showDifferences = true;

    public bool IsSideBySide => !Stacked;

    partial void OnStackedChanged(bool value) => OnPropertyChanged(nameof(IsSideBySide));

    /// <summary>Zoom of both canvases, 0.25 to 2.</summary>
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    [ObservableProperty] private double _zoom = 1;

    public string ZoomText => $"{Zoom * 100:0}%";

    partial void OnZoomChanged(double value)
    {
        var clamped = Math.Clamp(Math.Round(value, 2), 0.25, 2);
        if (clamped != value) Zoom = clamped;
    }

    // ---- selection ----

    [ObservableProperty] private PlanNodePair? _selectedPair;

    public IReadOnlyList<PlanDifferenceRow> Differences { get; private set; } = [];

    public string DifferencesTitle { get; private set; } = "Differences";

    public string DifferencesSubtitle { get; private set; } = "";

    public bool HasSelection => SelectedPair is not null;

    /// <summary>Selects the operators of a pair on both sides.</summary>
    public void SelectPair(PlanNodePair? pair)
    {
        _syncing = true;
        try
        {
            Baseline.SelectedNode = pair?.Before is { } b ? Baseline.Nodes.FirstOrDefault(n => n.Node == b) : null;
            Candidate.SelectedNode = pair?.After is { } a ? Candidate.Nodes.FirstOrDefault(n => n.Node == a) : null;
        }
        finally { _syncing = false; }
        ShowPair(pair);
    }

    /// <summary>A click on a card: selects it on its own side, and its counterpart on the other.</summary>
    public void SelectNode(PlanNodeViewModel node)
    {
        if (Baseline.Nodes.Contains(node)) Baseline.SelectedNode = node;
        else if (Candidate.Nodes.Contains(node)) Candidate.SelectedNode = node;
    }

    private void Sync(PlanStatementViewModel from, PlanStatementViewModel to, bool fromBaseline)
    {
        if (_syncing) return;
        var pair = from.SelectedNode is { } node ? Diff.Find(node.Node) : null;
        _syncing = true;
        try
        {
            var other = fromBaseline ? pair?.After : pair?.Before;
            to.SelectedNode = other is null ? null : to.Nodes.FirstOrDefault(n => n.Node == other);
        }
        finally { _syncing = false; }
        ShowPair(pair);
    }

    private PlanNodePair? FirstInteresting() =>
        Diff.Matched.FirstOrDefault(p => p.Replaced)
        ?? Diff.Matched.Where(p => p.Verdict is PlanVerdict.Better or PlanVerdict.Worse)
            .OrderByDescending(p => Math.Abs(p.Primary?.Change ?? 0)).FirstOrDefault()
        ?? Diff.Added.Concat(Diff.Removed).FirstOrDefault()
        ?? Diff.Matched.FirstOrDefault();

    private void ShowPair(PlanNodePair? pair)
    {
        SelectedPair = pair;
        var rows = new List<PlanDifferenceRow>();
        if (pair is not null)
        {
            var before = pair.Before;
            var after = pair.After;
            rows.Add(new PlanDifferenceRow("Overview", "", "", "", PlanVerdict.Same, true));
            rows.Add(new PlanDifferenceRow("Name", before?.Name ?? "-", after?.Name ?? "-",
                pair.Kind switch { PlanChangeKind.Added => "added", PlanChangeKind.Removed => "removed", _ => pair.Replaced ? "replaced" : "" },
                pair.Kind == PlanChangeKind.Matched && !pair.Replaced ? PlanVerdict.Same : PlanVerdict.Changed, false));
            if (before?.Object is not null || after?.Object is not null)
                rows.Add(new PlanDifferenceRow("Object", Short(before?.Object), Short(after?.Object), before?.Object == after?.Object ? "" : "changed",
                    before?.Object == after?.Object ? PlanVerdict.Same : PlanVerdict.Changed, false));
            if (pair.Metrics.Count > 0)
            {
                rows.Add(new PlanDifferenceRow("Measures", "", "", "", PlanVerdict.Same, true));
                foreach (var m in pair.Metrics)
                    rows.Add(new PlanDifferenceRow(m.Name, m.BeforeText, m.AfterText, m.IsComparable ? m.ChangeText : "", m.Verdict, false));
            }
            foreach (var g in pair.Properties.GroupBy(p => p.Group))
            {
                rows.Add(new PlanDifferenceRow(g.Key, "", "", "", PlanVerdict.Same, true));
                foreach (var p in g)
                    rows.Add(new PlanDifferenceRow(p.Name, Trim(p.Before), Trim(p.After), p.Before is null ? "added" : p.After is null ? "removed" : "changed", PlanVerdict.Changed, false));
            }
        }
        Differences = rows;
        DifferencesTitle = pair is null ? "Differences" : PlanNodePair.Describe(pair.Any);
        DifferencesSubtitle = pair switch
        {
            null => "Select an operator to see what changed.",
            { Kind: PlanChangeKind.Added } => "Only in the new plan",
            { Kind: PlanChangeKind.Removed } => "Only in the baseline",
            { Replaced: true } => $"{PlanNodePair.Describe(pair.Before!)} replaced by {PlanNodePair.Describe(pair.After!)}",
            _ => pair.Verdict switch { PlanVerdict.Better => "Better than the baseline", PlanVerdict.Worse => "Worse than the baseline", _ => "About the same as the baseline" },
        };
        OnPropertyChanged(nameof(Differences));
        OnPropertyChanged(nameof(DifferencesTitle));
        OnPropertyChanged(nameof(DifferencesSubtitle));
        OnPropertyChanged(nameof(HasSelection));
    }

    private static string Short(string? obj) => obj is null ? "-" : PlanFormat.ShortObject(obj);

    private static string Trim(string? s) => s is null ? "-" : PlanNodeViewModel.Trim(s, 400);

    /// <summary>Puts the badge ("-64% cost"), the state and the tag on every card of both sides.</summary>
    private void Decorate()
    {
        foreach (var pair in Diff.Pairs)
        {
            if (pair.Before is { } b && Baseline.Nodes.FirstOrDefault(n => n.Node == b) is { } left) left.Compare = StateOf(pair, left: true);
            if (pair.After is { } a && Candidate.Nodes.FirstOrDefault(n => n.Node == a) is { } right) right.Compare = StateOf(pair, left: false);
        }
    }

    private static PlanCompareState StateOf(PlanNodePair pair, bool left)
    {
        switch (pair.Kind)
        {
            case PlanChangeKind.Added: return new PlanCompareState("added", "Added", null, "IconAdd");
            case PlanChangeKind.Removed: return new PlanCompareState("removed", "Removed", null, "IconRemove");
        }
        var p = pair.Primary;
        var badge = p is null || p.Verdict == PlanVerdict.Same ? null : BadgeText(p);
        var tag = pair.Replaced ? "Replaced" : null;
        var kind = p?.Verdict switch { PlanVerdict.Better => "better", PlanVerdict.Worse => "worse", _ => "same" };
        // The baseline side shows what the operator was, the new side what it became; both carry the badge so either card says it.
        return new PlanCompareState(kind, tag, badge, p is null ? null : PlanCompareChip.IconOf(p));
    }

    private static string BadgeText(PlanMetricDelta m) => m.Name switch
    {
        "Own cost" => m.ChangeText + " cost",
        "Own time" => m.ChangeText,
        "Logical reads" => m.ChangeText + " reads",
        _ => m.ChangeText,
    };
}

/// <summary>How a card looks in a comparison: its state class, an optional tag (Added, Removed, Replaced) and delta badge.</summary>
public sealed record PlanCompareState(string Kind, string? Tag, string? Badge, string? IconKey)
{
    public bool IsBetter => Kind == "better";

    public bool IsWorse => Kind == "worse";

    public bool IsAdded => Kind == "added";

    public bool IsRemoved => Kind == "removed";

    public bool HasTag => Tag is not null;

    public bool HasBadge => Badge is not null;

    /// <summary>The badge for screen readers and tooltips: the colour is never the only signal.</summary>
    public string Spoken => string.Join(", ", new[] { Tag, Badge, Kind is "better" or "worse" ? Kind : null }.Where(s => s is not null));
}
