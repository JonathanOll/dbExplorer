using DbExplorer.Core.Model;

namespace DbExplorer.Core.Plans;

/// <summary>Which plan a run asks the server for.</summary>
public enum PlanMode
{
    /// <summary>A normal run.</summary>
    None,

    /// <summary>The plan the optimizer would use; the statement is not executed (SHOWPLAN_XML, EXPLAIN, EXPLAIN QUERY PLAN).</summary>
    Estimated,

    /// <summary>The plan the statement really ran with, with row counts and times (SQL Server STATISTICS XML, PostgreSQL EXPLAIN ANALYZE).</summary>
    Actual,
}

/// <summary>A name / value pair of an operator or statement (shown in the properties panel).</summary>
public sealed record PlanProperty(string Group, string Name, string Value);

/// <summary>A problem the server reported on an operator or a statement (spill, missing statistics, implicit conversion, ...).</summary>
public sealed record PlanWarning(string Title, string? Detail = null);

/// <summary>An index the optimizer would have liked to have (SQL Server missing-index hint).</summary>
public sealed record MissingIndex(
    double ImpactPercent,
    string Database,
    string Schema,
    string Table,
    IReadOnlyList<string> EqualityColumns,
    IReadOnlyList<string> InequalityColumns,
    IReadOnlyList<string> IncludeColumns)
{
    /// <summary>A CREATE INDEX statement for the hint (key columns: equality first, then inequality).</summary>
    public string CreateScript
    {
        get
        {
            var keys = EqualityColumns.Concat(InequalityColumns).ToList();
            var name = "IX_" + Clean(Table) + "_" + string.Join("_", keys.Select(Clean));
            if (name.Length > 120) name = name[..120];
            var sql = $"CREATE NONCLUSTERED INDEX [{name}]\n    ON {Schema}.{Table} ({string.Join(", ", keys)})";
            if (IncludeColumns.Count > 0) sql += $"\n    INCLUDE ({string.Join(", ", IncludeColumns)})";
            return sql + ";";
        }
    }

    private static string Clean(string identifier) => new(identifier.Where(char.IsLetterOrDigit).ToArray());
}

/// <summary>
/// One operator of an execution plan. Costs are in the engine's own unit (SQL Server: optimizer cost, PostgreSQL: planner cost);
/// SQLite reports none, so every cost there is 0 and <see cref="PlanStatement.HasCost"/> is false.
/// </summary>
public sealed class PlanNode
{
    public required int Id { get; init; }

    /// <summary>The operator as the engine names it ("Index Seek", "Nested Loops", "Hash Join", "Seq Scan").</summary>
    public required string Name { get; init; }

    /// <summary>The logical operation when it differs from <see cref="Name"/> ("Inner Join" for Nested Loops).</summary>
    public string? Logical { get; init; }

    /// <summary>The table or index the operator reads, when it reads one.</summary>
    public string? Object { get; init; }

    public PlanNode? Parent { get; internal set; }

    public List<PlanNode> Children { get; } = [];

    /// <summary>Cost of this operator alone.</summary>
    public double OwnCost { get; set; }

    /// <summary>Cost of this operator and everything below it.</summary>
    public double SubtreeCost { get; set; }

    public double? EstimatedRows { get; set; }

    /// <summary>Rows the operator really produced, summed over its executions; null for an estimated plan.</summary>
    public double? ActualRows { get; set; }

    public double? Executions { get; set; }

    /// <summary>How many times the optimizer expects the operator to run (the inner side of a nested loop runs once per outer row).</summary>
    public double EstimatedExecutions { get; set; } = 1;

    /// <summary>Time spent in this operator and below, in ms (actual plans only).</summary>
    public double? ActualTimeMs { get; set; }

    /// <summary>Time spent in this operator alone (actual plans only).</summary>
    public double? OwnTimeMs { get; set; }

    public double? AverageRowSize { get; set; }

    /// <summary>Pages read by this operator alone (SQL Server logical reads, PostgreSQL shared hit + read blocks); null when not reported.</summary>
    public double? LogicalReads { get; set; }

    public bool Parallel { get; set; }

    public List<PlanProperty> Properties { get; } = [];

    public List<PlanWarning> Warnings { get; } = [];

    /// <summary>The operator can be a problem on its own: a scan where a seek was possible, a table scan on a big table, a spill.</summary>
    public string? Hint { get; set; }

    /// <summary>Share of the statement's total cost spent here, 0 to 1.</summary>
    public double CostShare { get; internal set; }

    /// <summary>Share of the statement's total time spent here, 0 to 1 (actual plans only).</summary>
    public double TimeShare { get; internal set; }

    /// <summary>Actual rows over estimated rows: 1 is a perfect guess. Null when either is unknown or 0.</summary>
    public double? RowRatio => ActualRows is { } a && EstimatedRows is { } e && e > 0 ? a / e : null;

    /// <summary>The estimate is far off the reality (10 times too low or too high): the usual root cause of a bad plan.</summary>
    public bool BadEstimate => RowRatio is { } r && (r >= 10 || r <= 0.1) && Math.Max(ActualRows ?? 0, EstimatedRows ?? 0) >= 100;

    public IEnumerable<PlanNode> Descendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.Descendants())
                yield return d;
    }
}

/// <summary>One statement of a plan: its operator tree and what the server said about it.</summary>
public sealed class PlanStatement
{
    public required string Text { get; init; }

    /// <summary>"SELECT", "INSERT", ... when the engine says it.</summary>
    public string? Type { get; init; }

    public PlanNode? Root { get; set; }

    /// <summary>The root's subtree cost.</summary>
    public double TotalCost { get; set; }

    public bool HasCost { get; set; } = true;

    /// <summary>The plan has actual row counts (an actual plan).</summary>
    public bool HasActuals { get; set; }

    /// <summary>The plan has actual time per operator.</summary>
    public bool HasTimes { get; set; }

    public double? PlanningTimeMs { get; set; }

    public double? ExecutionTimeMs { get; set; }

    /// <summary>Statement-level facts (cached plan size, degree of parallelism, memory grant, optimization level...).</summary>
    public List<PlanProperty> Properties { get; } = [];

    public List<PlanWarning> Warnings { get; } = [];

    public List<MissingIndex> MissingIndexes { get; } = [];

    /// <summary>Position of the script statement this plan belongs to (1-based), set by the run.</summary>
    public int ScriptStatement { get; set; }

    public IEnumerable<PlanNode> Nodes => Root is null ? [] : Root.Descendants();

    /// <summary>The operators by decreasing own cost (by own time when the plan has times and no costs).</summary>
    public IReadOnlyList<PlanNode> TopOperators(int count = 10) =>
        Nodes.OrderByDescending(n => HasCost ? n.OwnCost : n.OwnTimeMs ?? 0).ThenBy(n => n.Id).Take(count).ToList();

    /// <summary>Fills the shares, the parents and the hints once the tree is built.</summary>
    internal void Finish()
    {
        if (Root is null) return;
        var total = Root.SubtreeCost;
        var time = Root.ActualTimeMs ?? 0;
        foreach (var n in Nodes)
        {
            foreach (var c in n.Children) c.Parent = n;
            n.CostShare = total > 0 ? Math.Clamp(n.OwnCost / total, 0, 1) : 0;
            n.TimeShare = time > 0 && n.OwnTimeMs is { } t ? Math.Clamp(t / time, 0, 1) : 0;
        }
        TotalCost = total;
    }
}

/// <summary>A whole plan: one entry per statement.</summary>
public sealed class QueryPlan
{
    public required DbKind Engine { get; init; }

    public required PlanMode Mode { get; init; }

    public List<PlanStatement> Statements { get; } = [];

    /// <summary>The plan as the server sent it (showplan XML, EXPLAIN JSON, or the EXPLAIN QUERY PLAN lines): what Save and Copy write.</summary>
    public required string Raw { get; init; }

    /// <summary>File extension of <see cref="Raw"/> for Save: ".sqlplan" opens in SSMS.</summary>
    public string FileExtension => Engine switch
    {
        DbKind.SqlServer => ".sqlplan",
        DbKind.Postgres => ".json",
        // MySQL's measured plan is a text tree; every other plan of MySQL and MariaDB is JSON.
        DbKind.MySql when Raw.AsSpan().TrimStart().StartsWith("{") => ".json",
        _ => ".txt",
    };

    public string FileKindName => Engine switch
    {
        DbKind.SqlServer => "SQL Server execution plan",
        DbKind.Postgres => "PostgreSQL plan (JSON)",
        DbKind.MySql when FileExtension == ".json" => "MySQL / MariaDB plan (JSON)",
        _ => "Query plan (text)",
    };
}
