using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Plans;

/// <summary>
/// Reads SQL Server showplan XML (what SHOWPLAN_XML and STATISTICS XML return, and what a .sqlplan file holds) into a
/// <see cref="QueryPlan"/>. Tolerant: an element or attribute it does not know is skipped, never a reason to show no plan.
/// Matching is on local names, so the schema version does not matter.
/// </summary>
public static partial class ShowplanParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>True when <paramref name="text"/> looks like showplan XML.</summary>
    public static bool LooksLikeShowplan(string? text) =>
        text is not null && text.AsSpan().TrimStart().StartsWith("<ShowPlanXML", StringComparison.Ordinal);

    public static QueryPlan Parse(string xml, PlanMode mode)
    {
        var doc = XDocument.Parse(xml, LoadOptions.None);
        var plan = new QueryPlan { Engine = DbKind.SqlServer, Mode = mode, Raw = xml };

        foreach (var stmt in doc.Descendants().Where(e => e.Name.LocalName.StartsWith("Stmt", StringComparison.Ordinal)))
        {
            var queryPlan = Child(stmt, "QueryPlan");
            if (queryPlan is null) continue;

            var statement = new PlanStatement
            {
                Text = Attr(stmt, "StatementText")?.Trim() ?? "",
                Type = Attr(stmt, "StatementType"),
            };
            ReadStatement(statement, stmt, queryPlan);
            if (Child(queryPlan, "RelOp") is { } root)
            {
                var ids = 0;
                statement.Root = ReadRelOp(root, ref ids);
                statement.HasActuals = statement.Nodes.Any(n => n.ActualRows is not null);
                statement.HasTimes = statement.Nodes.Any(n => n.ActualTimeMs is not null);
                if (statement.HasTimes) FillOwnTimes(statement.Root);
                var estimatedAll = statement.HasActuals;
                if (estimatedAll)
                    foreach (var n in statement.Nodes)
                        if (n.EstimatedRows is { } e) n.EstimatedRows = e * n.EstimatedExecutions;
                statement.Finish();
                foreach (var n in statement.Nodes) AddHints(n);
            }
            plan.Statements.Add(statement);
        }
        return plan;
    }

    private static void ReadStatement(PlanStatement s, XElement stmt, XElement qp)
    {
        void Add(string group, string name, string? value, string unit = "")
        {
            if (!string.IsNullOrEmpty(value)) s.Properties.Add(new PlanProperty(group, name, value + unit));
        }

        Add("Statement", "Statement type", Attr(stmt, "StatementType"));
        Add("Statement", "Estimated rows", Attr(stmt, "StatementEstRows") is { } r ? FormatNumber(Number(r) ?? 0) : null);
        Add("Statement", "Estimated subtree cost", Attr(stmt, "StatementSubTreeCost") is { } c ? FormatCost(Number(c) ?? 0) : null);
        Add("Statement", "Optimization level", Attr(stmt, "StatementOptmLevel"));
        Add("Statement", "Early abort reason", Attr(stmt, "StatementOptmEarlyAbortReason"));
        Add("Statement", "Cardinality estimation model", Attr(stmt, "CardinalityEstimationModelVersion"));
        Add("Statement", "Query hash", Attr(stmt, "QueryHash"));
        Add("Statement", "Query plan hash", Attr(stmt, "QueryPlanHash"));
        Add("Plan", "Degree of parallelism", Attr(qp, "DegreeOfParallelism"));
        Add("Plan", "Non-parallel reason", Attr(qp, "NonParallelPlanReason"));
        Add("Plan", "Cached plan size", Attr(qp, "CachedPlanSize"), " KB");
        Add("Plan", "Compile time", Attr(qp, "CompileTime"), " ms");
        Add("Plan", "Compile CPU", Attr(qp, "CompileCPU"), " ms");
        Add("Plan", "Compile memory", Attr(qp, "CompileMemory"), " KB");
        Add("Plan", "Memory grant", Attr(qp, "MemoryGrant"), " KB");

        if (Child(qp, "MemoryGrantInfo") is { } grant)
        {
            Add("Memory grant", "Requested", Attr(grant, "RequestedMemory"), " KB");
            Add("Memory grant", "Granted", Attr(grant, "GrantedMemory"), " KB");
            Add("Memory grant", "Max used", Attr(grant, "MaxUsedMemory"), " KB");
            Add("Memory grant", "Desired", Attr(grant, "DesiredMemory"), " KB");
            Add("Memory grant", "Wait time", Attr(grant, "GrantWaitTime"), " ms");
        }
        if (Child(qp, "QueryTimeStats") is { } times)
        {
            if (Attr(times, "ElapsedTime") is { } el && double.TryParse(el, NumberStyles.Float, Inv, out var ms)) s.ExecutionTimeMs = ms;
            Add("Time", "Elapsed", Attr(times, "ElapsedTime"), " ms");
            Add("Time", "CPU", Attr(times, "CpuTime"), " ms");
            Add("Time", "UDF elapsed", Attr(times, "UdfElapsedTime"), " ms");
        }
        if (Child(qp, "ParameterList") is { } parameters)
            foreach (var p in parameters.Elements())
                if (Attr(p, "Column") is { } name)
                {
                    var compiled = Attr(p, "ParameterCompiledValue");
                    var runtime = Attr(p, "ParameterRuntimeValue");
                    var value = compiled is not null && runtime is not null && compiled != runtime
                        ? $"compiled {compiled}, runtime {runtime}"
                        : compiled ?? runtime ?? "";
                    s.Properties.Add(new PlanProperty("Parameters", name, value));
                }

        if (Child(qp, "Warnings") is { } warnings) s.Warnings.AddRange(ReadWarnings(warnings));

        foreach (var group in qp.Descendants().Where(e => e.Name.LocalName == "MissingIndexGroup"))
        {
            var impact = Number(Attr(group, "Impact"));
            foreach (var index in group.Elements().Where(e => e.Name.LocalName == "MissingIndex"))
            {
                var eq = new List<string>();
                var ineq = new List<string>();
                var include = new List<string>();
                foreach (var columns in index.Elements().Where(e => e.Name.LocalName == "ColumnGroup"))
                {
                    var target = Attr(columns, "Usage") switch { "EQUALITY" => eq, "INEQUALITY" => ineq, _ => include };
                    target.AddRange(columns.Elements().Select(c => Attr(c, "Name")).OfType<string>());
                }
                s.MissingIndexes.Add(new MissingIndex(impact ?? 0, Attr(index, "Database") ?? "", Attr(index, "Schema") ?? "",
                    Attr(index, "Table") ?? "", eq, ineq, include));
            }
        }
    }

    // ---- operators ----

    private static readonly HashSet<string> NotChildren = ["RunTimeInformation", "Warnings", "OutputList", "MemoryFractions", "RunTimePartitionSummary", "InternalInfo"];

    private static PlanNode ReadRelOp(XElement relOp, ref int ids)
    {
        var physical = Attr(relOp, "PhysicalOp") ?? "Operator";
        var logical = Attr(relOp, "LogicalOp");
        var op = relOp.Elements().FirstOrDefault(e => !NotChildren.Contains(e.Name.LocalName));

        var node = new PlanNode
        {
            Id = Attr(relOp, "NodeId") is { } id && int.TryParse(id, out var n) ? n : ++ids,
            Name = physical,
            Logical = logical is not null && !string.Equals(logical, physical, StringComparison.OrdinalIgnoreCase) ? logical : null,
            Object = op is null ? null : ObjectLabel(op),
        };
        ids = Math.Max(ids, node.Id);

        node.SubtreeCost = Number(Attr(relOp, "EstimatedTotalSubtreeCost")) ?? 0;
        node.EstimatedRows = Number(Attr(relOp, "EstimateRows") ?? Attr(relOp, "EstimatedRows"));
        node.AverageRowSize = Number(Attr(relOp, "AvgRowSize"));
        node.Parallel = Attr(relOp, "Parallel") is "1" or "true";

        foreach (var child in RelOpChildren(relOp))
            node.Children.Add(ReadRelOp(child, ref ids));

        var childCost = node.Children.Sum(c => c.SubtreeCost);
        node.OwnCost = Math.Max(0, node.SubtreeCost - childCost);

        ReadRuntime(node, relOp);
        ReadProperties(node, relOp, op);
        if (Child(relOp, "Warnings") is { } w) node.Warnings.AddRange(ReadWarnings(w));
        return node;
    }

    /// <summary>The operators directly below <paramref name="relOp"/>: the RelOp elements inside the operator element, without entering a RelOp.</summary>
    private static IEnumerable<XElement> RelOpChildren(XElement relOp)
    {
        foreach (var e in relOp.Elements().Where(e => !NotChildren.Contains(e.Name.LocalName)))
            foreach (var found in FindRelOps(e))
                yield return found;
    }

    private static IEnumerable<XElement> FindRelOps(XElement e)
    {
        if (e.Name.LocalName == "RelOp")
        {
            yield return e;
            yield break;
        }
        foreach (var c in e.Elements())
            foreach (var found in FindRelOps(c))
                yield return found;
    }

    /// <summary>The Object element of an operator: inside its own element, never inside an operator below it.</summary>
    private static XElement? FindObject(XElement op)
    {
        foreach (var e in op.Elements())
        {
            if (e.Name.LocalName == "RelOp") continue;
            if (e.Name.LocalName == "Object") return e;
            if (FindObject(e) is { } found) return found;
        }
        return null;
    }

    private static string? ObjectLabel(XElement op)
    {
        var obj = FindObject(op);
        if (obj is null) return null;
        var table = Attr(obj, "Table");
        if (table is null) return null;
        var index = Attr(obj, "Index");
        var label = index is null ? table : $"{table}.{index}";
        var alias = Attr(obj, "Alias");
        return alias is not null && alias != table ? $"{label} ({alias})" : label;
    }

    private static void ReadRuntime(PlanNode node, XElement relOp)
    {
        var counters = Child(relOp, "RunTimeInformation")?.Elements().Where(e => e.Name.LocalName == "RunTimeCountersPerThread").ToList();
        if (counters is not { Count: > 0 }) return;

        double Sum(string name) => counters.Sum(c => Number(Attr(c, name)) ?? 0);
        bool Has(string name) => counters.Any(c => Attr(c, name) is not null);

        node.ActualRows = Sum("ActualRows");
        node.Executions = Sum("ActualExecutions");
        if (Has("ActualLogicalReads")) node.LogicalReads = Sum("ActualLogicalReads") + Sum("ActualLobLogicalReads");
        if (Has("ActualElapsedms")) node.ActualTimeMs = counters.Max(c => Number(Attr(c, "ActualElapsedms")) ?? 0);

        void Prop(string group, string name, string attr, string unit = "")
        {
            if (Has(attr)) node.Properties.Add(new PlanProperty(group, name, FormatNumber(Sum(attr)) + unit));
        }
        Prop("Rows", "Actual rows read", "ActualRowsRead");
        Prop("Rows", "Actual batches", "Batches");
        Prop("Time", "Actual CPU time", "ActualCPUms", " ms");
        if (node.ActualTimeMs is { } et) node.Properties.Add(new PlanProperty("Time", "Actual elapsed time", FormatNumber(et) + " ms"));
        Prop("I/O", "Actual scans", "ActualScans");
        Prop("I/O", "Actual logical reads", "ActualLogicalReads");
        Prop("I/O", "Actual physical reads", "ActualPhysicalReads");
        Prop("I/O", "Actual read-aheads", "ActualReadAheads");
        Prop("I/O", "Actual LOB logical reads", "ActualLobLogicalReads");
        Prop("I/O", "Actual LOB physical reads", "ActualLobPhysicalReads");
        if (counters.Count > 1) node.Properties.Add(new PlanProperty("Rows", "Threads", counters.Count(c => (Number(Attr(c, "ActualRows")) ?? 0) > 0).ToString(Inv)));
    }

    private static void FillOwnTimes(PlanNode node)
    {
        foreach (var c in node.Children) FillOwnTimes(c);
        if (node.ActualTimeMs is { } t) node.OwnTimeMs = Math.Max(0, t - node.Children.Sum(c => c.ActualTimeMs ?? 0));
    }

    private static void ReadProperties(PlanNode node, XElement relOp, XElement? op)
    {
        void Add(string group, string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) node.Properties.Add(new PlanProperty(group, name, value));
        }

        Add("Operator", "Physical operation", node.Name);
        Add("Operator", "Logical operation", Attr(relOp, "LogicalOp"));
        Add("Operator", "Execution mode", Attr(relOp, "EstimatedExecutionMode"));
        Add("Operator", "Actual execution mode", Child(relOp, "RunTimeInformation")?.Elements().Select(e => Attr(e, "ActualExecutionMode")).FirstOrDefault(v => v is not null));
        Add("Operator", "Parallel", node.Parallel ? "Yes" : null);
        Add("Operator", "Node id", node.Id.ToString(Inv));

        Add("Costs", "Estimated operator cost", FormatCost(node.OwnCost));
        Add("Costs", "Estimated I/O cost", Attr(relOp, "EstimateIO") is { } io ? FormatCost(Number(io) ?? 0) : null);
        Add("Costs", "Estimated CPU cost", Attr(relOp, "EstimateCPU") is { } cpu ? FormatCost(Number(cpu) ?? 0) : null);
        Add("Costs", "Estimated subtree cost", FormatCost(node.SubtreeCost));

        Add("Rows", "Estimated rows per execution", node.EstimatedRows is { } er ? FormatNumber(er) : null);
        if (Attr(relOp, "EstimateRebinds") is { } rebinds && Number(rebinds) is { } rb)
        {
            var rewinds = Number(Attr(relOp, "EstimateRewinds")) ?? 0;
            Add("Rows", "Estimated rebinds", FormatNumber(rb));
            Add("Rows", "Estimated rewinds", FormatNumber(rewinds));
            node.EstimatedExecutions = 1 + rb;
        }
        Add("Rows", "Estimated row size", node.AverageRowSize is { } sz ? FormatNumber(sz) + " B" : null);
        Add("Rows", "Table cardinality", Attr(relOp, "TableCardinality") is { } tc ? FormatNumber(Number(tc) ?? 0) : null);
        Add("Rows", "Estimated rows to be read", Attr(relOp, "EstimatedRowsRead") is { } rr ? FormatNumber(Number(rr) ?? 0) : null);
        if (node.ActualRows is { } ar) Add("Rows", "Actual rows", FormatNumber(ar));
        if (node.Executions is { } ex) Add("Rows", "Actual executions", FormatNumber(ex));

        if (op is not null)
        {
            var obj = FindObject(op);
            if (obj is not null)
            {
                Add("Object", "Database", Attr(obj, "Database"));
                Add("Object", "Schema", Attr(obj, "Schema"));
                Add("Object", "Table", Attr(obj, "Table"));
                Add("Object", "Index", Attr(obj, "Index"));
                Add("Object", "Index kind", Attr(obj, "IndexKind"));
                Add("Object", "Alias", Attr(obj, "Alias"));
            }

            foreach (var a in op.Attributes().Where(a => !a.IsNamespaceDeclaration))
                Add("Details", Humanize(a.Name.LocalName), a.Value);

            foreach (var part in op.Elements().Where(e => e.Name.LocalName is not ("Object" or "RelOp")))
            {
                var text = Describe(part);
                if (!string.IsNullOrWhiteSpace(text)) Add(PartGroup(part.Name.LocalName), Humanize(part.Name.LocalName), text);
            }
        }

        if (Child(relOp, "OutputList") is { } output)
        {
            var columns = output.Elements().Select(ColumnName).Where(c => c is not null).ToList();
            if (columns.Count > 0)
                Add("Output", "Output list", string.Join(", ", columns.Take(60)) + (columns.Count > 60 ? $" … (+{columns.Count - 60})" : ""));
        }
    }

    private static string PartGroup(string name) =>
        name.Contains("Predicate", StringComparison.Ordinal) || name.Contains("Residual", StringComparison.Ordinal) ? "Predicates" : "Details";

    /// <summary>The scalar text of an operator part: the topmost ScalarString, else the column references.</summary>
    private static string? Describe(XElement part)
    {
        var strings = new List<string>();
        void Walk(XElement e)
        {
            if (e.Name.LocalName == "RelOp") return;
            if (Attr(e, "ScalarString") is { } s && e.Name.LocalName == "ScalarOperator")
            {
                strings.Add(s);
                return;
            }
            foreach (var c in e.Elements()) Walk(c);
        }
        Walk(part);
        if (strings.Count > 0) return string.Join("\n", strings.Take(30));

        var columns = new List<string>();
        void WalkColumns(XElement e)
        {
            if (e.Name.LocalName == "RelOp") return;
            if (e.Name.LocalName == "ColumnReference" && ColumnName(e) is { } name) columns.Add(name);
            foreach (var c in e.Elements()) WalkColumns(c);
        }
        WalkColumns(part);
        return columns.Count == 0 ? null : string.Join(", ", columns.Distinct().Take(60));
    }

    private static string? ColumnName(XElement e)
    {
        var column = Attr(e, "Column");
        if (column is null) return null;
        var table = Attr(e, "Table");
        return table is null ? column : $"{table}.[{column.Trim('[', ']')}]";
    }

    // ---- warnings and hints ----

    private static IEnumerable<PlanWarning> ReadWarnings(XElement warnings)
    {
        foreach (var a in warnings.Attributes().Where(a => !a.IsNamespaceDeclaration))
        {
            if (a.Value is "true" or "1") yield return new PlanWarning(Humanize(a.Name.LocalName), WarningText(a.Name.LocalName));
        }
        foreach (var e in warnings.Elements())
        {
            var detail = string.Join(", ", e.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => $"{Humanize(a.Name.LocalName)} {a.Value}")
                .Concat(e.Elements().Select(c => Attr(c, "Column") ?? Attr(c, "ConvertIssue") ?? "")).Where(s => s.Length > 0));
            yield return new PlanWarning(Humanize(e.Name.LocalName), detail.Length > 0 ? detail : WarningText(e.Name.LocalName));
        }
    }

    private static string? WarningText(string name) => name switch
    {
        "NoJoinPredicate" => "A join has no predicate: every row meets every row (a cross product).",
        "ColumnsWithNoStatistics" => "The optimizer had no statistics on these columns, so its row estimates are guesses.",
        "ColumnsWithStaleStatistics" => "The statistics on these columns are out of date.",
        "SpillToTempDb" => "The operator ran out of memory and wrote to tempdb.",
        "SortSpillDetails" => "The sort ran out of memory and wrote to tempdb.",
        "HashSpillDetails" => "The hash ran out of memory and wrote to tempdb.",
        "PlanAffectingConvert" => "An implicit conversion changes the plan (an index may not be usable).",
        "UnmatchedIndexes" => "A filtered index could not be used because of the parameterization.",
        _ => null,
    };

    private static void AddHints(PlanNode node)
    {
        var name = node.Name;
        var hasPredicate = node.Properties.Any(p => p.Name is "Predicate");
        var seek = node.Properties.Any(p => p.Name is "Seek predicates");
        if (name is "Table Scan" or "Clustered Index Scan" or "Index Scan" or "Clustered Index Seek" or "Index Seek" or "Columnstore Index Scan" or "RID Lookup" or "Key Lookup")
        {
            if (name is "Key Lookup" or "RID Lookup" || node.Logical is "Key Lookup" or "RID Lookup")
                node.Hint = "Lookup: the index used does not cover the query. Add the looked-up columns to it as INCLUDE columns to remove this operator.";
            else if (name.Contains("Scan", StringComparison.Ordinal) && !seek && hasPredicate)
                node.Hint = "Scan with a predicate: every row is read and filtered. An index on the filtered columns could turn this into a seek.";
            else if (name == "Table Scan")
                node.Hint = "Table scan: the table has no clustered index, so every row is read.";
        }
        // Only where the error starts: a wrong estimate below carries up through every join and would flag the whole tree.
        if (node.BadEstimate && !node.Children.Any(c => c.BadEstimate))
            node.Warnings.Add(new PlanWarning("Wrong row estimate",
                $"Estimated {FormatNumber(node.EstimatedRows ?? 0)} rows, got {FormatNumber(node.ActualRows ?? 0)}. Out-of-date statistics or a predicate the optimizer cannot estimate."));
    }

    // ---- helpers ----

    private static XElement? Child(XElement e, string localName) => e.Elements().FirstOrDefault(c => c.Name.LocalName == localName);

    private static string? Attr(XElement e, string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    private static double? Number(string? s) => s is not null && double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : null;

    internal static string FormatNumber(double v) => v >= 100 || v == Math.Floor(v) ? v.ToString("N0", Inv) : v.ToString("0.###", Inv);

    internal static string FormatCost(double v) => v == 0 ? "0" : v < 0.0001 ? v.ToString("0.#####E+0", Inv) : v.ToString("0.#######", Inv);

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex Camel();

    private static string Humanize(string name)
    {
        var text = Camel().Replace(name, " ");
        return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant().Replace(" i d", " ID").Replace(" o s", " OS");
    }
}
