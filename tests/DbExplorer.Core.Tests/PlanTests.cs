using DbExplorer.Core.Model;
using DbExplorer.Core.Plans;
using DbExplorer.Core.Scripting;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

public class PlanTests
{
    /// <summary>An actual plan: Nested Loops over an index seek and a key lookup, a missing index and a spilling sort.</summary>
    internal const string ActualXml = """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.5">
         <BatchSequence><Batch><Statements>
          <StmtSimple StatementText="SELECT o.Id, c.Name FROM Orders o JOIN Customers c ON c.Id = o.CustomerId WHERE o.Total &gt; 100" StatementId="1" StatementType="SELECT" StatementSubTreeCost="10" StatementEstRows="50" StatementOptmLevel="FULL">
           <QueryPlan DegreeOfParallelism="1" CachedPlanSize="32" CompileTime="3">
            <MissingIndexes><MissingIndexGroup Impact="87.5"><MissingIndex Database="[Shop]" Schema="[dbo]" Table="[Orders]">
              <ColumnGroup Usage="EQUALITY"><Column Name="[Status]" ColumnId="2"/></ColumnGroup>
              <ColumnGroup Usage="INEQUALITY"><Column Name="[Total]" ColumnId="3"/></ColumnGroup>
              <ColumnGroup Usage="INCLUDE"><Column Name="[CustomerId]" ColumnId="4"/></ColumnGroup>
            </MissingIndex></MissingIndexGroup></MissingIndexes>
            <RelOp NodeId="0" PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="50" EstimateIO="0" EstimateCPU="0.5" AvgRowSize="40" EstimatedTotalSubtreeCost="10" Parallel="0" EstimateRebinds="0" EstimateRewinds="0">
             <OutputList><ColumnReference Table="[Orders]" Alias="[o]" Column="Id"/></OutputList>
             <RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="5000" ActualExecutions="1" ActualElapsedms="120" ActualCPUms="100"/></RunTimeInformation>
             <NestedLoops Optimized="false">
              <RelOp NodeId="1" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan" EstimateRows="50" EstimateIO="2" EstimateCPU="0.5" EstimatedTotalSubtreeCost="2.5" TableCardinality="100000" Parallel="0" EstimateRebinds="0" EstimateRewinds="0">
               <RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="5000" ActualRowsRead="100000" ActualExecutions="1" ActualElapsedms="80" ActualLogicalReads="900"/></RunTimeInformation>
               <IndexScan Ordered="false"><Object Database="[Shop]" Schema="[dbo]" Table="[Orders]" Index="[PK_Orders]" Alias="[o]" IndexKind="Clustered"/>
                <Predicate><ScalarOperator ScalarString="[Shop].[dbo].[Orders].[Total] as [o].[Total]&gt;(100)"><Compare/></ScalarOperator></Predicate></IndexScan>
              </RelOp>
              <RelOp NodeId="2" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="1" EstimateIO="0.003" EstimateCPU="0.0001" EstimatedTotalSubtreeCost="7.5" Parallel="0" EstimateRebinds="49" EstimateRewinds="0">
               <RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="5000" ActualExecutions="5000" ActualElapsedms="20"/></RunTimeInformation>
               <IndexScan><Object Database="[Shop]" Schema="[dbo]" Table="[Customers]" Index="[PK_Customers]" Alias="[c]" IndexKind="Clustered"/>
                <SeekPredicates><SeekPredicateNew><SeekKeys><Prefix ScanType="EQ"><RangeColumns><ColumnReference Column="Id"/></RangeColumns><RangeExpressions><ScalarOperator ScalarString="[o].[CustomerId]"/></RangeExpressions></Prefix></SeekKeys></SeekPredicateNew></SeekPredicates></IndexScan>
              </RelOp>
             </NestedLoops>
            </RelOp>
           </QueryPlan>
          </StmtSimple>
         </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    [Fact]
    public void Showplan_BuildsTreeCostsAndActuals()
    {
        var plan = ShowplanParser.Parse(ActualXml, PlanMode.Actual);

        var s = Assert.Single(plan.Statements);
        Assert.Equal("SELECT", s.Type);
        Assert.StartsWith("SELECT o.Id", s.Text);
        Assert.NotNull(s.Root);
        Assert.Equal("Nested Loops", s.Root!.Name);
        Assert.Equal("Inner Join", s.Root.Logical);
        Assert.Equal(2, s.Root.Children.Count);
        Assert.True(s.HasActuals);
        Assert.True(s.HasTimes);

        // own cost = subtree - children: 10 - (2.5 + 7.5) = 0
        Assert.Equal(0, s.Root.OwnCost, 6);
        var scan = s.Root.Children[0];
        Assert.Equal(2.5, scan.OwnCost, 6);
        Assert.Equal(0.25, scan.CostShare, 6);
        Assert.Equal("[Orders].[PK_Orders] ([o])", scan.Object);
        Assert.Equal(5000, scan.ActualRows);
        Assert.Equal(80, s.Root.Children[0].OwnTimeMs);   // 80 - nothing below
        Assert.Equal(7.5, s.Root.Children[1].OwnCost, 6);
        Assert.Same(s.Root, scan.Parent);

        // seek: 49 rebinds -> 50 executions expected, 1 row each: 50 rows; got 5000: far off
        var seek = s.Root.Children[1];
        Assert.Equal(50, seek.EstimatedRows);
        Assert.True(seek.BadEstimate);
        Assert.Contains(seek.Warnings, w => w.Title == "Wrong row estimate");
        Assert.Contains(seek.Properties, p => p.Name == "Seek predicates" && p.Value.Contains("[o].[CustomerId]"));

        // the scan with a predicate gets the hint
        Assert.NotNull(scan.Hint);
        Assert.Contains(scan.Properties, p => p.Group == "Predicates" && p.Value.Contains("Total"));
    }

    [Fact]
    public void Showplan_ReadsMissingIndexAndScript()
    {
        var s = ShowplanParser.Parse(ActualXml, PlanMode.Actual).Statements[0];

        var index = Assert.Single(s.MissingIndexes);
        Assert.Equal(87.5, index.ImpactPercent);
        Assert.Equal(["[Status]"], index.EqualityColumns);
        Assert.Equal(["[Total]"], index.InequalityColumns);
        Assert.Equal(["[CustomerId]"], index.IncludeColumns);
        var sql = index.CreateScript;
        Assert.Contains("ON [dbo].[Orders] ([Status], [Total])", sql);
        Assert.Contains("INCLUDE ([CustomerId])", sql);
        Assert.StartsWith("CREATE NONCLUSTERED INDEX [IX_Orders_Status_Total]", sql);
    }

    [Fact]
    public void Showplan_EstimatedPlanHasNoActuals()
    {
        var xml = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT 1" StatementType="SELECT"><QueryPlan>
            <RelOp NodeId="0" PhysicalOp="Constant Scan" LogicalOp="Constant Scan" EstimateRows="1" EstimatedTotalSubtreeCost="0.0000001"><ConstantScan/></RelOp>
            </QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>
            """;
        var s = ShowplanParser.Parse(xml, PlanMode.Estimated).Statements.Single();
        Assert.False(s.HasActuals);
        Assert.False(s.HasTimes);
        Assert.Null(s.Root!.ActualRows);
        Assert.Equal(1, s.Root.EstimatedRows);
    }

    [Fact]
    public void Showplan_WarningsAreReported()
    {
        var xml = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements>
            <StmtSimple StatementText="x"><QueryPlan>
            <RelOp NodeId="0" PhysicalOp="Sort" LogicalOp="Sort" EstimateRows="10" EstimatedTotalSubtreeCost="3">
              <Warnings NoJoinPredicate="true"><SpillToTempDb SpillLevel="1"/><ColumnsWithNoStatistics><ColumnReference Column="Age"/></ColumnsWithNoStatistics></Warnings>
              <Sort/></RelOp></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>
            """;
        var w = ShowplanParser.Parse(xml, PlanMode.Estimated).Statements.Single().Root!.Warnings;
        Assert.Contains(w, x => x.Title == "No join predicate");
        Assert.Contains(w, x => x.Title.StartsWith("Spill"));
        Assert.Contains(w, x => x.Title.StartsWith("Columns with no statistics") && x.Detail == "Age");
    }

    [Fact]
    public void Postgres_ParsesAnalyzePlan()
    {
        var json = """
            [{"Plan":{"Node Type":"Hash Join","Join Type":"Inner","Startup Cost":10.0,"Total Cost":100.0,"Plan Rows":10,"Plan Width":8,
              "Actual Startup Time":1.0,"Actual Total Time":50.0,"Actual Rows":1000,"Actual Loops":1,"Hash Cond":"(a.id = b.id)",
              "Plans":[
               {"Node Type":"Seq Scan","Parent Relationship":"Outer","Relation Name":"orders","Alias":"a","Startup Cost":0,"Total Cost":60.0,"Plan Rows":10,"Plan Width":4,
                "Actual Startup Time":0.1,"Actual Total Time":30.0,"Actual Rows":1000,"Actual Loops":1,"Filter":"(total > 5)","Rows Removed by Filter":50000},
               {"Node Type":"Index Scan","Parent Relationship":"Inner","Relation Name":"customers","Alias":"b","Index Name":"customers_pkey","Startup Cost":0,"Total Cost":20.0,"Plan Rows":1,"Plan Width":4,
                "Actual Startup Time":0.01,"Actual Total Time":0.01,"Actual Rows":1,"Actual Loops":1000}]},
              "Planning Time":0.4,"Execution Time":51.5}]
            """;
        var plan = PostgresPlanParser.Parse(json, PlanMode.Actual, "SELECT 1");

        var s = Assert.Single(plan.Statements);
        Assert.Equal("Hash Join", s.Root!.Name);
        Assert.Equal("Inner join", s.Root.Logical);
        Assert.Equal(20, s.Root.OwnCost, 6);   // 100 - 60 - 20
        Assert.True(s.HasActuals && s.HasTimes);
        Assert.Equal(51.5, s.ExecutionTimeMs);
        var scan = s.Root.Children[0];
        Assert.Equal("orders as a", scan.Object);
        Assert.Contains("50,000", scan.Hint!);
        var index = s.Root.Children[1];
        Assert.Equal("customers (customers_pkey) as b", index.Object);
        Assert.Equal(1000, index.ActualRows);   // 1 row x 1000 loops
        Assert.Equal(10, index.OwnTimeMs);       // 0.01 ms x 1000 loops
        Assert.Contains(s.Root.Properties, p => p.Name == "Hash Cond" && p.Group == "Predicates");
    }

    [Fact]
    public void Sqlite_BuildsTreeFromRows()
    {
        var plan = SqlitePlanParser.Parse(
        [
            new(2, 0, "SCAN o"),
            new(5, 0, "SEARCH c USING INTEGER PRIMARY KEY (rowid=?)"),
            new(9, 0, "USE TEMP B-TREE FOR ORDER BY"),
        ], "SELECT 1");
        var s = Assert.Single(plan.Statements);
        Assert.False(s.HasCost);
        Assert.Equal(3, s.Root!.Children.Count);
        Assert.Equal("Table scan", s.Root.Children[0].Name);
        Assert.NotNull(s.Root.Children[0].Hint);
        Assert.Equal("Primary key seek", s.Root.Children[1].Name);
        Assert.Equal("c", s.Root.Children[1].Object);
        Assert.Equal("Temp b-tree (order by)", s.Root.Children[2].Name);
    }

    [Fact]
    public void MissingIndexes_NoEquality_StillScripts()
    {
        var index = new MissingIndex(10, "[d]", "[dbo]", "[T]", [], ["[A]"], []);
        Assert.Contains("([A])", index.CreateScript);
        Assert.DoesNotContain("INCLUDE", index.CreateScript);
    }

    // ---- through the runner, on a real SQLite file ----

    [Fact]
    public async Task Sqlite_RunnerReturnsPlanWithoutRunningTheStatement()
    {
        var file = Path.Combine(Path.GetTempPath(), $"plan-{Guid.NewGuid():N}.db");
        try
        {
            await using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
            {
                await c.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO t (v) VALUES ('a');";
                await cmd.ExecuteNonQueryAsync();
            }

            await using var conn = new SqliteConnection($"Data Source={file};Pooling=False");
            await conn.OpenAsync();
            var stmts = ScriptSplitter.Split("DELETE FROM t WHERE v = 'a'; SELECT * FROM t WHERE id = 1", DbKind.Sqlite);
            var options = new ScriptOptions(100, ReadOnly: true, Kind: DbKind.Sqlite, Plan: PlanMode.Estimated);

            var plans = new List<QueryPlan?>();
            await foreach (var r in ScriptRunner.RunAsync(conn, stmts, options, CancellationToken.None))
            {
                Assert.Null(r.Error);   // an estimated plan of a write is fine on a read-only connection: nothing runs
                var (display, plan) = PlanCapture.Extract(r, DbKind.Sqlite, PlanMode.Estimated);
                Assert.Empty(display.Sets);
                plans.Add(plan);
            }

            Assert.Equal(2, plans.Count);
            Assert.All(plans, p => Assert.NotNull(p));
            Assert.Contains(plans[0]!.Statements[0].Nodes, n => n.Name == "Table scan");
            Assert.Contains(plans[1]!.Statements[0].Nodes, n => n.Name == "Primary key seek");

            await using var check = conn.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM t";
            Assert.Equal(1L, await check.ExecuteScalarAsync());   // the DELETE did not run
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void Extract_SqlServerSplitsPlanFromRows()
    {
        var rows = new ResultSet(["Id"], [[1L], [2L]], false);
        var xmlSet = new ResultSet(["Microsoft SQL Server 2005 XML Showplan"], [[ActualXml]], false);
        var result = new StatementResult(new ScriptStatement("SELECT 1", 0, 8), [rows, xmlSet], null, TimeSpan.Zero, null);

        var (display, plan) = PlanCapture.Extract(result, DbKind.SqlServer, PlanMode.Actual);

        Assert.Single(display.Sets);
        Assert.Equal("Id", display.Sets[0].Columns[0]);
        Assert.NotNull(plan);
        Assert.Equal(PlanMode.Actual, plan!.Mode);
        Assert.Equal(".sqlplan", plan.FileExtension);
    }

    [Fact]
    public void Extract_FailedStatementKeepsItsError()
    {
        var result = new StatementResult(new ScriptStatement("SELEC", 0, 5), [], null, TimeSpan.Zero, "boom");
        var (display, plan) = PlanCapture.Extract(result, DbKind.SqlServer, PlanMode.Estimated);
        Assert.Same(result, display);
        Assert.Null(plan);
    }

    [Theory]
    [InlineData("SELECT 1", false)]
    [InlineData("  -- c\n/* x */ with a as (select 1) select * from a", false)]
    [InlineData("(SELECT 1)", false)]
    [InlineData("SET search_path = x", true)]
    [InlineData("BEGIN", true)]
    public void Postgres_SomeStatementsHaveNoPlan(string sql, bool refused) =>
        Assert.Equal(refused, PlanCapture.CannotPlan(sql, DbKind.Postgres) is not null);
}
