using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;
using DbExplorer.Core.Plans;

namespace DbExplorer.App.Tests;

public class PlanCompareViewModelTests : IDisposable
{
    private readonly ScriptTabFixture _t = new();

    public void Dispose() => _t.Dispose();

    /// <summary>The same showplan as <see cref="PlanSamples.Xml"/> after an index: the scan of Orders is a seek and everything above is cheaper.</summary>
    private static PlanResultTab IndexedTab(IPlanCompareHost? host)
    {
        var xml = PlanSamples.Xml
            .Replace("PhysicalOp=\"Clustered Index Scan\" LogicalOp=\"Clustered Index Scan\" EstimateRows=\"10\" EstimatedTotalSubtreeCost=\"8\"", "PhysicalOp=\"Index Seek\" LogicalOp=\"Index Seek\" EstimateRows=\"10\" EstimatedTotalSubtreeCost=\"0.5\"")
            .Replace("Index=\"[PK]\" IndexKind=\"Clustered\"/><Predicate>", "Index=\"[IX_Status]\" IndexKind=\"NonClustered\"/><Predicate>")
            .Replace("EstimatedTotalSubtreeCost=\"9.5\"", "EstimatedTotalSubtreeCost=\"2\"")
            .Replace("StatementSubTreeCost=\"10\"", "StatementSubTreeCost=\"2.5\"")
            .Replace("EstimatedTotalSubtreeCost=\"10\"", "EstimatedTotalSubtreeCost=\"2.5\"");
        var plan = ShowplanParser.Parse(xml, PlanMode.Actual);
        return new PlanResultTab(plan.Statements.Select(s => (plan, s)).ToList(), PlanMode.Actual, DbKind.SqlServer) { Host = host };
    }

    private static PlanResultTab BaselineTab(IPlanCompareHost? host)
    {
        var tab = PlanSamples.Tab();
        tab.Host = host;
        return tab;
    }

    [Fact]
    public async Task Pin_KeepsThePlanOnTheScriptTab_AcrossRuns()
    {
        var script = await _t.NewTab("SELECT id FROM t");
        var first = BaselineTab(script);
        script.ResultTabs.Add(first);

        Assert.True(first.CanPin);
        Assert.Null(script.PinnedPlan);
        first.PinPlanCommand.Execute(null);

        Assert.Same(first.Selected!.Statement, script.PinnedPlan!.Statement);
        Assert.True(first.IsSelectedPinned);
        Assert.False(first.CanPin);
        Assert.False(first.CanComparePinned);   // not with itself

        // a new run clears the result tabs, the pinned plan stays
        script.ResultTabs.Clear();
        var second = IndexedTab(script);
        script.ResultTabs.Add(second);
        Assert.NotNull(script.PinnedPlan);
        Assert.True(second.HasPinned);
        Assert.True(second.CanComparePinned);
        Assert.Contains("Compare with pinned plan", second.ComparePinnedText);
    }

    [Fact]
    public async Task Unpin_ForgetsTheBaseline()
    {
        var script = await _t.NewTab("SELECT id FROM t");
        var tab = BaselineTab(script);
        script.ResultTabs.Add(tab);
        tab.PinPlanCommand.Execute(null);

        tab.UnpinPlanCommand.Execute(null);

        Assert.Null(script.PinnedPlan);
        Assert.False(tab.HasPinned);
        Assert.False(tab.IsSelectedPinned);
    }

    [Fact]
    public async Task ComparePinned_OpensTheComparison_AndClosing_ReturnsToThePlan()
    {
        var script = await _t.NewTab("SELECT id FROM t");
        var before = BaselineTab(script);
        script.ResultTabs.Add(before);
        before.PinPlanCommand.Execute(null);
        script.ResultTabs.Clear();
        var after = IndexedTab(script);
        script.ResultTabs.Add(after);

        Assert.False(after.IsComparing);
        after.ComparePinnedCommand.Execute(null);

        var cmp = after.Comparison!;
        Assert.True(after.IsComparing);
        Assert.Equal("Pinned plan", cmp.BaselineLabel);
        Assert.Equal(PlanVerdict.Better, cmp.Overall);
        Assert.StartsWith("Cost 10 → 2.5 (-75%)", cmp.Summary);
        Assert.Contains("Clustered Index Scan on Orders replaced by Index Seek on Orders", cmp.Summary);
        Assert.Contains(cmp.Chips, c => c.Name == "Total cost" && c.IsBetter && c.Change == "-75%");
        // the replaced scan is selected on both sides, and the differences list says what changed
        Assert.Equal("Clustered Index Scan", cmp.Baseline.SelectedNode!.Title);
        Assert.Equal("Index Seek", cmp.Candidate.SelectedNode!.Title);
        Assert.Contains(cmp.Differences, r => r.Name == "Own cost" && r.Verdict == PlanVerdict.Better);
        Assert.Equal("-94%", cmp.Differences.Single(r => r.Name == "Own cost").Change);
        // the badge is on the card of both sides
        Assert.Equal("-94% cost", cmp.Baseline.SelectedNode.Compare!.Badge);
        Assert.True(cmp.Candidate.SelectedNode.Compare!.IsBetter);
        Assert.Equal("Replaced", cmp.Candidate.SelectedNode.Compare.Tag);

        cmp.CloseCommand.Execute(null);
        Assert.False(after.IsComparing);
        Assert.Null(after.Comparison);
    }

    [Fact]
    public async Task SelectingAnOperator_SelectsItsCounterpartOnTheOtherSide()
    {
        var script = await _t.NewTab("SELECT id FROM t");
        var before = BaselineTab(script);
        script.ResultTabs.Add(before);
        before.PinPlanCommand.Execute(null);
        script.ResultTabs.Clear();
        var after = IndexedTab(script);
        script.ResultTabs.Add(after);
        after.ComparePinnedCommand.Execute(null);
        var cmp = after.Comparison!;

        var seekRight = cmp.Candidate.Nodes.Single(n => n.Title == "Index Seek" && n.Subtitle!.Contains("Customers"));
        cmp.SelectNode(seekRight);
        Assert.Equal("Index Seek", cmp.Baseline.SelectedNode!.Title);
        Assert.Contains("Customers", cmp.Baseline.SelectedNode.Subtitle);
        Assert.Equal("Index Seek on Customers", cmp.DifferencesTitle);

        var sortLeft = cmp.Baseline.Nodes.Single(n => n.Title == "Sort");
        cmp.SelectNode(sortLeft);
        Assert.Equal("Sort", cmp.Candidate.SelectedNode!.Title);
        Assert.Same(sortLeft, cmp.Baseline.SelectedNode);
    }

    [Fact]
    public void AnOperatorOnOneSideOnly_HasNoCounterpart_AndSaysSo()
    {
        var plan = PlanSamples.Tab();
        var withoutSort = ShowplanParser.Parse(PlanSamples.Xml.Replace("PhysicalOp=\"Sort\" LogicalOp=\"Sort\"", "PhysicalOp=\"Compute Scalar\" LogicalOp=\"Compute Scalar\""), PlanMode.Actual);
        // the baseline has an extra Sort on top of the nested loops: wrap the candidate's root away
        var candidate = new PlanCompareSource("New", withoutSort, withoutSort.Statements[0]);
        var baselinePlan = plan.Selected!;
        candidate.Statement.Root!.Children.Clear();
        var cmp = new PlanCompareViewModel(new PlanCompareSource("Old", baselinePlan.Plan, baselinePlan.Statement), candidate);

        var removedNode = cmp.Baseline.Nodes.First(n => cmp.Diff.Removed.Any(p => p.Before == n.Node));
        cmp.SelectNode(removedNode);

        Assert.Null(cmp.Candidate.SelectedNode);
        Assert.Equal("Only in the baseline", cmp.DifferencesSubtitle);
        Assert.True(removedNode.Compare!.IsRemoved);
        Assert.Equal("Removed", removedNode.Compare.Tag);
    }

    [Fact]
    public async Task CompareChoices_ListThePinnedPlan_AndTheOtherStatements()
    {
        var script = await _t.NewTab("SELECT id FROM t");
        var tab = BaselineTab(script);
        script.ResultTabs.Add(tab);
        Assert.Empty(tab.GetCompareChoices());   // nothing to compare with yet
        tab.RefreshCompareChoices();
        Assert.True(tab.HasNoCompareChoices);

        tab.PinPlanCommand.Execute(null);
        script.ResultTabs.Clear();
        var next = IndexedTab(script);
        script.ResultTabs.Add(next);

        var choice = Assert.Single(next.GetCompareChoices());
        Assert.True(choice.IsPinned);
        Assert.Equal("Pinned plan", choice.Label);
        Assert.Contains("cost 10", choice.Detail);

        next.CompareWithCommand.Execute(choice);
        Assert.True(next.IsComparing);
    }

    [Fact]
    public void WithoutAScriptTab_PinningIsDisabled_WithAReason()
    {
        var tab = PlanSamples.Tab();

        Assert.False(tab.CanPin);
        Assert.Contains("script tab", tab.PinTooltip);
        tab.PinPlanCommand.Execute(null);   // does nothing, does not throw
        Assert.False(tab.HasPinned);
    }
}

public class PlanCompareViewTests
{
    [AvaloniaFact]
    public void TheCompareView_BuildsBothCanvases_WithOneCardPerOperator()
    {
        var plan = ShowplanParser.Parse(PlanSamples.Xml, PlanMode.Actual);
        var s = plan.Statements[0];
        var vm = new PlanCompareViewModel(new PlanCompareSource("Baseline", plan, s), new PlanCompareSource("New", plan, s));
        var window = new Window { Content = new PlanCompareView { DataContext = vm }, Width = 1300, Height = 800 };
        window.Show();
        for (var i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();

        var cards = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("plannode")).ToList();
        Assert.Equal(8, cards.Count);   // 4 operators on each side
        Assert.Equal(2, window.GetVisualDescendants().OfType<PlanEdges>().Count());
        Assert.Contains(window.GetVisualDescendants().OfType<PlanEdges>(), e => e.Statement == vm.Baseline);
        Assert.Contains(window.GetVisualDescendants().OfType<PlanEdges>(), e => e.Statement == vm.Candidate);
        Assert.Equal(2, cards.Count(c => c.Classes.Contains("selected")));   // an operator and its counterpart

        vm.Stacked = !vm.Stacked;
        Dispatcher.UIThread.RunJobs();
        var candidatePane = window.GetVisualDescendants().OfType<DockPanel>().Single(d => d.Name == "CandidatePane");
        Assert.Equal(vm.Stacked ? 1 : 0, Grid.GetRow(candidatePane));
        window.Close();
    }

    [AvaloniaFact]
    public void CtrlPlusMinusAndZero_ZoomAndFit_LikeThePlanView()
    {
        var plan = ShowplanParser.Parse(PlanSamples.Xml, PlanMode.Actual);
        var s = plan.Statements[0];
        var vm = new PlanCompareViewModel(new PlanCompareSource("Baseline", plan, s), new PlanCompareSource("New", plan, s));
        var window = new Window { Content = new PlanCompareView { DataContext = vm }, Width = 1300, Height = 800 };
        window.Show();
        for (var i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<ScrollViewer>().Single(x => x.Name == "BaselineScroller").Focus();
        var fitted = vm.Zoom;

        window.KeyPressQwerty(PhysicalKey.Minus, RawInputModifiers.Control);
        Assert.Equal(fitted - 0.1, vm.Zoom, 6);
        window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.Control);
        window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.Control);
        Assert.Equal(fitted + 0.1, vm.Zoom, 6);
        window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(fitted, vm.Zoom, 6);
        Assert.True(vm.Zoom >= PlanView.MinFitZoom);
        window.Close();
    }

    [AvaloniaFact]
    public void ThePlanView_SwitchesToTheComparison_AndBack()
    {
        var tab = PlanSamples.Tab();
        var view = new PlanView { DataContext = tab };
        var window = new Window { Content = view, Width = 1300, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(window.GetVisualDescendants().OfType<PlanCompareView>());

        var s = tab.Selected!;
        tab.CompareWithCommand.Execute(new PlanCompareChoice("Baseline", "", new PlanCompareSource("Baseline", s.Plan, s.Statement), false));
        for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();

        var compare = Assert.Single(window.GetVisualDescendants().OfType<PlanCompareView>());
        Assert.True(compare.IsVisible);
        Assert.True(tab.Comparison!.Diff.IsIdentical);

        tab.CloseComparisonCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(tab.IsComparing);
        window.Close();
    }
}
