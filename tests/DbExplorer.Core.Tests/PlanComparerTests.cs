using DbExplorer.Core.Plans;

namespace DbExplorer.Core.Tests;

public class PlanComparerTests
{
    private static int _id;

    private static PlanNode N(string name, string? obj, double own, double? rows = null, double? ms = null, double? reads = null, string? logical = null, params PlanNode[] children)
    {
        var node = new PlanNode { Id = ++_id, Name = name, Object = obj, Logical = logical, OwnCost = own, EstimatedRows = rows, ActualRows = rows, OwnTimeMs = ms, LogicalReads = reads };
        node.SubtreeCost = own + children.Sum(c => c.SubtreeCost);
        node.Children.AddRange(children);
        return node;
    }

    private static PlanStatement Plan(PlanNode root, bool cost = true, double? exec = null, double? planning = null)
    {
        var s = new PlanStatement { Text = "SELECT 1", Root = root, HasCost = cost, ExecutionTimeMs = exec, PlanningTimeMs = planning };
        s.Finish();
        return s;
    }

    private static PlanStatement Before(double scan = 9) =>
        Plan(N("Nested Loops", null, 0.5, 100, logical: "Inner Join", children:
        [
            N("Clustered Index Scan", "[Orders].[PK_Orders]", scan, 5000, 80, 900),
            N("Index Seek", "[Customers].[PK_Customers]", 0.5, 5000, 20, 100),
        ]));

    [Fact]
    public void Identical_PlansHaveNoChange()
    {
        var diff = PlanComparer.Compare(Before(), Before());

        Assert.True(diff.IsIdentical);
        Assert.Equal(3, diff.Matched.Count());
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.All(diff.Matched, p => Assert.Equal(PlanVerdict.Same, p.Verdict));
        Assert.Equal(PlanVerdict.Same, diff.Overall);
        Assert.Contains("same plan", diff.Summary);
    }

    [Fact]
    public void ScanTurnedIntoSeek_IsMatchedAsReplaced_AndBetter()
    {
        var after = Plan(N("Nested Loops", null, 0.5, 100, logical: "Inner Join", children:
        [
            N("Index Seek", "[Orders].[IX_Orders_Status]", 0.2, 5000, 5, 40),
            N("Index Seek", "[Customers].[PK_Customers]", 0.5, 5000, 20, 100),
        ]));

        var diff = PlanComparer.Compare(Before(), after);

        var pair = Assert.Single(diff.Matched, p => p.Replaced);
        Assert.Equal("Clustered Index Scan", pair.Before!.Name);
        Assert.Equal("Index Seek", pair.After!.Name);
        Assert.Equal(PlanVerdict.Better, pair.Verdict);
        Assert.Equal("Own cost", pair.Primary!.Name);
        Assert.Equal("-98%", pair.Primary.ChangeText);
        Assert.Equal(PlanVerdict.Better, diff.Overall);
        var total = diff.Statement.Single(m => m.Name == "Total cost");
        Assert.Equal(10, total.Before);
        Assert.Equal(1.2, total.After!.Value, 6);
        Assert.Equal("-88%", total.ChangeText);
        Assert.Contains("Cost 10 → 1.2 (-88%)", diff.Summary);
        Assert.Contains("Clustered Index Scan on Orders replaced by Index Seek on Orders", diff.Summary);
        Assert.Equal(PlanVerdict.Better, diff.Statement.Single(m => m.Name == "Logical reads").Verdict);
    }

    [Fact]
    public void ExtraOperatorOnTop_IsAdded_AndTheRestStillMatches()
    {
        var after = Plan(N("Sort", null, 2, 100, children: [Before().Root!]));

        var diff = PlanComparer.Compare(Before(), after);

        var added = Assert.Single(diff.Added);
        Assert.Equal("Sort", added.After!.Name);
        Assert.Equal(3, diff.Matched.Count());
        Assert.Contains(diff.Matched, p => p.Before!.Name == "Nested Loops" && p.After!.Name == "Nested Loops");
        Assert.Equal(PlanVerdict.Worse, diff.Overall);
        Assert.Contains("Sort added", diff.Summary);
    }

    [Fact]
    public void ExtraOperatorInTheMiddle_IsAdded_NotMatchedToItsNeighbours()
    {
        var after = Plan(N("Nested Loops", null, 0.5, 100, logical: "Inner Join", children:
        [
            N("Sort", null, 1, 5000, children: [N("Clustered Index Scan", "[Orders].[PK_Orders]", 9, 5000, 80, 900)]),
            N("Index Seek", "[Customers].[PK_Customers]", 0.5, 5000, 20, 100),
        ]));

        var diff = PlanComparer.Compare(Before(), after);

        Assert.Equal("Sort", Assert.Single(diff.Added).After!.Name);
        Assert.Empty(diff.Removed);
        Assert.Equal(3, diff.Matched.Count());
        Assert.Contains(diff.Matched, p => p.Before!.Name == "Clustered Index Scan" && p.After!.Name == "Clustered Index Scan");
    }

    [Fact]
    public void RemovedOperator_IsListed()
    {
        var withSort = Plan(N("Sort", null, 2, 100, children: [Before().Root!]));

        var diff = PlanComparer.Compare(withSort, Before());

        Assert.Equal("Sort", Assert.Single(diff.Removed).Before!.Name);
        Assert.Empty(diff.Added);
        Assert.Equal(3, diff.Matched.Count());
        Assert.Equal(PlanVerdict.Better, diff.Overall);
        Assert.Contains("Sort removed", diff.Summary);
    }

    [Fact]
    public void DifferentTables_AreNotPaired()
    {
        var a = Plan(N("Seq Scan", "public.orders as o", 5, 10));
        var b = Plan(N("Seq Scan", "public.customers as c", 5, 10));

        var diff = PlanComparer.Compare(a, b);

        // the roots are paired by position anyway; a root with another table is a removed + added operator
        Assert.Single(diff.Removed);
        Assert.Single(diff.Added);
    }

    [Fact]
    public void Children_AreAlignedByLikeness_NotOnlyByPosition()
    {
        var a = Plan(N("Hash Join", null, 1, children: [N("Seq Scan", "public.orders", 5, 10), N("Seq Scan", "public.customers", 3, 10)]));
        var b = Plan(N("Hash Join", null, 1, children: [N("Seq Scan", "public.customers", 3, 10), N("Seq Scan", "public.orders", 5, 10)]));

        var diff = PlanComparer.Compare(a, b);

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.All(diff.Matched.Where(p => p.Before!.Object is not null), p => Assert.Equal(p.Before!.Object, p.After!.Object));
    }

    [Fact]
    public void DifferentEngines_ShapesCompareWithoutCrashing_AndMissingMetricsAreSkipped()
    {
        // SQL Server style (costs, reads, no time on the statement) against a SQLite one (no cost, no rows)
        var sqlserver = Before();
        var sqlite = Plan(N("SEARCH", "orders USING INDEX ix_status", 0), cost: false);
        sqlite.Root!.EstimatedRows = null;
        sqlite.Root.ActualRows = null;

        var diff = PlanComparer.Compare(sqlserver, sqlite);

        Assert.DoesNotContain(diff.Statement, m => m.Name == "Total cost");   // one side has no cost: no cost delta
        Assert.DoesNotContain(diff.Statement, m => m.Name == "Execution time");
        Assert.Contains(diff.Statement, m => m.Name == "Operators" && m.Before == 3 && m.After == 1);
        Assert.NotEmpty(diff.Summary);
    }

    [Fact]
    public void WithoutCostOnBothSides_TheVerdictComesFromTime()
    {
        var a = Plan(N("SCAN", "orders", 0, ms: 100), cost: false);
        var b = Plan(N("SCAN", "orders", 0, ms: 20), cost: false);

        var pair = Assert.Single(PlanComparer.Compare(a, b).Matched);

        Assert.Equal("Own time", pair.Primary!.Name);
        Assert.Equal(PlanVerdict.Better, pair.Verdict);
        Assert.Equal("-80 ms", pair.Primary.ChangeText);
    }

    [Fact]
    public void MetricAbsentOnBothSides_IsNotListed()
    {
        var a = Plan(N("Seq Scan", "t", 5, 10));
        var b = Plan(N("Seq Scan", "t", 5, 10));

        var pair = Assert.Single(PlanComparer.Compare(a, b).Matched);

        Assert.DoesNotContain(pair.Metrics, m => m.Name is "Own time" or "Logical reads");
        Assert.Contains(pair.Metrics, m => m.Name == "Own cost");
    }

    [Theory]
    [InlineData(100, 109, PlanVerdict.Same)]
    [InlineData(100, 111, PlanVerdict.Worse)]
    [InlineData(100, 89, PlanVerdict.Better)]
    [InlineData(0, 50, PlanVerdict.Worse)]
    [InlineData(0.0001, 0.0002, PlanVerdict.Same)]   // below the noise floor
    public void Verdict_UsesTenPercent(double before, double after, PlanVerdict expected) =>
        Assert.Equal(expected, new PlanMetricDelta("Own cost", PlanUnit.Cost, before, after).Verdict);

    [Fact]
    public void RowCounts_AreNeutral_NotBetterOrWorse() =>
        Assert.Equal(PlanVerdict.Changed, new PlanMetricDelta("Rows", PlanUnit.Rows, 100, 1000, LowerIsBetter: false).Verdict);

    [Fact]
    public void StatementDeltas_CarryTimesAndReads()
    {
        var a = Plan(N("Seq Scan", "t", 5, 10, reads: 100), exec: 120, planning: 2);
        var b = Plan(N("Seq Scan", "t", 5, 10, reads: 10), exec: 30, planning: 2.5);

        var diff = PlanComparer.Compare(a, b);

        Assert.Equal(PlanVerdict.Better, diff.Statement.Single(m => m.Name == "Execution time").Verdict);
        Assert.Equal(PlanVerdict.Better, diff.Statement.Single(m => m.Name == "Logical reads").Verdict);
        Assert.Equal(PlanVerdict.Same, diff.Statement.Single(m => m.Name == "Planning time").Verdict);   // 0.5 ms is noise
        Assert.Equal(PlanVerdict.Better, diff.Overall);   // no cost delta (equal): execution time decides
    }

    [Fact]
    public void PropertyDifferences_ListWhatChanged()
    {
        var x = N("Index Seek", "[T].[IX]", 1);
        x.Properties.Add(new PlanProperty("Predicates", "Seek predicate", "a = 1"));
        var y = N("Index Seek", "[T].[IX]", 1);
        y.Properties.Add(new PlanProperty("Predicates", "Seek predicate", "a = 2"));
        y.Properties.Add(new PlanProperty("Operator", "Ordered", "True"));

        var pair = Assert.Single(PlanComparer.Compare(Plan(x), Plan(y)).Matched);

        Assert.Equal(2, pair.Properties.Count);
        Assert.Contains(pair.Properties, p => p.Name == "Seek predicate" && p.Before == "a = 1" && p.After == "a = 2");
        Assert.Contains(pair.Properties, p => p.Name == "Ordered" && p.Before is null && p.After == "True");
    }

    [Fact]
    public void ParsedShowplans_Compare()
    {
        var plan = ShowplanParser.Parse(PlanTests.ActualXml, PlanMode.Actual);
        var s = plan.Statements[0];

        var diff = PlanComparer.Compare(s, s);

        Assert.True(diff.IsIdentical);
        Assert.Equal(3, diff.Matched.Count());
        Assert.Equal(900, diff.Statement.Single(m => m.Name == "Logical reads").After);
    }
}
