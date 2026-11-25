using System.Text.RegularExpressions;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Plans;

/// <summary>One row of <c>EXPLAIN QUERY PLAN</c>.</summary>
public sealed record SqliteExplainRow(long Id, long Parent, string Detail);

/// <summary>
/// Turns the rows of <c>EXPLAIN QUERY PLAN</c> into a tree. SQLite reports no cost and no row count, so the plan has none
/// (<see cref="PlanStatement.HasCost"/> is false); what it does say, scan or search and which index, is shown with hints.
/// </summary>
public static partial class SqlitePlanParser
{
    [GeneratedRegex(@"^(?<verb>SCAN|SEARCH)\s+(?:TABLE\s+)?(?<table>\S+)(?:\s+AS\s+(?<alias>\S+))?(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScanOrSearch();

    public static QueryPlan Parse(IReadOnlyList<SqliteExplainRow> rows, string statementText)
    {
        var raw = string.Join("\n", rows.Select(r => $"{r.Id}|{r.Parent}|{r.Detail}"));
        var plan = new QueryPlan { Engine = DbKind.Sqlite, Mode = PlanMode.Estimated, Raw = raw };

        var root = new PlanNode { Id = 0, Name = "Query" };
        var byId = new Dictionary<long, PlanNode> { [0] = root };
        foreach (var row in rows)
        {
            var node = ReadRow(row);
            byId[row.Id] = node;
            (byId.TryGetValue(row.Parent, out var parent) ? parent : root).Children.Add(node);
        }

        var statement = new PlanStatement { Text = statementText.Trim(), Root = rows.Count == 0 ? null : root, HasCost = false };
        statement.Finish();
        plan.Statements.Add(statement);
        return plan;
    }

    private static PlanNode ReadRow(SqliteExplainRow row)
    {
        var detail = row.Detail.Trim();
        var m = ScanOrSearch().Match(detail);
        PlanNode node;
        if (m.Success)
        {
            var verb = m.Groups["verb"].Value.ToUpperInvariant();
            var table = m.Groups["table"].Value;
            var alias = m.Groups["alias"].Success ? m.Groups["alias"].Value : null;
            var rest = m.Groups["rest"].Value.Trim();
            var usesIndex = rest.StartsWith("USING", StringComparison.OrdinalIgnoreCase);
            var covering = rest.Contains("COVERING INDEX", StringComparison.OrdinalIgnoreCase);
            var primary = rest.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase);
            var constant = table.Equals("CONSTANT", StringComparison.OrdinalIgnoreCase);

            var name = verb == "SEARCH"
                ? (primary ? "Primary key seek" : "Index seek")
                : (covering ? "Covering index scan" : usesIndex ? "Index scan" : constant ? "Constant row" : "Table scan");
            node = new PlanNode { Id = (int)row.Id, Name = name, Logical = verb, Object = alias is null ? table : $"{table} as {alias}" };
            if (!usesIndex && !constant && verb == "SCAN")
                node.Hint = $"Full scan: every row of {table} is read. An index on the filtered or joined column would let SQLite search instead.";
            if (usesIndex) node.Properties.Add(new PlanProperty("Details", "Using", rest[5..].Trim()));
            node.Properties.Add(new PlanProperty("Object", "Table", table));
            if (alias is not null) node.Properties.Add(new PlanProperty("Object", "Alias", alias));
        }
        else
        {
            node = new PlanNode { Id = (int)row.Id, Name = Title(detail) };
            if (detail.StartsWith("USE TEMP B-TREE", StringComparison.OrdinalIgnoreCase))
                node.Hint = "SQLite builds a temporary b-tree to sort or group: an index in the right order would avoid it.";
        }
        node.Properties.Insert(0, new PlanProperty("Operator", "Plan line", detail));
        return node;
    }

    private static string Title(string detail) =>
        detail.StartsWith("USE TEMP B-TREE FOR ", StringComparison.OrdinalIgnoreCase)
            ? "Temp b-tree (" + detail[20..].ToLowerInvariant() + ")"
            : detail.Length == 0 ? "Step" : char.ToUpperInvariant(detail[0]) + detail[1..].ToLowerInvariant();
}
