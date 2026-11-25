using System.Globalization;
using System.Text.Json;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Plans;

/// <summary>Reads the output of <c>EXPLAIN (FORMAT JSON)</c> (with or without ANALYZE) into a <see cref="QueryPlan"/>.</summary>
public static class PostgresPlanParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool LooksLikePlan(string? text) =>
        text is not null && text.AsSpan().TrimStart() is { Length: > 0 } t && t[0] == '[' && text.Contains("\"Plan\"", StringComparison.Ordinal);

    public static QueryPlan Parse(string json, PlanMode mode, string statementText)
    {
        using var doc = JsonDocument.Parse(json);
        var plan = new QueryPlan { Engine = DbKind.Postgres, Mode = mode, Raw = json };
        var entries = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        foreach (var entry in entries)
        {
            if (!entry.TryGetProperty("Plan", out var root)) continue;
            var statement = new PlanStatement { Text = statementText.Trim() };
            var ids = 0;
            statement.Root = ReadNode(root, ref ids);
            statement.HasActuals = statement.Nodes.Any(n => n.ActualRows is not null);
            statement.HasTimes = statement.Nodes.Any(n => n.ActualTimeMs is not null);
            if (statement.HasTimes) FillOwnTimes(statement.Root);

            if (entry.TryGetProperty("Planning Time", out var pt) && pt.TryGetDouble(out var planning))
            {
                statement.PlanningTimeMs = planning;
                statement.Properties.Add(new PlanProperty("Time", "Planning time", $"{planning.ToString("0.###", Inv)} ms"));
            }
            if (entry.TryGetProperty("Execution Time", out var xt) && xt.TryGetDouble(out var execution))
            {
                statement.ExecutionTimeMs = execution;
                statement.Properties.Add(new PlanProperty("Time", "Execution time", $"{execution.ToString("0.###", Inv)} ms"));
            }
            if (entry.TryGetProperty("Triggers", out var triggers) && triggers.ValueKind == JsonValueKind.Array)
                foreach (var t in triggers.EnumerateArray())
                    statement.Properties.Add(new PlanProperty("Triggers", Str(t, "Trigger Name") ?? "trigger",
                        $"{Str(t, "Time") ?? "?"} ms, {Str(t, "Calls") ?? "?"} calls"));
            if (entry.TryGetProperty("JIT", out var jit) && jit.ValueKind == JsonValueKind.Object)
                foreach (var p in jit.EnumerateObject())
                    if (p.Value.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                        statement.Properties.Add(new PlanProperty("JIT", p.Name, p.Value.ToString()));

            statement.Finish();
            foreach (var n in statement.Nodes) AddHints(n);
            plan.Statements.Add(statement);
        }
        return plan;
    }

    private static readonly HashSet<string> Structural = ["Plans", "Node Type", "Parent Relationship", "Subplan Name"];

    private static PlanNode ReadNode(JsonElement e, ref int ids)
    {
        var id = ++ids;
        var type = Str(e, "Node Type") ?? "Node";
        var join = Str(e, "Join Type");
        var strategy = Str(e, "Strategy");
        var logical = join is not null ? join + " join" : strategy;

        var obj = Str(e, "Relation Name") ?? Str(e, "CTE Name") ?? Str(e, "Function Name");
        if (obj is not null && Str(e, "Schema") is { } schema) obj = schema + "." + obj;
        if (Str(e, "Index Name") is { } index) obj = obj is null ? index : $"{obj} ({index})";
        if (Str(e, "Alias") is { } alias && alias != Str(e, "Relation Name")) obj = obj is null ? alias : $"{obj} as {alias}";

        var node = new PlanNode { Id = id, Name = type, Logical = logical, Object = obj };
        var loops = Number(e, "Actual Loops");
        var hasActual = e.TryGetProperty("Actual Rows", out _);

        node.SubtreeCost = Number(e, "Total Cost") ?? 0;
        node.AverageRowSize = Number(e, "Plan Width");
        node.Parallel = e.TryGetProperty("Parallel Aware", out var pa) && pa.ValueKind == JsonValueKind.True;
        if (hasActual)
        {
            var perLoop = Number(e, "Actual Rows") ?? 0;
            node.Executions = loops ?? 1;
            node.ActualRows = perLoop * (loops ?? 1);
            node.EstimatedExecutions = loops ?? 1;
            if (Number(e, "Actual Total Time") is { } t) node.ActualTimeMs = t * (loops ?? 1);
        }
        node.EstimatedRows = (Number(e, "Plan Rows") ?? 0) * (hasActual ? loops ?? 1 : 1);

        if (e.TryGetProperty("Plans", out var plans) && plans.ValueKind == JsonValueKind.Array)
            foreach (var c in plans.EnumerateArray())
            {
                var child = ReadNode(c, ref ids);
                if (Str(c, "Subplan Name") is { } sub) child.Properties.Insert(0, new PlanProperty("Operator", "Subplan", sub));
                node.Children.Add(child);
            }

        // The planner's cost of a node includes its children; a Limit can cost less than what it reads, hence the clamp.
        node.OwnCost = Math.Max(0, node.SubtreeCost - node.Children.Sum(c => c.SubtreeCost));

        node.Properties.Add(new PlanProperty("Operator", "Node type", type));
        if (join is not null) node.Properties.Add(new PlanProperty("Operator", "Join type", join));
        if (Str(e, "Parent Relationship") is { } rel) node.Properties.Add(new PlanProperty("Operator", "Relationship", rel));
        node.Properties.Add(new PlanProperty("Costs", "Startup cost", ShowplanParser.FormatCost(Number(e, "Startup Cost") ?? 0)));
        node.Properties.Add(new PlanProperty("Costs", "Total cost", ShowplanParser.FormatCost(node.SubtreeCost)));
        node.Properties.Add(new PlanProperty("Costs", "Own cost", ShowplanParser.FormatCost(node.OwnCost)));
        node.Properties.Add(new PlanProperty("Rows", "Estimated rows per loop", ShowplanParser.FormatNumber(Number(e, "Plan Rows") ?? 0)));
        if (hasActual)
        {
            node.Properties.Add(new PlanProperty("Rows", "Actual rows per loop", ShowplanParser.FormatNumber(Number(e, "Actual Rows") ?? 0)));
            node.Properties.Add(new PlanProperty("Rows", "Actual rows (all loops)", ShowplanParser.FormatNumber(node.ActualRows ?? 0)));
            node.Properties.Add(new PlanProperty("Rows", "Loops", ShowplanParser.FormatNumber(loops ?? 1)));
            if (Number(e, "Actual Startup Time") is { } st)
                node.Properties.Add(new PlanProperty("Time", "Actual startup time", $"{st.ToString("0.###", Inv)} ms"));
            if (Number(e, "Actual Total Time") is { } tt)
                node.Properties.Add(new PlanProperty("Time", "Actual time per loop", $"{tt.ToString("0.###", Inv)} ms"));
        }

        foreach (var p in e.EnumerateObject())
        {
            if (Structural.Contains(p.Name) || Known.Contains(p.Name)) continue;
            var value = p.Value.ValueKind switch
            {
                JsonValueKind.Array => string.Join(", ", p.Value.EnumerateArray().Select(v => v.ToString())),
                JsonValueKind.Object => null,
                _ => p.Value.ToString(),
            };
            if (!string.IsNullOrWhiteSpace(value)) node.Properties.Add(new PlanProperty(GroupOf(p.Name), p.Name, value));
        }
        return node;
    }

    private static readonly HashSet<string> Known =
    [
        "Startup Cost", "Total Cost", "Plan Rows", "Plan Width", "Actual Rows", "Actual Loops", "Actual Startup Time", "Actual Total Time",
        "Join Type", "Parallel Aware",
    ];

    private static string GroupOf(string name) =>
        name.EndsWith("Blocks", StringComparison.Ordinal) || name.Contains("Buffers", StringComparison.Ordinal) ? "Buffers"
        : name.Contains("Filter", StringComparison.Ordinal) || name.EndsWith("Cond", StringComparison.Ordinal) || name.Contains("Recheck", StringComparison.Ordinal) ? "Predicates"
        : name is "Output" ? "Output"
        : name.StartsWith("Relation", StringComparison.Ordinal) || name is "Schema" or "Alias" or "Index Name" or "CTE Name" or "Function Name" ? "Object"
        : "Details";

    private static void FillOwnTimes(PlanNode node)
    {
        foreach (var c in node.Children) FillOwnTimes(c);
        if (node.ActualTimeMs is { } t) node.OwnTimeMs = Math.Max(0, t - node.Children.Sum(c => c.ActualTimeMs ?? 0));
    }

    private static void AddHints(PlanNode n)
    {
        string? P(string name) => n.Properties.FirstOrDefault(p => p.Name == name)?.Value;
        double? Removed() => double.TryParse(P("Rows Removed by Filter")?.Replace(",", ""), NumberStyles.Float, Inv, out var v) ? v : null;

        if (n.Name == "Seq Scan" && P("Filter") is not null)
        {
            var removed = (Removed() ?? 0) * Math.Max(1, n.Executions ?? 1);
            if (removed >= 1000 && removed > 10 * (n.ActualRows ?? 0))
                n.Hint = $"Sequential scan that throws away {ShowplanParser.FormatNumber(removed)} rows to keep {ShowplanParser.FormatNumber(n.ActualRows ?? 0)}. An index on the filter column would help.";
            else if (n.ActualRows is null)
                n.Hint = "Sequential scan with a filter: every row is read. An index on the filter column may avoid it on a big table.";
        }
        else if (n.Name == "Seq Scan" && n.EstimatedRows > 100_000)
        {
            n.Hint = "Sequential scan of a big table.";
        }

        if (P("Sort Method") is { } method && method.Contains("external", StringComparison.OrdinalIgnoreCase))
            n.Warnings.Add(new PlanWarning("Sort spilled to disk", $"{method}, {P("Sort Space Used") ?? "?"} kB on {P("Sort Space Type") ?? "disk"}. Raise work_mem or sort fewer rows."));
        if (P("Hash Batches") is { } batches && double.TryParse(batches, NumberStyles.Float, Inv, out var b) && b > 1)
            n.Warnings.Add(new PlanWarning("Hash spilled to disk", $"{batches} batches. Raise work_mem."));
        if (P("Heap Fetches") is { } fetches && n.Name == "Index Only Scan" && double.TryParse(fetches, NumberStyles.Float, Inv, out var f) && f > 1000)
            n.Warnings.Add(new PlanWarning("Index only scan went to the table", $"{ShowplanParser.FormatNumber(f)} heap fetches: VACUUM the table so its visibility map is current."));
        if (n.BadEstimate && !n.Children.Any(c => c.BadEstimate))
            n.Warnings.Add(new PlanWarning("Wrong row estimate",
                $"Estimated {ShowplanParser.FormatNumber(n.EstimatedRows ?? 0)} rows, got {ShowplanParser.FormatNumber(n.ActualRows ?? 0)}. Run ANALYZE on the table, or check the predicate."));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}
