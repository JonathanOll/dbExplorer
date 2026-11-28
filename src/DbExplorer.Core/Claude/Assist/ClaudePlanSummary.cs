using System.Globalization;
using System.Text;
using DbExplorer.Core.Plans;

namespace DbExplorer.Core.Claude.Assist;

/// <summary>
/// A plan as text for a question. With a redactor (schema only) it holds the operators, objects, row counts, costs and the
/// titles of the warnings: no property (predicates, parameter values, output lists), no warning detail, and the names of
/// the operators pass through the redactor because some engines write expressions in them.
/// </summary>
public static class ClaudePlanSummary
{
    private const int MaxLength = 40_000;
    private const int MaxProperty = 400;

    public static string Write(PlanStatement statement, PlanMode mode, ClaudeRedactor? redactor)
    {
        string Safe(string text) => redactor?.Redact(text) ?? text;
        var text = new StringBuilder();
        text.AppendLine(mode == PlanMode.Actual ? "Actual plan (the statement ran)." : "Estimated plan (nothing ran).");
        if (statement.HasCost) text.AppendLine(CultureInfo.InvariantCulture, $"Total cost: {statement.TotalCost:0.####}");
        if (statement.ExecutionTimeMs is { } ms) text.AppendLine(CultureInfo.InvariantCulture, $"Execution time: {ms:0.##} ms");
        foreach (var w in statement.Warnings)
            text.Append("Warning: ").Append(w.Title).AppendLine(redactor is null && w.Detail is { } d ? ". " + d : "");

        void Node(PlanNode node, int depth)
        {
            if (text.Length > MaxLength) return;
            text.Append(' ', depth * 2).Append(Safe(node.Name));
            if (node.Logical is { } logical) text.Append(" (").Append(Safe(logical)).Append(')');
            if (node.Object is { } target) text.Append(" on ").Append(Safe(target));
            var c = CultureInfo.InvariantCulture;
            if (node.EstimatedRows is { } rows) text.Append(c, $"  estimated rows={rows:0.##}");
            if (node.ActualRows is { } actual) text.Append(c, $"  actual rows={actual:0.##}");
            if (node.Executions is { } times) text.Append(c, $"  executions={times:0}");
            if (statement.HasCost) text.Append(c, $"  own cost={node.OwnCost:0.####} ({node.CostShare:P0})");
            if (node.OwnTimeMs is { } time) text.Append(c, $"  own time={time:0.##} ms");
            if (node.LogicalReads is { } reads) text.Append(c, $"  reads={reads:0}");
            foreach (var w in node.Warnings)
                text.Append("  [").Append(w.Title).Append(redactor is null && w.Detail is { } detail ? ": " + detail : "").Append(']');
            text.AppendLine();
            if (redactor is null)
                foreach (var p in node.Properties)
                {
                    if (text.Length > MaxLength) return;
                    var value = p.Value.Length > MaxProperty ? p.Value[..MaxProperty] + "…" : p.Value;
                    text.Append(' ', depth * 2 + 4).Append(p.Name).Append(": ").AppendLine(value.ReplaceLineEndings(" "));
                }
            foreach (var child in node.Children) Node(child, depth + 1);
        }

        if (statement.Root is { } root) Node(root, 0);
        foreach (var index in statement.MissingIndexes)
            text.AppendLine(CultureInfo.InvariantCulture, $"Missing index the server suggests (impact {index.ImpactPercent:0.#}%):")
                .AppendLine(index.CreateScript);
        return text.Length <= MaxLength ? text.ToString() : text.ToString(0, MaxLength) + "\n… (cut)";
    }
}
