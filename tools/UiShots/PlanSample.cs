using System.Globalization;
using System.Text;

namespace UiShots;

/// <summary>A showplan document for the screenshots: a join of orders and customers, grouped and sorted, with a bad estimate and a missing index.</summary>
internal static class PlanSample
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Op(int id, string physical, string logical, double estRows, double subtree, double actualRows, double ms, string inner, string? obj = null,
        double executions = 1, double rebinds = 0, string extra = "") =>
        $"""
        <RelOp NodeId="{id}" PhysicalOp="{physical}" LogicalOp="{logical}" EstimateRows="{estRows.ToString(Inv)}" EstimateIO="0.01" EstimateCPU="0.02" AvgRowSize="48"
               EstimatedTotalSubtreeCost="{subtree.ToString(Inv)}" Parallel="0" EstimateRebinds="{rebinds.ToString(Inv)}" EstimateRewinds="0" EstimatedExecutionMode="Row">
          <OutputList><ColumnReference Column="id"/></OutputList>
          {extra}
          <RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="{actualRows.ToString(Inv)}" ActualExecutions="{executions.ToString(Inv)}" ActualElapsedms="{ms.ToString(Inv)}" ActualCPUms="{(ms * 0.8).ToString(Inv)}" ActualLogicalReads="{(actualRows / 4).ToString("0", Inv)}"/></RunTimeInformation>
          {inner}{(obj is null ? "" : "")}
        </RelOp>
        """;

    public static string Xml()
    {
        var scanOrders = Op(4, "Clustered Index Scan", "Clustered Index Scan", 1200, 3.3, 84_000, 640,
            """
            <IndexScan Ordered="false" ForcedIndex="false"><Object Database="[Shop]" Schema="[dbo]" Table="[Orders]" Index="[PK_Orders]" Alias="[o]" IndexKind="Clustered"/>
              <Predicate><ScalarOperator ScalarString="[Shop].[dbo].[Orders].[Status] as [o].[Status]&lt;&gt;'open' AND [o].[Total]&gt;(100)"><Compare/></ScalarOperator></Predicate></IndexScan>
            """);
        var seekCustomers = Op(5, "Index Seek", "Index Seek", 1, 0.6, 84_000, 310,
            """
            <IndexScan><Object Database="[Shop]" Schema="[dbo]" Table="[Customers]" Index="[PK_Customers]" Alias="[c]" IndexKind="Clustered"/>
              <SeekPredicates><SeekPredicateNew><SeekKeys><Prefix ScanType="EQ"><RangeColumns><ColumnReference Column="id"/></RangeColumns><RangeExpressions><ScalarOperator ScalarString="[o].[customer_id]"/></RangeExpressions></Prefix></SeekKeys></SeekPredicateNew></SeekPredicates></IndexScan>
            """, executions: 84_000, rebinds: 1199);
        var join = Op(3, "Nested Loops", "Inner Join", 1200, 4.5, 84_000, 1_180,
            $"<NestedLoops Optimized=\"false\">{scanOrders}{seekCustomers}</NestedLoops>");
        var compute = Op(2, "Compute Scalar", "Compute Scalar", 1200, 4.52, 84_000, 1_210,
            $"<ComputeScalar><DefinedValues><DefinedValue><ColumnReference Column=\"Expr1004\"/><ScalarOperator ScalarString=\"[o].[Total]*(1.0)\"/></DefinedValue></DefinedValues>{join}</ComputeScalar>");
        var aggregate = Op(1, "Hash Match", "Aggregate", 300, 5.1, 6_200, 1_520,
            $"<Hash><DefinedValues/><HashKeysBuild><ColumnReference Column=\"id\"/></HashKeysBuild>{compute}</Hash>");
        var sort = Op(0, "Sort", "TopN Sort", 8, 5.12, 8, 1_560,
            $"<TopSort Rows=\"8\" Distinct=\"false\"><OrderBy><OrderByColumn Ascending=\"false\"><ColumnReference Column=\"total\"/></OrderByColumn></OrderBy>{aggregate}</TopSort>",
            extra: "<Warnings><SpillToTempDb SpillLevel=\"1\" SpilledThreadCount=\"1\"/></Warnings>");

        var sb = new StringBuilder();
        sb.Append("""
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.6"><BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT TOP 8 c.name, SUM(o.total) AS total&#xA;FROM orders o JOIN customers c ON c.id = o.customer_id&#xA;WHERE o.status &lt;&gt; 'open' AND o.total &gt; 100&#xA;GROUP BY c.id, c.name ORDER BY total DESC" StatementId="1" StatementType="SELECT" StatementSubTreeCost="5.12" StatementEstRows="8" StatementOptmLevel="FULL" CardinalityEstimationModelVersion="160">
              <QueryPlan DegreeOfParallelism="1" CachedPlanSize="48" CompileTime="9" CompileCPU="9" CompileMemory="312">
                <MissingIndexes><MissingIndexGroup Impact="71.4"><MissingIndex Database="[Shop]" Schema="[dbo]" Table="[Orders]">
                  <ColumnGroup Usage="EQUALITY"><Column Name="[Status]" ColumnId="3"/></ColumnGroup>
                  <ColumnGroup Usage="INEQUALITY"><Column Name="[Total]" ColumnId="4"/></ColumnGroup>
                  <ColumnGroup Usage="INCLUDE"><Column Name="[customer_id]" ColumnId="2"/></ColumnGroup>
                </MissingIndex></MissingIndexGroup></MissingIndexes>
                <QueryTimeStats ElapsedTime="1562" CpuTime="1290"/>
            """);
        sb.Append(sort);
        sb.Append("</QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>");
        return sb.ToString();
    }
}
