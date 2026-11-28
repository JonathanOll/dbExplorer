using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Plans;

/// <summary>
/// Reads the plans of MySQL and MariaDB into a <see cref="QueryPlan"/>: <c>EXPLAIN FORMAT=JSON</c> (both engines, their
/// shapes differ, and MySQL's second JSON format), MariaDB's <c>ANALYZE FORMAT=JSON</c> (the same tree with <c>r_</c>
/// figures) and MySQL's <c>EXPLAIN ANALYZE</c>, which is a text tree.
/// </summary>
public static partial class MySqlPlanParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Properties of a query block that hold operators; anything else is a figure or a description.
    private static readonly Dictionary<string, string> Operators = new(StringComparer.Ordinal)
    {
        ["query_block"] = "Query block",
        ["table"] = "Table",
        ["nested_loop"] = "Nested loop",
        ["ordering_operation"] = "Order",
        ["grouping_operation"] = "Group aggregate",
        ["duplicates_removal"] = "Unique",
        ["windowing"] = "Window",
        ["window_functions_computation"] = "Window",
        ["buffer_result"] = "Buffer result",
        ["union_result"] = "Union result",
        ["intersect_result"] = "Intersect result",
        ["except_result"] = "Except result",
        ["materialized_from_subquery"] = "Materialize",
        ["materialized"] = "Materialize",
        ["temporary_table"] = "Temporary table",
        ["filesort"] = "Sort",
        ["read_sorted_file"] = "Read sorted file",
        ["block-nl-join"] = "Block nested loop join",
        ["expression_cache"] = "Expression cache",
    };

    // Lists whose entries only wrap a query block: they add no operator of their own.
    private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal)
    {
        "query_specifications", "attached_subqueries", "subqueries", "select_list_subqueries", "having_subqueries",
        "order_by_subqueries", "group_by_subqueries", "optimized_away_subqueries", "update_value_subqueries",
    };

    public static bool LooksLikePlan(string? text)
    {
        if (text is null) return false;
        var t = text.AsSpan().TrimStart();
        if (t.StartsWith("->")) return true;
        return t.StartsWith("{")
               && (text.Contains("\"query_block\"", StringComparison.Ordinal) || text.Contains("\"operation\"", StringComparison.Ordinal));
    }

    public static QueryPlan Parse(string raw, PlanMode mode, string statementText)
    {
        var plan = new QueryPlan { Engine = DbKind.MySql, Mode = mode, Raw = raw };
        var statement = new PlanStatement { Text = statementText.Trim() };
        var ids = 0;
        if (raw.AsSpan().TrimStart().StartsWith("{"))
        {
            using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 512 });
            var root = doc.RootElement;
            if (root.TryGetProperty("query_block", out var block))
            {
                statement.Root = ReadBlockNode("query_block", block, ref ids);
                if (root.TryGetProperty("query_optimization", out var o) && Number(o, "r_total_time_ms") is { } planning)
                {
                    statement.PlanningTimeMs = planning;
                    statement.Properties.Add(new PlanProperty("Time", "Planning time", Ms(planning)));
                }
                if (Number(block, "r_total_time_ms") is { } execution)
                {
                    statement.ExecutionTimeMs = execution;
                    statement.Properties.Add(new PlanProperty("Time", "Execution time", Ms(execution)));
                }
            }
            else statement.Root = ReadOperation(root.TryGetProperty("query_plan", out var q) ? q : root, ref ids);
        }
        else statement.Root = ReadTree(raw, ref ids);

        if (statement.Root is null) return plan;
        statement.HasCost = statement.Nodes.Any(n => n.SubtreeCost > 0);
        statement.HasActuals = statement.Nodes.Any(n => n.ActualRows is not null);
        statement.HasTimes = statement.Nodes.Any(n => n.ActualTimeMs is not null);
        if (statement.HasTimes)
        {
            FillTimes(statement.Root);
            statement.ExecutionTimeMs ??= statement.Root.ActualTimeMs;
        }
        statement.Finish();
        foreach (var n in statement.Nodes) AddHints(n);
        plan.Statements.Add(statement);
        return plan;
    }

    // ---- query_block JSON (MySQL format 1, MariaDB) ----

    private static PlanNode ReadBlockNode(string key, JsonElement e, ref int ids)
    {
        var id = ++ids;
        var children = new List<PlanNode>();
        var properties = new List<PlanProperty>();
        if (e.ValueKind == JsonValueKind.Array)
            foreach (var item in e.EnumerateArray()) Collect(item, children, properties, ref ids);
        else Collect(e, children, properties, ref ids);

        var isObject = e.ValueKind == JsonValueKind.Object;
        var name = Operators.GetValueOrDefault(key, key);
        string? obj = null, logical = null;
        if (isObject)
        {
            if (key == "ordering_operation" && Flag(e, "using_filesort")) name = "Sort";
            var table = Str(e, "table_name");
            if (table is not null)
            {
                if (key == "table")
                {
                    var access = Str(e, "access_type");
                    name = Flag(e, "insert") ? "Insert" : Flag(e, "update") ? "Update" : Flag(e, "delete") ? "Delete" : AccessName(access);
                    logical = access;
                }
                obj = Str(e, "key") is { } index ? $"{table} ({index})" : table;
            }
        }

        var node = new PlanNode { Id = id, Name = name, Logical = logical, Object = obj };
        node.Children.AddRange(children);
        node.Properties.AddRange(properties);

        double childCost = children.Sum(c => c.SubtreeCost);
        double own = 0, subtree = 0;
        if (isObject)
        {
            var info = e.TryGetProperty("cost_info", out var ci) && ci.ValueKind == JsonValueKind.Object ? ci : default;
            if (key == "table")
            {
                // MySQL: read + eval is this table alone (prefix_cost adds the tables joined before it). MariaDB: "cost".
                own = info.ValueKind == JsonValueKind.Object
                    ? (Number(info, "read_cost") ?? 0) + (Number(info, "eval_cost") ?? 0)
                    : Number(e, "cost") ?? 0;
                subtree = own + childCost;
            }
            else
            {
                var total = (info.ValueKind == JsonValueKind.Object ? Number(info, "query_cost") : null) ?? Number(e, "cost") ?? 0;
                if (info.ValueKind == JsonValueKind.Object && Number(info, "sort_cost") is { } sort) total = Math.Max(total, childCost + sort);
                subtree = Math.Max(total, childCost);
            }
            ReadFigures(node, e);
        }
        else subtree = childCost;

        node.SubtreeCost = subtree == 0 ? childCost : subtree;
        node.OwnCost = Math.Max(0, node.SubtreeCost - childCost);
        return node;
    }

    private static void Collect(JsonElement e, List<PlanNode> children, List<PlanProperty> properties, ref int ids)
    {
        if (e.ValueKind != JsonValueKind.Object) return;
        foreach (var p in e.EnumerateObject())
        {
            var isContainer = p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
            if (isContainer && Operators.ContainsKey(p.Name)) children.Add(ReadBlockNode(p.Name, p.Value, ref ids));
            else if (isContainer && Wrappers.Contains(p.Name))
            {
                if (p.Value.ValueKind == JsonValueKind.Array)
                    foreach (var item in p.Value.EnumerateArray()) Collect(item, children, properties, ref ids);
                else Collect(p.Value, children, properties, ref ids);
            }
            else AddProperty(properties, p.Name, p.Value);
        }
    }

    private static void AddProperty(List<PlanProperty> properties, string name, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var inner in value.EnumerateObject()) AddProperty(properties, inner.Name, inner.Value);
                break;
            case JsonValueKind.Array:
                if (value.EnumerateArray().All(v => v.ValueKind is JsonValueKind.String or JsonValueKind.Number))
                    properties.Add(new PlanProperty(GroupOf(name), name, string.Join(", ", value.EnumerateArray().Select(v => v.ToString()))));
                break;
            case JsonValueKind.Null:
                break;
            // Whole numbers ("rows_examined_per_scan": 96000) read like the other figures of the plan: "96,000".
            case JsonValueKind.Number when value.TryGetInt64(out var whole):
                properties.Add(new PlanProperty(GroupOf(name), name, whole.ToString("N0", Inv)));
                break;
            default:
                properties.Add(new PlanProperty(GroupOf(name), name, value.ToString()));
                break;
        }
    }

    private static void ReadFigures(PlanNode node, JsonElement e)
    {
        var loops = Number(e, "r_loops");
        var perLoop = Number(e, "r_rows");
        if (loops is { } l)
        {
            node.Executions = l;
            node.EstimatedExecutions = Math.Max(1, l);
            if (perLoop is { } r) node.ActualRows = r * l;
        }
        // MySQL gives the rows of the whole join; MariaDB the rows of one loop.
        if (Number(e, "rows_produced_per_join") is { } produced) node.EstimatedRows = produced;
        else if (Number(e, "rows") is { } rows) node.EstimatedRows = rows * Math.Max(1, loops ?? Number(e, "loops") ?? 1);

        if (Number(e, "r_total_time_ms") is { } time) node.ActualTimeMs = time;
        else if (Number(e, "r_table_time_ms") is { } table) node.ActualTimeMs = table + (Number(e, "r_other_time_ms") ?? 0);
    }

    private static string AccessName(string? access) => access?.ToLowerInvariant() switch
    {
        "all" => "Table scan",
        "index" => "Index scan",
        "range" => "Index range seek",
        "ref" or "ref_or_null" => "Index seek",
        "eq_ref" => "Unique index seek",
        "const" or "system" => "Constant row seek",
        "index_merge" => "Index merge seek",
        "unique_subquery" or "index_subquery" => "Subquery index seek",
        "fulltext" => "Fulltext seek",
        null => "Table",
        _ => "Table access",
    };

    // ---- MySQL JSON format 2: operation / inputs ----

    private static PlanNode? ReadOperation(JsonElement e, ref int ids)
    {
        if (e.ValueKind != JsonValueKind.Object || Str(e, "operation") is not { } operation) return null;
        var id = ++ids;
        var children = new List<PlanNode>();
        if (e.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
            foreach (var input in inputs.EnumerateArray())
                if (ReadOperation(input, ref ids) is { } child) children.Add(child);

        var (name, obj) = Describe(operation);
        var node = new PlanNode { Id = id, Name = name, Object = Str(e, "table_name") ?? obj };
        node.Children.AddRange(children);
        foreach (var p in e.EnumerateObject())
            if (p.Name != "inputs") AddProperty(node.Properties, p.Name, p.Value);
        var loops = Number(e, "actual_loops");
        if (loops is { } l)
        {
            node.Executions = l;
            node.EstimatedExecutions = Math.Max(1, l);
            if (Number(e, "actual_rows") is { } rows) node.ActualRows = rows * l;
            if (Number(e, "actual_last_row_ms") is { } ms) node.ActualTimeMs = ms * l;
        }
        if (Number(e, "estimated_rows") is { } estimated) node.EstimatedRows = estimated * Math.Max(1, loops ?? 1);
        var children2 = children.Sum(c => c.SubtreeCost);
        node.SubtreeCost = Math.Max(Number(e, "estimated_total_cost") ?? 0, 0);
        if (node.SubtreeCost == 0) node.SubtreeCost = children2;
        node.OwnCost = Math.Max(0, node.SubtreeCost - children2);
        return node;
    }

    // ---- MySQL EXPLAIN ANALYZE: a text tree ----

    private sealed record TreeLine(int Indent, string Text);

    private static PlanNode? ReadTree(string text, ref int ids)
    {
        var lines = new List<TreeLine>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart(' ');
            if (trimmed.StartsWith("->", StringComparison.Ordinal))
                lines.Add(new TreeLine(line.Length - trimmed.Length, trimmed[2..].Trim()));
            else if (lines.Count > 0 && trimmed.Length > 0)
                lines[^1] = lines[^1] with { Text = lines[^1].Text + " " + trimmed.TrimEnd() };   // a condition over several lines
        }
        if (lines.Count == 0) return null;
        var at = 0;
        var roots = new List<PlanNode>();
        while (at < lines.Count) roots.Add(ReadLine(lines, ref at, ref ids));
        if (roots.Count == 1) return roots[0];

        // Several top operators (a subquery printed beside the query): one root holds them.
        var root = new PlanNode { Id = ++ids, Name = "Query" };
        root.Children.AddRange(roots);
        root.SubtreeCost = roots.Sum(r => r.SubtreeCost);
        return root;
    }

    private static PlanNode ReadLine(List<TreeLine> lines, ref int at, ref int ids)
    {
        var line = lines[at++];
        var id = ++ids;
        var children = new List<PlanNode>();
        while (at < lines.Count && lines[at].Indent > line.Indent) children.Add(ReadLine(lines, ref at, ref ids));

        var cost = CostPart().Match(line.Text);
        var actual = ActualPart().Match(line.Text);
        var never = line.Text.IndexOf("(never executed)", StringComparison.Ordinal);
        var cut = new[] { cost.Success ? cost.Index : -1, actual.Success ? actual.Index : -1, never }
            .Where(i => i >= 0).DefaultIfEmpty(line.Text.Length).Min();
        var operation = line.Text[..cut].Trim();
        var (name, obj) = Describe(operation);

        var node = new PlanNode { Id = id, Name = name, Object = obj };
        node.Children.AddRange(children);
        node.Properties.Add(new PlanProperty("Operator", "Operation", operation));
        var loops = actual.Success ? Parse(actual.Groups["loops"].Value) : never >= 0 ? 0 : (double?)null;
        if (loops is { } l)
        {
            node.Executions = l;
            node.EstimatedExecutions = Math.Max(1, l);
            node.ActualRows = actual.Success ? Parse(actual.Groups["rows"].Value) * l : 0;
        }
        if (actual.Success)
        {
            node.ActualTimeMs = Parse(actual.Groups["last"].Value) * Parse(actual.Groups["loops"].Value);
            node.Properties.Add(new PlanProperty("Rows", "Actual rows per loop", ShowplanParser.FormatNumber(Parse(actual.Groups["rows"].Value))));
            node.Properties.Add(new PlanProperty("Rows", "Loops", ShowplanParser.FormatNumber(Parse(actual.Groups["loops"].Value))));
            node.Properties.Add(new PlanProperty("Time", "Actual time per loop", $"{actual.Groups["first"].Value}..{actual.Groups["last"].Value} ms"));
        }
        var childCost = children.Sum(c => c.SubtreeCost);
        if (cost.Success)
        {
            node.SubtreeCost = Parse(cost.Groups["cost"].Value);
            if (cost.Groups["rows"].Success)
            {
                node.EstimatedRows = Parse(cost.Groups["rows"].Value) * Math.Max(1, loops ?? 1);
                node.Properties.Add(new PlanProperty("Rows", "Estimated rows per loop", ShowplanParser.FormatNumber(Parse(cost.Groups["rows"].Value))));
            }
            node.Properties.Add(new PlanProperty("Costs", "Total cost", cost.Groups["cost"].Value));
        }
        else node.SubtreeCost = childCost;
        node.OwnCost = Math.Max(0, node.SubtreeCost - childCost);
        return node;
    }

    // "Index lookup on o using ix (customer_id=c.id)" is the operator "Index seek" on "o using ix".
    private static (string Name, string? Object) Describe(string operation)
    {
        var colon = operation.IndexOf(':');
        var on = operation.IndexOf(" on ", StringComparison.Ordinal);
        string name = operation;
        string? obj = null;
        if (on > 0 && (colon < 0 || on < colon))
        {
            name = operation[..on];
            obj = operation[(on + 4)..];
            var detail = obj.IndexOf(" (", StringComparison.Ordinal);
            if (detail > 0) obj = obj[..detail];
        }
        else if (colon > 0) name = operation[..colon];
        // "lookup" means a second trip to the table on other engines: here it is an ordinary seek.
        name = name.Replace("Single-row index lookup", "Unique index seek", StringComparison.OrdinalIgnoreCase)
            .Replace("Single-row covering index lookup", "Unique covering index seek", StringComparison.OrdinalIgnoreCase)
            .Replace("index lookup", "index seek", StringComparison.OrdinalIgnoreCase);
        if (name.StartsWith("index seek", StringComparison.Ordinal)) name = "I" + name[1..];
        if (name.Length > 60) name = name[..60] + "…";
        return (name, obj);
    }

    // ---- shared ----

    private static void FillTimes(PlanNode node)
    {
        foreach (var c in node.Children) FillTimes(c);
        var below = node.Children.Sum(c => c.ActualTimeMs ?? 0);
        if (node.ActualTimeMs is null && node.Children.Any(c => c.ActualTimeMs is not null)) node.ActualTimeMs = below;
        if (node.ActualTimeMs is { } t) node.OwnTimeMs = Math.Max(0, t - below);
    }

    private static void AddHints(PlanNode n)
    {
        bool Has(string name, string value) => n.Properties.Any(p => p.Name == name && p.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
        var filtered = n.Properties.Any(p => p.Name is "attached_condition") || n.Parent?.Name == "Filter";
        if (n.Name == "Table scan" && n.Object is not null && !n.Object.StartsWith('<'))
        {
            if (filtered && Math.Max(n.ActualRows ?? 0, n.EstimatedRows ?? 0) >= 1000)
                n.Hint = "Table scan with a filter: every row is read. An index on the filter column would help.";
            else if (n.EstimatedRows > 100_000) n.Hint = "Table scan of a big table.";
        }
        if (Has("using_filesort", "true") && n.Name == "Sort")
            n.Warnings.Add(new PlanWarning("Filesort", "The rows are sorted after being read: an index in the requested order would avoid it."));
        if (Has("using_temporary_table", "true"))
            n.Warnings.Add(new PlanWarning("Temporary table", "The server builds a temporary table for this step."));
        if (n.BadEstimate && !n.Children.Any(c => c.BadEstimate))
            n.Warnings.Add(new PlanWarning("Wrong row estimate",
                $"Estimated {ShowplanParser.FormatNumber(n.EstimatedRows ?? 0)} rows, got {ShowplanParser.FormatNumber(n.ActualRows ?? 0)}. Run ANALYZE TABLE, or check the predicate."));
    }

    private static string GroupOf(string name) =>
        name.Contains("cost", StringComparison.Ordinal) ? "Costs"
        : name.Contains("condition", StringComparison.Ordinal) || name.Contains("filter", StringComparison.Ordinal) ? "Predicates"
        : name.Contains("time", StringComparison.Ordinal) || name.EndsWith("_ms", StringComparison.Ordinal) ? "Time"
        : name.Contains("rows", StringComparison.Ordinal) || name.Contains("loops", StringComparison.Ordinal) ? "Rows"
        : name is "table_name" or "key" or "possible_keys" or "used_key_parts" or "access_type" or "used_columns" or "ref" ? "Object"
        : "Details";

    private static string Ms(double ms) => $"{ms.ToString("0.###", Inv)} ms";

    private static double Parse(string number) => double.TryParse(number, NumberStyles.Float, Inv, out var v) ? v : 0;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object or JsonValueKind.Array) ? v.ToString() : null;

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // MySQL writes its costs as strings ("1.20").
    private static double? Number(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetDouble(out var d) ? d : null;
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, Inv, out var s) ? s : null;
    }

    [GeneratedRegex(@"\(cost=(?:[0-9.e+-]+\.\.)?(?<cost>[0-9.e+-]+)(?: rows=(?<rows>[0-9.e+-]+))?\)")]
    private static partial Regex CostPart();

    [GeneratedRegex(@"\(actual time=(?<first>[0-9.e+-]+)\.\.(?<last>[0-9.e+-]+) rows=(?<rows>[0-9.e+-]+) loops=(?<loops>[0-9.e+-]+)\)")]
    private static partial Regex ActualPart();
}
