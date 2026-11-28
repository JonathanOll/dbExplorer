using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Model;
using DbExplorer.Core.Plans;

namespace DbExplorer.App.ViewModels;

/// <summary>What the operators are coloured by.</summary>
public enum PlanMetric { Cost, Time, Rows }

/// <summary>The family of an operator: picks its icon and tile colour.</summary>
public static class PlanCategory
{
    public static string Of(PlanNode n)
    {
        var name = n.Name.ToLowerInvariant();
        var logical = (n.Logical ?? "").ToLowerInvariant();
        if (name.Contains("lookup")) return "lookup";
        if (name.Contains("insert") || name.Contains("update") || name.Contains("delete") || name.Contains("modifytable") || name.Contains("merge") && !name.Contains("join"))
            return "modify";
        if (name.Contains("sort") || name.Contains("temp b-tree")) return "sort";
        if (name.Contains("aggregate") || logical.Contains("aggregate") || name == "group" || name == "windowagg" || name == "window") return "aggregate";
        if (name.Contains("join") || name.Contains("nested loop") || logical.Contains("join") || name.Contains("hash match")) return "join";
        if (name.Contains("parallel") || name.StartsWith("gather") || name.Contains("repartition") || name.Contains("distribute")) return "parallel";
        if (name.Contains("seek") || name.Contains("primary key") || name.Contains("bitmap") || name.Contains("index only")) return "seek";
        // PostgreSQL's "Index Scan" walks the index by key (a seek); SQL Server's and SQLite's read it whole (a scan).
        if (name.Contains("index scan")) return n.Properties.Any(p => p.Name == "Node type") ? "seek" : "scan";
        if (name.Contains("scan")) return "scan";
        if (name.Contains("filter") || name.Contains("compute") || name.Contains("limit") || name.Contains("top") || name.Contains("project")
            || name.Contains("result") || name.Contains("sequence") || name.Contains("concat") || name.Contains("append") || name.Contains("spool")
            || name.Contains("materialize") || name.Contains("hash") || name.Contains("unique") || name.Contains("set"))
            return "compute";
        return "other";
    }
}

/// <summary>One operator as the canvas shows it: its box, its labels, how hot it is.</summary>
public sealed partial class PlanNodeViewModel : ObservableObject
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public PlanNodeViewModel(PlanNode node, PlanBox box, bool hasCost)
    {
        Node = node;
        Box = box;
        Category = PlanCategory.Of(node);
        Title = node.Name;
        Subtitle = node.Object ?? node.Logical;
        HasCost = hasCost;
        CostText = !hasCost ? null : node.CostShare <= 0 ? "0%" : node.CostShare < 0.005 ? "<1%" : (node.CostShare * 100).ToString("0", Inv) + "%";
        RowsText = RowsLine(node);
        TimeText = node.OwnTimeMs is { } t ? FormatMs(t) : null;
        HasWarning = node.Warnings.Count > 0;
        HasHint = node.Hint is not null;
        ToolTipText = BuildToolTip(node, CostText);
        AutomationName = $"{node.Name}{(node.Object is null ? "" : " on " + node.Object)}{(CostText is null ? "" : ", cost " + CostText)}";
    }

    public PlanNode Node { get; }

    public PlanBox Box { get; }

    public double X => Box.X;

    public double Y => Box.Y;

    public double Width => Box.Width;

    public double Height => Box.Height;

    public string Category { get; }

    /// <summary>Icon resource key of the operator's family.</summary>
    public string IconKey => Category switch
    {
        "scan" => "IconTable",
        "seek" => "IconSearch",
        "lookup" => "IconKey",
        "join" => "IconCompare",
        "aggregate" => "IconCount",
        "sort" => "IconSortDown",
        "parallel" => "IconGroup",
        "compute" => "IconSettings",
        "modify" => "IconSave",
        _ => "IconSchema",
    };

    public string Title { get; }

    public string? Subtitle { get; }

    public bool HasCost { get; }

    /// <summary>"32%": the operator's own share of the statement cost; null when the engine reports no cost.</summary>
    public string? CostText { get; }

    public string? RowsText { get; }

    public string? TimeText { get; }

    public bool HasWarning { get; }

    public bool HasHint { get; }

    /// <summary>The info mark: a hint but no warning (the warning mark takes the corner otherwise).</summary>
    public bool ShowHintOnly => HasHint && !HasWarning;

    public string ToolTipText { get; }

    public string AutomationName { get; }

    /// <summary>0 to 1: how much of the metric this operator takes; the cost bar's length.</summary>
    [NotifyPropertyChangedFor(nameof(IsHigh), nameof(IsMid), nameof(IsLow))]
    [ObservableProperty] private double _heat;

    /// <summary>"none", "low", "mid" or "high": the colour class of the cost bar.</summary>
    [NotifyPropertyChangedFor(nameof(IsHigh), nameof(IsMid), nameof(IsLow))]
    [ObservableProperty] private string _heatClass = "none";

    public bool IsHigh => HeatClass == "high";

    public bool IsMid => HeatClass == "mid";

    public bool IsLow => HeatClass == "low";

    [ObservableProperty] private bool _isSelected;

    /// <summary>The operator is on the path from the root to the selected one (its edge is lit).</summary>
    [ObservableProperty] private bool _isOnPath;

    /// <summary>Thickness of the edge to the parent, from the rows it carries.</summary>
    public double EdgeThickness { get; init; } = 1.5;

    /// <summary>Rows on the edge to the parent ("5,000", "~50"), null when unknown.</summary>
    public string? EdgeLabel { get; init; }

    public PlanNodeViewModel? Parent { get; internal set; }

    /// <summary>Set when the plan is shown in a comparison: the card's badge, tag and outline.</summary>
    public PlanCompareState? Compare { get; set; }

    internal static string FormatMs(double ms) =>
        ms >= 10_000 ? $"{ms / 1000:0.#} s" : ms >= 100 ? $"{ms:0} ms" : ms >= 1 ? $"{ms:0.#} ms" : $"{ms:0.###} ms";

    internal static string Count(double v) => v >= 1e7 ? $"{v / 1e6:0.#}M" : v.ToString("N0", Inv);

    private static string? RowsLine(PlanNode n)
    {
        if (n.ActualRows is { } a)
        {
            var text = Count(a) + (a == 1 ? " row" : " rows");
            if (n.EstimatedRows is { } e) text += $" · est {Count(e)}";
            return text;
        }
        return n.EstimatedRows is { } est ? "~" + Count(est) + (est == 1 ? " row" : " rows") : null;
    }

    private static string BuildToolTip(PlanNode n, string? cost)
    {
        var lines = new List<string> { n.Logical is { } l ? $"{n.Name} ({l})" : n.Name };
        if (n.Object is { } o) lines.Add(o);
        if (cost is not null) lines.Add($"Cost: {cost} of the statement (own {ShowFormat(n.OwnCost)}, with children {ShowFormat(n.SubtreeCost)})");
        if (n.ActualRows is { } a)
            lines.Add($"Rows: {Count(a)} actual" + (n.EstimatedRows is { } e ? $", {Count(e)} estimated" : "") + (n.Executions is { } x ? $", {Count(x)} execution{(x == 1 ? "" : "s")}" : ""));
        else if (n.EstimatedRows is { } er) lines.Add($"Rows: {Count(er)} estimated");
        if (n.OwnTimeMs is { } t) lines.Add($"Time: {FormatMs(t)} in this operator ({FormatMs(n.ActualTimeMs ?? t)} with children)");
        foreach (var p in n.Properties.Where(p => p.Group == "Predicates").Take(2))
            lines.Add($"{p.Name}: {Trim(p.Value, 180)}");
        foreach (var w in n.Warnings) lines.Add("⚠ " + w.Title + (w.Detail is null ? "" : ": " + Trim(w.Detail, 160)));
        if (n.Hint is { } h) lines.Add("→ " + h);
        return string.Join("\n", lines);
    }

    private static string ShowFormat(double v) => v == 0 ? "0" : v < 0.001 ? v.ToString("0.#####E+0", Inv) : v.ToString("0.####", Inv);

    internal static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>A line of the properties panel: a group header, or a name and its value.</summary>
public sealed record PlanPropertyRow(string Name, string Value, bool IsHeader)
{
    public bool IsEntry => !IsHeader;
}

/// <summary>A problem listed in the Issues tab: a warning, a hint, a missing index.</summary>
public sealed record PlanIssue(string Kind, string Title, string? Detail, PlanNodeViewModel? Node, string? Script = null)
{
    public bool IsWarning => Kind == "warn";

    public bool IsHintKind => Kind == "hint";

    public bool IsIndex => Kind == "index";

    public bool HasScript => Script is not null;

    public bool HasNode => Node is not null;
}

/// <summary>One line of the Top operators tab.</summary>
public sealed record PlanTopRow(int Rank, PlanNodeViewModel Node, string Share, string Rows)
{
    public string Name => Node.Title;

    public string? Object => Node.Subtitle;
}

/// <summary>One statement of a plan: its operators placed on the canvas, and what is selected in it.</summary>
public sealed partial class PlanStatementViewModel : ObservableObject
{
    public PlanStatementViewModel(QueryPlan plan, PlanStatement statement, int number, double batchShare)
    {
        Plan = plan;
        Statement = statement;
        Number = number;
        BatchShare = batchShare;
        Layout = PlanLayout.Compute(statement.Root);

        var byNode = new Dictionary<PlanNode, PlanNodeViewModel>();
        foreach (var box in Layout.Boxes)
        {
            var n = box.Node;
            var rows = n.ActualRows ?? n.EstimatedRows;
            var thickness = !statement.HasCost || rows is null ? 1.5 : Math.Clamp(1 + Math.Log10(1 + rows.Value) * 1.1, 1, 8);
            var label = rows is null ? null : (n.ActualRows is null ? "~" : "") + Compact(rows.Value);
            byNode[n] = new PlanNodeViewModel(n, box, statement.HasCost) { EdgeThickness = thickness, EdgeLabel = label };
        }
        foreach (var vm in byNode.Values)
            if (vm.Node.Parent is { } p && byNode.TryGetValue(p, out var parent)) vm.Parent = parent;
        Nodes = byNode.Values.OrderBy(v => v.Node.Id).ToList();

        Top = statement.TopOperators(12)
            .Select((n, i) => new PlanTopRow(i + 1, byNode[n], ShareText(n, statement), n.ActualRows is { } a ? PlanNodeViewModel.Count(a) : n.EstimatedRows is { } e ? "~" + PlanNodeViewModel.Count(e) : ""))
            .ToList();

        var issues = new List<PlanIssue>();
        foreach (var w in statement.Warnings) issues.Add(new PlanIssue("warn", w.Title, w.Detail, null));
        foreach (var m in statement.MissingIndexes)
            issues.Add(new PlanIssue("index", $"Missing index on {m.Schema}.{m.Table} · saves about {m.ImpactPercent:0.#}%", m.CreateScript, null, m.CreateScript));
        foreach (var vm in Nodes)
        {
            foreach (var w in vm.Node.Warnings) issues.Add(new PlanIssue("warn", $"{vm.Title}: {w.Title}", w.Detail, vm));
            if (vm.Node.Hint is { } h) issues.Add(new PlanIssue("hint", $"{vm.Title}{(vm.Subtitle is null ? "" : " on " + vm.Subtitle)}", h, vm));
        }
        Issues = issues;

        ApplyMetric(statement.HasCost ? PlanMetric.Cost : PlanMetric.Rows);
        // Opens on the costliest operator: the first thing to look at (the statement's own properties close the list).
        // Without cost (SQLite): the first operator with a hint, else the first one below the root.
        SelectedNode = Top.Count > 0 && Top[0].Node.Heat > 0 ? Top[0].Node
            : Nodes.FirstOrDefault(n => n.HasHint)
              ?? (statement.Root is { Children.Count: > 0 } r ? Nodes.FirstOrDefault(n => n.Node == r.Children[0]) : null)
              ?? Nodes.FirstOrDefault(n => n.Node == statement.Root);
    }

    /// <summary>Short row count for the arrows: "840", "84K", "1.2M".</summary>
    internal static string Compact(double v) =>
        v >= 1e6 ? $"{v / 1e6:0.#}M" : v >= 1e5 ? $"{v / 1e3:0}K" : v >= 1e4 ? $"{v / 1e3:0.#}K" : v.ToString("N0", CultureInfo.InvariantCulture);

    public QueryPlan Plan { get; }

    public PlanStatement Statement { get; }

    /// <summary>1-based position among the statements of the tab.</summary>
    public int Number { get; }

    /// <summary>This statement's share of the cost of all statements of the run, 0 to 1.</summary>
    public double BatchShare { get; }

    public PlanLayout Layout { get; }

    public double CanvasWidth => Layout.Width;

    public double CanvasHeight => Layout.Height;

    public IReadOnlyList<PlanNodeViewModel> Nodes { get; }

    public IReadOnlyList<PlanTopRow> Top { get; }

    public IReadOnlyList<PlanIssue> Issues { get; }

    public int IssueCount => Issues.Count;

    public bool HasNoIssues => Issues.Count == 0;

    /// <summary>"Query 2 · 45% of the batch" for the chip of a statement.</summary>
    public string Title => $"Query {Number}";

    public string? Subtitle => Statement.HasCost && BatchShare > 0 ? $"{BatchShare * 100:0}% of the batch" : null;

    public string StatementText => Statement.Text;

    /// <summary>The statement on one line for the header (whitespace collapsed, cut at 240 characters).</summary>
    public string StatementPreview
    {
        get
        {
            var line = string.Join(' ', Statement.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return line.Length <= 240 ? line : line[..240] + "…";
        }
    }

    /// <summary>The best missing-index hint (highest impact), shown as a banner over the canvas.</summary>
    public MissingIndex? BestMissingIndex => Statement.MissingIndexes.OrderByDescending(m => m.ImpactPercent).FirstOrDefault();

    public bool HasMissingIndex => BestMissingIndex is not null;

    public string MissingIndexText => BestMissingIndex is { } m
        ? $"Missing index on {m.Schema}.{m.Table}: the optimizer estimates it would cut this query's cost by {m.ImpactPercent:0.#}%."
        : "";

    public string? MissingIndexScript => BestMissingIndex?.CreateScript;

    /// <summary>The line under the toolbar: cost, estimated rows, times.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Statement.HasCost) parts.Add($"cost {Statement.TotalCost.ToString("0.####", CultureInfo.InvariantCulture)}");
            if (Statement.Root?.EstimatedRows is { } e) parts.Add($"~{PlanNodeViewModel.Count(e)} rows estimated");
            if (Statement.Root?.ActualRows is { } a) parts.Add($"{PlanNodeViewModel.Count(a)} rows returned");
            if (Statement.ExecutionTimeMs is { } x) parts.Add($"executed in {PlanNodeViewModel.FormatMs(x)}");
            if (Statement.PlanningTimeMs is { } p) parts.Add($"planned in {PlanNodeViewModel.FormatMs(p)}");
            parts.Add(Statement.Nodes.Count() == 1 ? "1 operator" : $"{Statement.Nodes.Count()} operators");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Metrics this statement can colour by.</summary>
    public bool CanColourByCost => Statement.HasCost;

    public bool CanColourByTime => Statement.HasTimes;

    /// <summary>Some metric can colour the operators (SQLite's plan has none: no bar, no choice to offer).</summary>
    public bool CanColour => CanColourByCost || CanColourByTime || CanColourByRows;

    public bool CanColourByRows => Nodes.Any(n => n.Node.ActualRows is not null || n.Node.EstimatedRows is not null);

    [ObservableProperty] private PlanNodeViewModel? _selectedNode;

    /// <summary>The properties panel: those of the selected operator, else those of the statement.</summary>
    public IReadOnlyList<PlanPropertyRow> Properties { get; private set; } = [];

    /// <summary>Warnings and hint of the selected operator, shown above its properties.</summary>
    public IReadOnlyList<PlanIssue> SelectedIssues { get; private set; } = [];

    public string SelectedTitle => SelectedNode is { } n ? n.Title + (n.Subtitle is null ? "" : " · " + n.Subtitle) : "Statement";

    partial void OnSelectedNodeChanged(PlanNodeViewModel? oldValue, PlanNodeViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        foreach (var n in Nodes) n.IsOnPath = false;
        if (newValue is not null)
        {
            newValue.IsSelected = true;
            for (var p = newValue; p is not null; p = p.Parent) p.IsOnPath = true;
        }
        Properties = BuildProperties(newValue?.Node);
        SelectedIssues = newValue is null ? [] : Issues.Where(i => i.Node == newValue).ToList();
        OnPropertyChanged(nameof(Properties));
        OnPropertyChanged(nameof(SelectedIssues));
        OnPropertyChanged(nameof(HasSelectedIssues));
        OnPropertyChanged(nameof(SelectedTitle));
    }

    public bool HasSelectedIssues => SelectedIssues.Count > 0;

    private IReadOnlyList<PlanPropertyRow> BuildProperties(PlanNode? node)
    {
        var rows = new List<PlanPropertyRow>();
        void Group(IEnumerable<PlanProperty> properties)
        {
            foreach (var g in properties.GroupBy(p => p.Group))
            {
                rows.Add(new PlanPropertyRow(g.Key, "", true));
                rows.AddRange(g.Select(p => new PlanPropertyRow(p.Name, p.Value, false)));
            }
        }
        if (node is not null) Group(node.Properties);
        Group(Statement.Properties);
        return rows;
    }

    /// <summary>The share of the cost (or time) shown in the Top tab: own cost over the statement's.</summary>
    private static string ShareText(PlanNode n, PlanStatement s) =>
        s.HasCost ? (n.CostShare < 0.005 ? "<1%" : (n.CostShare * 100).ToString("0", CultureInfo.InvariantCulture) + "%")
        : n.OwnTimeMs is { } t ? PlanNodeViewModel.FormatMs(t) : "";

    /// <summary>Recolours every operator by <paramref name="metric"/>.</summary>
    public void ApplyMetric(PlanMetric metric)
    {
        var maxRows = Nodes.Max(n => n.Node.ActualRows ?? n.Node.EstimatedRows ?? 0);
        foreach (var n in Nodes)
        {
            double heat;
            double high, mid, low;
            switch (metric)
            {
                case PlanMetric.Time:
                    heat = n.Node.TimeShare;
                    (high, mid, low) = (0.5, 0.2, 0.05);
                    break;
                case PlanMetric.Rows:
                    var rows = n.Node.ActualRows ?? n.Node.EstimatedRows ?? 0;
                    heat = maxRows <= 0 ? 0 : Math.Log10(1 + rows) / Math.Log10(1 + maxRows);
                    (high, mid, low) = (0.85, 0.6, 0.35);
                    break;
                default:
                    heat = n.Node.CostShare;
                    (high, mid, low) = (0.5, 0.2, 0.05);
                    break;
            }
            n.Heat = Math.Clamp(heat, 0, 1);
            n.HeatClass = heat >= high ? "high" : heat >= mid ? "mid" : heat >= low ? "low" : "none";
        }
    }

    /// <summary>Selects the operator on the way in <paramref name="direction"/>: parent, first child, or the sibling above / below.</summary>
    public PlanNodeViewModel? Neighbour(PlanNodeViewModel from, string direction)
    {
        var node = from.Node;
        switch (direction)
        {
            case "left": return from.Parent;
            case "right": return node.Children.Count == 0 ? null : Nodes.First(n => n.Node == node.Children[0]);
            default:
                if (node.Parent is not { } parent) return null;
                var siblings = parent.Children;
                var i = siblings.IndexOf(node) + (direction == "up" ? -1 : 1);
                return i < 0 || i >= siblings.Count ? null : Nodes.First(n => n.Node == siblings[i]);
        }
    }
}

/// <summary>
/// The "Execution plan" tab of a script run: every statement's plan with a canvas, a properties panel, the costliest operators
/// and the problems found. Not a grid and not a statement: it has its own icon.
/// </summary>
public sealed partial class PlanResultTab : ObservableObject, IResultTab
{
    public PlanResultTab(IReadOnlyList<(QueryPlan Plan, PlanStatement Statement)> items, PlanMode mode, DbKind engine)
    {
        Mode = mode;
        Engine = engine;
        var total = items.Where(i => i.Statement.HasCost).Sum(i => i.Statement.TotalCost);
        Statements = items
            .Select((item, i) => new PlanStatementViewModel(item.Plan, item.Statement, i + 1, total > 0 && item.Statement.HasCost ? item.Statement.TotalCost / total : 0))
            .ToList();
        _selected = Statements.FirstOrDefault();
        _metric = Selected?.CanColourByCost == true ? PlanMetric.Cost : Selected?.CanColourByTime == true ? PlanMetric.Time : PlanMetric.Rows;
        foreach (var s in Statements) s.ApplyMetric(_metric);

        Header = mode == PlanMode.Estimated || !Statements.Any(s => s.Statement.HasActuals) ? "Estimated plan" : "Execution plan";
        ToolTipText = mode == PlanMode.Estimated
            ? "The plan the optimizer would use. The statement was not run."
            : "The plan the statement ran with, with real row counts and times.";
    }

    public PlanMode Mode { get; }

    public DbKind Engine { get; }

    public IReadOnlyList<PlanStatementViewModel> Statements { get; }

    public bool HasSeveralStatements => Statements.Count > 1;

    public bool IsEmpty => Statements.Count == 0;

    [ObservableProperty] private PlanStatementViewModel? _selected;

    [NotifyPropertyChangedFor(nameof(IsCost), nameof(IsTime), nameof(IsRows))]
    [ObservableProperty] private PlanMetric _metric;

    public bool IsCost { get => Metric == PlanMetric.Cost; set { if (value) Metric = PlanMetric.Cost; } }

    public bool IsTime { get => Metric == PlanMetric.Time; set { if (value) Metric = PlanMetric.Time; } }

    public bool IsRows { get => Metric == PlanMetric.Rows; set { if (value) Metric = PlanMetric.Rows; } }

    partial void OnMetricChanged(PlanMetric value)
    {
        foreach (var s in Statements) s.ApplyMetric(value);
    }

    /// <summary>Zoom of the canvas, 0.25 to 2.</summary>
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    [ObservableProperty] private double _zoom = 1;

    public string ZoomText => $"{Zoom * 100:0}%";

    partial void OnZoomChanged(double value)
    {
        var clamped = Math.Clamp(Math.Round(value, 2), 0.25, 2);
        if (clamped != value) Zoom = clamped;
    }

    /// <summary>The properties / top / issues panel is shown.</summary>
    [ObservableProperty] private bool _showSidePanel = true;

    // ---- comparison ----

    /// <summary>Asks Claude about the plan on screen; set by the script tab.</summary>
    public Func<Task>? Analyse { get; set; }

    internal Func<bool>? CanAnalyseNow { get; set; }

    /// <summary>The "Analyse with Claude" button shows.</summary>
    public bool ShowAnalyse => Analyse is not null && CanAnalyseNow?.Invoke() == true;

    public void RefreshAnalyse() => OnPropertyChanged(nameof(ShowAnalyse));

    [RelayCommand]
    private Task AnalyseWithClaudeAsync() => Analyse?.Invoke() ?? Task.CompletedTask;

    /// <summary>The script tab that keeps the pinned baseline; null for a plan opened outside a script (tests).</summary>
    public IPlanCompareHost? Host { get; set; }

    /// <summary>The plan is shown against another one instead of alone.</summary>
    [NotifyPropertyChangedFor(nameof(IsComparing))]
    [ObservableProperty] private PlanCompareViewModel? _comparison;

    public bool IsComparing => Comparison is not null;

    public PlanCompareSource? Pinned => Host?.PinnedPlan;

    public bool HasPinned => Pinned is not null;

    /// <summary>The statement on screen is the pinned one (a pinned plan is compared with a later run, not with itself).</summary>
    public bool IsSelectedPinned => Pinned is { } p && Selected is { } s && p.Statement == s.Statement;

    public bool CanPin => Host is not null && Selected is not null && !IsSelectedPinned;

    public bool CanComparePinned => HasPinned && !IsSelectedPinned && Selected is not null;

    public string PinTooltip => Host is null ? "Plans can only be pinned from a script tab."
        : IsSelectedPinned ? "This plan is the pinned baseline."
        : HasPinned ? "Replace the pinned plan with this one. A pinned plan survives the next runs of this script, so the new plan can be compared with it."
        : "Keep this plan as the baseline. After the next run, compare the new plan with it.";

    public string ComparePinnedText => Pinned is { } p ? $"Compare with pinned plan ({p.Detail})" : "No pinned plan";

    /// <summary>The entries of the "Compare with" menu: the pinned plan, then the other statements of this tab, then the plans of the other plan tabs.</summary>
    public IReadOnlyList<PlanCompareChoice> GetCompareChoices()
    {
        var choices = new List<PlanCompareChoice>();
        if (Selected is not { } current) return choices;
        if (Pinned is { } pinned && !IsSelectedPinned) choices.Add(new PlanCompareChoice("Pinned plan", pinned.Detail, pinned, true));
        foreach (var other in Statements.Where(s => s != current))
            choices.Add(new PlanCompareChoice(other.Title, new PlanCompareSource(other.Title, other.Plan, other.Statement).Detail,
                new PlanCompareSource(other.Title, other.Plan, other.Statement), false));
        foreach (var tab in (Host?.PlanTabs ?? []).Where(t => t != this))
            foreach (var st in tab.Statements)
            {
                var label = tab.Statements.Count > 1 ? $"{tab.Header} · {st.Title}" : tab.Header;
                var source = new PlanCompareSource(label, st.Plan, st.Statement);
                choices.Add(new PlanCompareChoice(label, source.Detail, source, false));
            }
        return choices;
    }

    /// <summary>What the "Compare with" menu lists; refreshed when it opens.</summary>
    [NotifyPropertyChangedFor(nameof(HasNoCompareChoices))]
    [ObservableProperty] private IReadOnlyList<PlanCompareChoice> _compareChoices = [];

    public bool HasNoCompareChoices => CompareChoices.Count == 0;

    public void RefreshCompareChoices() => CompareChoices = GetCompareChoices();

    /// <summary>Tells the bindings that the pinned plan or the selected statement changed.</summary>
    public void RefreshPin()
    {
        foreach (var name in new[] { nameof(Pinned), nameof(HasPinned), nameof(IsSelectedPinned), nameof(CanPin), nameof(CanComparePinned), nameof(PinTooltip), nameof(ComparePinnedText) })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private void PinPlan()
    {
        if (Host is null || Selected is not { } s) return;
        Host.PinPlan(new PlanCompareSource("Pinned plan", s.Plan, s.Statement));
        RefreshPin();
    }

    [RelayCommand]
    private void UnpinPlan()
    {
        Host?.PinPlan(null);
        RefreshPin();
    }

    /// <summary>Opens the comparison of the statement on screen (the new plan, on the right) with <paramref name="baseline"/> (on the left).</summary>
    [RelayCommand]
    private void CompareWith(PlanCompareChoice? baseline)
    {
        if (baseline is null || Selected is not { } s) return;
        Comparison = new PlanCompareViewModel(baseline.Source, new PlanCompareSource(Header, s.Plan, s.Statement), () => Comparison = null);
    }

    [RelayCommand]
    private void ComparePinned()
    {
        if (Pinned is { } pinned && CanComparePinned) CompareWith(new PlanCompareChoice("Pinned plan", pinned.Detail, pinned, true));
    }

    [RelayCommand]
    private void CloseComparison() => Comparison = null;

    // ---- IResultTab ----

    public string Header { get; }

    public string? ToolTipText { get; }

    public bool HasError => false;

    public bool ShowOk => false;

    public bool IsGrid => false;

    public bool ShowFailure => false;

    public bool IsBlocked => false;

    public bool IsPlan => true;

    public string AutomationName => Header;

    /// <summary>The plan as the server sent it, for the statement on screen (what Save and Copy write).</summary>
    public string? Raw => Selected?.Plan.Raw;

    public string FileExtension => Selected?.Plan.FileExtension ?? ".txt";

    public string FileKindName => Selected?.Plan.FileKindName ?? "Plan";

    /// <summary>"Query 1" or a file name for Save.</summary>
    public string SuggestedFileName => $"plan{(Statements.Count > 1 && Selected is { } s ? "-" + s.Number : "")}{FileExtension}";

    partial void OnSelectedChanged(PlanStatementViewModel? value)
    {
        OnPropertyChanged(nameof(Raw));
        OnPropertyChanged(nameof(FileExtension));
        OnPropertyChanged(nameof(SuggestedFileName));
        Comparison = null;
        RefreshPin();
    }
}
