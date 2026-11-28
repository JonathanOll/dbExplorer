using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;
using DbExplorer.Core.Plans;

namespace DbExplorer.App.Tests;

/// <summary>A showplan with a hot scan, a spilling sort and a missing index, as a tab of the result strip.</summary>
internal static class PlanSamples
{
    public const string Xml = """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements>
        <StmtSimple StatementText="SELECT 1" StatementType="SELECT" StatementSubTreeCost="10" StatementEstRows="10"><QueryPlan>
         <MissingIndexes><MissingIndexGroup Impact="60"><MissingIndex Database="[d]" Schema="[dbo]" Table="[Orders]">
           <ColumnGroup Usage="EQUALITY"><Column Name="[Status]"/></ColumnGroup></MissingIndex></MissingIndexGroup></MissingIndexes>
         <RelOp NodeId="0" PhysicalOp="Sort" LogicalOp="Sort" EstimateRows="10" EstimatedTotalSubtreeCost="10">
          <Warnings><SpillToTempDb SpillLevel="1"/></Warnings>
          <RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="10" ActualExecutions="1" ActualElapsedms="100"/></RunTimeInformation>
          <Sort><RelOp NodeId="1" PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="10" EstimatedTotalSubtreeCost="9.5">
           <RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="10" ActualExecutions="1" ActualElapsedms="90"/></RunTimeInformation>
           <NestedLoops>
            <RelOp NodeId="2" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan" EstimateRows="10" EstimatedTotalSubtreeCost="8">
             <RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="10" ActualExecutions="1" ActualElapsedms="80"/></RunTimeInformation>
             <IndexScan><Object Table="[Orders]" Index="[PK]" IndexKind="Clustered"/><Predicate><ScalarOperator ScalarString="[Status]='x'"/></Predicate></IndexScan></RelOp>
            <RelOp NodeId="3" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="1" EstimatedTotalSubtreeCost="1.4">
             <RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="10" ActualExecutions="10" ActualElapsedms="5"/></RunTimeInformation>
             <IndexScan><Object Table="[Customers]" Index="[PK]" IndexKind="Clustered"/></IndexScan></RelOp>
           </NestedLoops></RelOp></Sort></RelOp>
        </QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>
        """;

    public static PlanResultTab Tab(PlanMode mode = PlanMode.Actual)
    {
        var plan = ShowplanParser.Parse(Xml, mode);
        return new PlanResultTab(plan.Statements.Select(s => (plan, s)).ToList(), mode, DbKind.SqlServer);
    }
}

public class PlanViewModelTests
{
    [Fact]
    public void Tab_OpensOnTheCostliestOperator_AndColoursByCost()
    {
        var tab = PlanSamples.Tab();
        var s = tab.Selected!;

        Assert.Equal("Execution plan", tab.Header);
        Assert.True(((IResultTab)tab).IsPlan);
        Assert.Equal("Clustered Index Scan", s.SelectedNode!.Title);   // 8 of 10
        Assert.Equal("high", s.SelectedNode.HeatClass);
        Assert.Equal("80%", s.SelectedNode.CostText);
        Assert.True(s.SelectedNode.IsSelected);
        Assert.True(s.Nodes.Single(n => n.Node.Id == 0).IsOnPath);   // the path to the root is lit
        Assert.Equal(PlanMetric.Cost, tab.Metric);
    }

    [Fact]
    public void Metric_ChangesTheHeat()
    {
        var tab = PlanSamples.Tab();
        var s = tab.Selected!;
        var sort = s.Nodes.Single(n => n.Node.Id == 0);
        Assert.Equal("low", sort.HeatClass);   // own cost 0.5 of 10: 5%

        tab.IsTime = true;   // the sort itself spent 10 of 100 ms: still small; the scan spent 80
        Assert.Equal(PlanMetric.Time, tab.Metric);
        Assert.Equal("high", s.Nodes.Single(n => n.Node.Id == 2).HeatClass);

        tab.IsRows = true;
        Assert.Equal(PlanMetric.Rows, tab.Metric);
        Assert.True(tab.IsRows && !tab.IsCost);
    }

    [Fact]
    public void Issues_ListWarningsHintsAndTheMissingIndex()
    {
        var s = PlanSamples.Tab().Selected!;

        Assert.Contains(s.Issues, i => i.IsIndex && i.Script!.StartsWith("CREATE NONCLUSTERED INDEX"));
        Assert.Contains(s.Issues, i => i.IsWarning && i.Title.StartsWith("Sort: Spill"));
        Assert.Contains(s.Issues, i => i.IsHintKind && i.Title.StartsWith("Clustered Index Scan"));
        Assert.True(s.HasMissingIndex);
        Assert.Contains("60%", s.MissingIndexText);
        // what is selected shows its own issues above its properties
        Assert.Contains(s.SelectedIssues, i => i.IsHintKind);
    }

    [Fact]
    public void Properties_GroupTheSelectedOperatorThenTheStatement()
    {
        var s = PlanSamples.Tab().Selected!;
        var rows = s.Properties;

        Assert.Contains(rows, r => r.IsHeader && r.Name == "Costs");
        Assert.Contains(rows, r => r.IsEntry && r.Name == "Estimated operator cost" && r.Value == "8");
        Assert.Contains(rows, r => r.IsEntry && r.Name == "Predicate" && r.Value.Contains("[Status]"));
        Assert.Contains(rows, r => r.IsHeader && r.Name == "Statement");
        Assert.True(rows.ToList().FindIndex(r => r.Name == "Costs") < rows.ToList().FindIndex(r => r.Name == "Statement"));
    }

    [Fact]
    public void Neighbour_WalksTheTree()
    {
        var s = PlanSamples.Tab().Selected!;
        var sort = s.Nodes.Single(n => n.Node.Id == 0);
        var join = s.Nodes.Single(n => n.Node.Id == 1);
        var scan = s.Nodes.Single(n => n.Node.Id == 2);
        var seek = s.Nodes.Single(n => n.Node.Id == 3);

        Assert.Same(join, s.Neighbour(sort, "right"));
        Assert.Same(scan, s.Neighbour(join, "right"));
        Assert.Same(seek, s.Neighbour(scan, "down"));
        Assert.Same(scan, s.Neighbour(seek, "up"));
        Assert.Same(join, s.Neighbour(scan, "left"));
        Assert.Null(s.Neighbour(sort, "left"));
        Assert.Null(s.Neighbour(seek, "down"));
    }

    [Fact]
    public void Layout_PutsChildrenRightOfTheirParent_AndTheParentLevelWithThem()
    {
        var s = PlanSamples.Tab().Selected!;
        var boxes = s.Layout.ByNode;
        var join = boxes[s.Statement.Nodes.Single(n => n.Id == 1)];
        var scan = boxes[s.Statement.Nodes.Single(n => n.Id == 2)];
        var seek = boxes[s.Statement.Nodes.Single(n => n.Id == 3)];

        Assert.True(scan.X > join.Right);
        Assert.Equal(scan.X, seek.X);
        Assert.True(seek.Y > scan.Y + scan.Height);   // no overlap
        Assert.Equal((scan.CenterY + seek.CenterY) / 2, join.CenterY, 3);
    }

    [Fact]
    public void Zoom_IsKeptInRange_AndSaveNamesTheEngineFile()
    {
        var tab = PlanSamples.Tab(PlanMode.Estimated);
        Assert.Equal("Estimated plan", tab.Header);   // no actuals were asked for
        tab.Zoom = 9;
        Assert.Equal(2, tab.Zoom);
        tab.Zoom = 0.01;
        Assert.Equal(0.25, tab.Zoom);
        Assert.Equal(".sqlplan", tab.FileExtension);
        Assert.Equal("plan.sqlplan", tab.SuggestedFileName);
        Assert.StartsWith("<ShowPlanXML", tab.Raw);
    }

    [Fact]
    public void Keys_CtrlL_IsTheEstimatedPlan_CtrlM_TogglesTheActualOne()
    {
        Assert.Equal(ScriptKeyAction.EstimatedPlan, ScriptTabKeys.Resolve(Key.L, KeyModifiers.Control));
        Assert.Equal(ScriptKeyAction.ToggleActualPlan, ScriptTabKeys.Resolve(Key.M, KeyModifiers.Control));
        Assert.Equal(ScriptKeyAction.None, ScriptTabKeys.Resolve(Key.L, KeyModifiers.None));
    }
}

public class PlanRunTests : IDisposable
{
    private readonly ScriptTabFixture _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task EstimatedPlan_ShowsOnlyThePlanTab_AndRunsNothing()
    {
        var tab = await _t.NewTab("DELETE FROM t WHERE id = 1");

        await tab.ShowEstimatedPlanAsync();

        var plan = Assert.IsType<PlanResultTab>(Assert.Single(tab.ResultTabs));
        Assert.Empty(tab.Results);
        Assert.Same(plan, tab.SelectedResultTab);
        Assert.Equal("Estimated plan", plan.Header);
        Assert.Equal(3L, _t.Scalar("SELECT COUNT(*) FROM t"));   // the DELETE did not run
        Assert.Contains("nothing was run", _t.Status.Message);
        Assert.NotEmpty(plan.Selected!.Nodes);
    }

    [Fact]
    public async Task EstimatedPlan_OfABrokenStatement_ShowsItsError()
    {
        var tab = await _t.NewTab("SELECT * FROM missing_table");

        await tab.ShowEstimatedPlanAsync();

        var result = Assert.Single(tab.Results);
        Assert.True(result.HasError);
        Assert.DoesNotContain(tab.ResultTabs, x => x is PlanResultTab);
    }

    [Fact]
    public async Task EstimatedPlan_OnAReadOnlyConnection_IsAllowedForAWrite()
    {
        using var ro = new ScriptTabFixture(readOnly: true);
        var tab = await ro.NewTab("UPDATE t SET name = 'x'");

        await tab.ShowEstimatedPlanAsync();

        Assert.IsType<PlanResultTab>(Assert.Single(tab.ResultTabs));
        Assert.Empty(tab.Results);
    }

    [Fact]
    public async Task ARunWithoutPlanStaysAsBefore()
    {
        var tab = await _t.NewTab("SELECT id FROM t");
        await tab.RunStatementAsync();

        Assert.Single(tab.Results);
        Assert.DoesNotContain(tab.ResultTabs, x => x is PlanResultTab);
        Assert.False(tab.CanUseActualPlan);   // SQLite has only the estimated plan
    }

    [Fact]
    public async Task APlanThenARun_ReplacesThePlanTab()
    {
        var tab = await _t.NewTab("SELECT id FROM t");
        await tab.ShowEstimatedPlanAsync();
        await tab.RunStatementAsync();

        Assert.DoesNotContain(tab.ResultTabs, x => x is PlanResultTab);
        Assert.Single(tab.Results);
    }
}

public class PlanViewTests
{
    [AvaloniaFact]
    public void TheView_DrawsOneCardPerOperator_AndAClickSelects()
    {
        var tab = PlanSamples.Tab();
        var window = new Window { Content = new PlanView { DataContext = tab }, Width = 1100, Height = 650 };
        window.Show();
        for (var i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();

        var cards = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("plannode")).ToList();
        Assert.Equal(4, cards.Count);
        Assert.Single(cards, c => c.Classes.Contains("selected"));
        Assert.Contains(window.GetVisualDescendants().OfType<PlanEdges>(), e => e.Statement == tab.Selected);

        var seekCard = cards.Single(c => ((PlanNodeViewModel)c.DataContext!).Node.Id == 3);
        tab.Selected!.SelectedNode = (PlanNodeViewModel)seekCard.DataContext!;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("selected", seekCard.Classes);
        Assert.Equal("Index Seek · [Customers].[PK]", tab.Selected.SelectedTitle);
        window.Close();
    }

    [AvaloniaFact]
    public void TheSidePanel_CanBeHidden_AndZoomFollowsTheTab()
    {
        var tab = PlanSamples.Tab();
        var view = new PlanView { DataContext = tab };
        var window = new Window { Content = view, Width = 1100, Height = 650 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        tab.Zoom = 0.5;
        Dispatcher.UIThread.RunJobs();
        var zoomer = view.GetVisualDescendants().OfType<LayoutTransformControl>().First(z => z.Name == "Zoomer");
        var scale = Assert.IsType<Avalonia.Media.ScaleTransform>(zoomer.LayoutTransform);
        Assert.Equal(0.5, scale.ScaleX);

        var side = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SidePanel");
        Assert.True(side.IsVisible);
        tab.ShowSidePanel = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(side.IsVisible);
        window.Close();
    }
}
