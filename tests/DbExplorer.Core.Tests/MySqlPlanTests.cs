using DbExplorer.Core.Plans;

namespace DbExplorer.Core.Tests;

public class MySqlPlanTests
{
    private static PlanStatement Parse(string raw, PlanMode mode = PlanMode.Estimated) =>
        Assert.Single(MySqlPlanParser.Parse(raw, mode, "SELECT 1").Statements);

    [Fact]
    public void MySqlJson_NestedLoopWithSortAndCosts()
    {
        var st = Parse("""
            {"query_block":{"select_id":1,"cost_info":{"query_cost":"30.00"},
              "ordering_operation":{"using_filesort":true,"cost_info":{"sort_cost":"10.00"},
                "nested_loop":[
                  {"table":{"table_name":"c","access_type":"ALL","rows_examined_per_scan":2000,"rows_produced_per_join":2000,
                            "cost_info":{"read_cost":"5.00","eval_cost":"10.00","prefix_cost":"15.00"},"attached_condition":"(c.a > 1)"}},
                  {"table":{"table_name":"o","access_type":"ref","key":"ix_customer","rows_produced_per_join":4000,
                            "cost_info":{"read_cost":"1.00","eval_cost":"4.00","prefix_cost":"20.00"}}}]}}}
            """);

        Assert.Equal("Query block", st.Root!.Name);
        Assert.Equal(30, st.TotalCost);
        Assert.False(st.HasActuals);
        var sort = Assert.Single(st.Root.Children);
        Assert.Equal("Sort", sort.Name);
        Assert.Contains(sort.Warnings, w => w.Title == "Filesort");
        var join = Assert.Single(sort.Children);
        Assert.Equal("Nested loop", join.Name);
        Assert.Equal(["Table scan", "Index seek"], join.Children.Select(c => c.Name));
        Assert.Equal("o (ix_customer)", join.Children[1].Object);
        Assert.Equal(15, join.Children[0].OwnCost);
        Assert.Equal(2000, join.Children[0].EstimatedRows);
        Assert.NotNull(join.Children[0].Hint);
        Assert.Contains(join.Children[0].Properties, p => p is { Group: "Predicates", Name: "attached_condition" });
    }

    [Fact]
    public void MySqlJson_UnionSubqueryAndMaterialization()
    {
        var st = Parse("""
            {"query_block":{"union_result":{"using_temporary_table":true,"table_name":"<union1,2>","access_type":"ALL",
              "query_specifications":[
                {"query_block":{"select_id":1,"table":{"table_name":"<derived3>","access_type":"ALL",
                  "materialized_from_subquery":{"query_block":{"select_id":3,"table":{"table_name":"t","access_type":"index"}}}}}},
                {"query_block":{"select_id":2,"message":"No tables used"}}]}}}
            """);

        var union = Assert.Single(st.Root!.Children);
        Assert.Equal("Union result", union.Name);
        Assert.Equal(2, union.Children.Count);
        Assert.Contains(st.Nodes, n => n.Name == "Materialize");
        Assert.Contains(st.Nodes, n => n.Name == "Index scan" && n.Object == "t");
        Assert.False(st.HasCost);
    }

    [Fact]
    public void MariaDbAnalyze_ReadsActualsAndTimes()
    {
        var plan = MySqlPlanParser.Parse("""
            {"query_optimization":{"r_total_time_ms":0.25},
             "query_block":{"select_id":1,"cost":0.05,"r_loops":1,"r_total_time_ms":12.5,
               "filesort":{"sort_key":"t.a","r_loops":1,"r_total_time_ms":3,"r_output_rows":10,
                 "temporary_table":{"nested_loop":[
                   {"table":{"table_name":"t","access_type":"ALL","loops":1,"r_loops":1,"rows":100,"r_rows":5000,"cost":0.02,
                             "r_table_time_ms":6,"r_other_time_ms":1,"filtered":100}},
                   {"table":{"table_name":"u","access_type":"eq_ref","key":"PRIMARY","r_loops":5000,"rows":1,"r_rows":1,"cost":0.01,
                             "r_table_time_ms":2}}]}}}}
            """, PlanMode.Actual, "SELECT 1");
        var st = Assert.Single(plan.Statements);

        Assert.True(st.HasActuals);
        Assert.True(st.HasTimes);
        Assert.Equal(0.25, st.PlanningTimeMs);
        Assert.Equal(12.5, st.ExecutionTimeMs);
        var scan = st.Nodes.Single(n => n.Object == "t");
        Assert.Equal(5000, scan.ActualRows);
        Assert.Equal(7, scan.ActualTimeMs);
        Assert.True(scan.BadEstimate);
        var seek = st.Nodes.Single(n => n.Object == "u (PRIMARY)");
        Assert.Equal("Unique index seek", seek.Name);
        Assert.Equal(5000, seek.ActualRows);
        Assert.Equal(".json", plan.FileExtension);
    }

    [Fact]
    public void MySqlExplainAnalyze_ReadsTheTextTree()
    {
        const string tree = """
            -> Nested loop inner join  (cost=1.6 rows=3) (actual time=0.0312..0.5 rows=3 loops=1)
                -> Filter: (a.x > 1)  (cost=0.55 rows=3) (actual time=0.02..0.1 rows=3 loops=1)
                    -> Table scan on a  (cost=0.55 rows=3) (actual time=0.0221..0.08 rows=3 loops=1)
                -> Single-row index lookup on b using PRIMARY (id=a.id)  (cost=0.283 rows=1) (actual time=0.003..0.01 rows=1 loops=3)
                -> Index lookup on c using ix (id=a.id)  (cost=0.1..0.2 rows=1e+3) (never executed)
            """;
        var plan = MySqlPlanParser.Parse(tree, PlanMode.Actual, "SELECT 1");
        var st = Assert.Single(plan.Statements);

        var root = st.Root!;
        Assert.Equal("Nested loop inner join", root.Name);
        Assert.Equal(3, root.Children.Count);
        Assert.Equal("Filter", root.Children[0].Name);
        Assert.Equal(("Table scan", "a"), (root.Children[0].Children[0].Name, root.Children[0].Children[0].Object));
        var seek = root.Children[1];
        Assert.Equal(("Unique index seek", "b using PRIMARY"), (seek.Name, seek.Object));
        Assert.Equal(3, seek.ActualRows);
        Assert.Equal(0.03, seek.ActualTimeMs!.Value, 6);
        var never = root.Children[2];
        Assert.Equal("Index seek", never.Name);
        Assert.Equal(0, never.ActualRows);
        Assert.Equal(0.2, never.SubtreeCost);
        Assert.Equal(1000, never.EstimatedRows);
        Assert.Equal(0.5, st.ExecutionTimeMs);
        Assert.Equal(".txt", plan.FileExtension);
    }

    [Fact]
    public void MySqlJsonVersion2_ReadsOperations()
    {
        var st = Parse("""
            {"query":"select 1","query_plan":{"operation":"Nested loop inner join","estimated_rows":4,"estimated_total_cost":2.5,
              "inputs":[{"operation":"Table scan on a","table_name":"a","estimated_rows":4,"estimated_total_cost":0.5},
                        {"operation":"Index lookup on b using ix (x=a.x)","estimated_rows":1,"estimated_total_cost":1.0}]}}
            """);

        Assert.Equal(2.5, st.TotalCost);
        Assert.Equal(["Table scan", "Index seek"], st.Root!.Children.Select(c => c.Name));
        Assert.Equal(1.0, st.Root.OwnCost);
    }

    [Theory]
    [InlineData("{\"query_block\":{}}", true)]
    [InlineData("  -> Table scan on t", true)]
    [InlineData("{\"a\":1}", false)]
    [InlineData("hello", false)]
    [InlineData(null, false)]
    public void LooksLikePlan(string? text, bool expected) => Assert.Equal(expected, MySqlPlanParser.LooksLikePlan(text));
}
