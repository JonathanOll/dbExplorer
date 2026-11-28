using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbExplorer.Core.Plans;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Claude;

/// <summary>What the tools write back: JSON for rows and metadata, indented text for plans, all of it bounded.</summary>
internal static class ClaudeText
{
    /// <summary>Under the size past which Claude Code puts a result in a file.</summary>
    public const int MaxOutput = 40_000;
    public const int MaxCell = 2_000;

    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Json(JsonNode node) => node.ToJsonString(Options);

    /// <summary>The rows that fit in <see cref="MaxOutput"/>; "truncated" says when some are left out.</summary>
    public static string Rows(ResultSet? set)
    {
        var columns = new JsonArray();
        var rows = new JsonArray();
        var shortened = false;
        var truncated = set?.Truncated ?? false;
        var size = 0;
        foreach (var name in set?.Columns ?? [])
        {
            columns.Add((JsonNode?)Cell(name, ref shortened));
            size += Math.Min(name.Length, MaxCell) + 3;
        }
        if (size > MaxOutput / 2) throw new ClaudeRefusedException("The result has too many columns. Select fewer.");

        foreach (var values in set?.Rows ?? [])
        {
            var row = new JsonArray();
            foreach (var value in values) row.Add(Cell(value, ref shortened));
            var length = Json(row).Length + 1;
            if (size + length > MaxOutput) { truncated = true; break; }
            size += length;
            rows.Add((JsonNode)row);
        }

        var result = new JsonObject
        {
            ["columns"] = columns,
            ["rows"] = rows,
            ["row_count"] = rows.Count,
            ["truncated"] = truncated,
        };
        if (shortened) result["note"] = $"Some values were cut at {MaxCell} characters.";
        return Json(result);
    }

    private static JsonNode? Cell(object? value, ref bool shortened)
    {
        switch (value)
        {
            case null: return null;
            case bool b: return JsonValue.Create(b);
            case byte or sbyte or short or ushort or int or uint or long:
                return JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case double d when double.IsFinite(d): return JsonValue.Create(d);
            case float f when float.IsFinite(f): return JsonValue.Create(f);
            case byte[] bytes: return JsonValue.Create($"<binary, {bytes.Length} bytes>");
        }
        var text = value switch
        {
            DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            DateTimeOffset d => d.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
            Array a => string.Join(", ", a.Cast<object?>().Take(200).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture))),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        if (text.Length > MaxCell)
        {
            shortened = true;
            text = text[..MaxCell] + "…";
        }
        return JsonValue.Create(text);
    }

    public static string Plan(QueryPlan plan)
    {
        var text = new StringBuilder();
        text.AppendLine($"Estimated plan ({plan.Engine}). Nothing was executed.");
        foreach (var statement in plan.Statements)
        {
            if (statement.HasCost) text.AppendLine(FormattableString.Invariant($"Total cost: {statement.TotalCost:0.####}"));
            foreach (var w in statement.Warnings) text.AppendLine($"Warning: {w.Title} {w.Detail}");
            if (statement.Root is { } root) Node(text, root, statement.HasCost, 0);
            foreach (var index in statement.MissingIndexes)
                text.AppendLine(FormattableString.Invariant($"Missing index (impact {index.ImpactPercent:0.#}%):")).AppendLine(index.CreateScript);
        }
        return Cut(text.ToString());
    }

    private static void Node(StringBuilder text, PlanNode node, bool costs, int depth)
    {
        if (text.Length > MaxOutput) return;
        text.Append(' ', depth * 2).Append(node.Name);
        if (node.Logical is { } logical) text.Append(" (").Append(logical).Append(')');
        if (node.Object is { } target) text.Append(" on ").Append(target);
        if (node.EstimatedRows is { } rows) text.Append(FormattableString.Invariant($"  rows={rows:0.##}"));
        if (costs) text.Append(FormattableString.Invariant($"  cost={node.SubtreeCost:0.####}"));
        if (node.Hint is { } hint) text.Append("  [").Append(hint).Append(']');
        foreach (var w in node.Warnings) text.Append("  [").Append(w.Title).Append(']');
        text.AppendLine();
        foreach (var child in node.Children) Node(text, child, costs, depth + 1);
    }

    public static string Cut(string text) => text.Length <= MaxOutput ? text : text[..MaxOutput] + "\n… (cut)";
}
