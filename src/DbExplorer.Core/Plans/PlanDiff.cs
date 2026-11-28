using System.Globalization;

namespace DbExplorer.Core.Plans;

/// <summary>How a metric moved: better and worse follow the metric (less cost is better), changed is a neutral move (row counts).</summary>
public enum PlanVerdict { Same, Better, Worse, Changed }

/// <summary>The unit of a metric, which decides how it is written.</summary>
public enum PlanUnit { Cost, Rows, Milliseconds, Pages, Count, Percent }

/// <summary>One metric before and after: the statement-level chips and the rows of the Differences list are built from these.</summary>
public sealed record PlanMetricDelta(string Name, PlanUnit Unit, double? Before, double? After, bool LowerIsBetter = true)
{
    /// <summary>A move of 10 % or less (or below the noise floor of the unit) counts as no change.</summary>
    public const double Threshold = 0.10;

    public bool IsComparable => Before is not null && After is not null;

    public double? Change => IsComparable ? After - Before : null;

    /// <summary>Relative move, -0.75 for a drop of 75 %. Null when the baseline is 0 or a side is missing.</summary>
    public double? Percent => IsComparable && Before != 0 ? (After - Before) / Math.Abs(Before!.Value) : null;

    public PlanVerdict Verdict
    {
        get
        {
            if (Change is not { } change) return PlanVerdict.Same;
            if (Math.Abs(change) <= Floor(Unit)) return PlanVerdict.Same;
            var relative = Before == 0 ? double.PositiveInfinity : Math.Abs(change) / Math.Abs(Before!.Value);
            if (relative <= Threshold) return PlanVerdict.Same;
            if (!LowerIsBetter) return PlanVerdict.Changed;
            return change < 0 ? PlanVerdict.Better : PlanVerdict.Worse;
        }
    }

    public string BeforeText => Before is { } b ? PlanFormat.Value(b, Unit) : "-";

    public string AfterText => After is { } a ? PlanFormat.Value(a, Unit) : "-";

    /// <summary>"-75%" for a cost, "+12 ms" for a time: what the delta column says. Empty when there is nothing to compare.</summary>
    public string ChangeText
    {
        get
        {
            if (Change is not { } change) return "";
            if (Verdict == PlanVerdict.Same) return "no change";
            if (Unit == PlanUnit.Percent) return PlanFormat.Signed(change * 100, "0.#") + " pt";
            if (Unit == PlanUnit.Cost && Percent is { } p) return PlanFormat.Signed(p * 100, "0") + "%";
            if (Percent is { } pc && Unit is PlanUnit.Rows or PlanUnit.Count) return PlanFormat.Signed(pc * 100, "0") + "%";
            return PlanFormat.Signed(change, Unit);
        }
    }

    private static double Floor(PlanUnit unit) => unit switch
    {
        PlanUnit.Cost => 0.001,
        PlanUnit.Milliseconds => 0.5,
        PlanUnit.Percent => 0.005,
        _ => 0.5,
    };
}

/// <summary>A property that differs between the two sides of a matched pair (or exists on one side only).</summary>
public sealed record PlanPropertyDelta(string Group, string Name, string? Before, string? After);

/// <summary>What happened to an operator.</summary>
public enum PlanChangeKind { Matched, Added, Removed }

/// <summary>An operator of the baseline matched to one of the new plan, or an operator that exists on one side only.</summary>
public sealed class PlanNodePair
{
    public required PlanChangeKind Kind { get; init; }

    public PlanNode? Before { get; init; }

    public PlanNode? After { get; init; }

    /// <summary>Own cost, share, rows, time, reads of the operator before and after (a metric absent on both sides is not listed).</summary>
    public IReadOnlyList<PlanMetricDelta> Metrics { get; init; } = [];

    public IReadOnlyList<PlanPropertyDelta> Properties { get; init; } = [];

    /// <summary>The metric the verdict and the badge come from: own cost, else own time, else logical reads.</summary>
    public PlanMetricDelta? Primary { get; init; }

    public PlanVerdict Verdict => Kind == PlanChangeKind.Matched ? Primary?.Verdict ?? PlanVerdict.Same : PlanVerdict.Changed;

    /// <summary>The operator changed kind: a scan became a seek.</summary>
    public bool Replaced => Kind == PlanChangeKind.Matched && Before!.Name != After!.Name;

    public PlanNode Any => (After ?? Before)!;

    /// <summary>"Clustered Index Scan on Orders".</summary>
    public static string Describe(PlanNode n) => n.Object is { } o ? $"{n.Name} on {PlanFormat.TableName(o)}" : n.Name;
}

/// <summary>The comparison of two statements' plans: statement-level deltas, matched operators with their deltas, and the operators that appeared or went.</summary>
public sealed class PlanDiff
{
    public required PlanStatement Baseline { get; init; }

    public required PlanStatement Candidate { get; init; }

    /// <summary>Total cost, rows, times, reads, operator count: those present on at least one side.</summary>
    public required IReadOnlyList<PlanMetricDelta> Statement { get; init; }

    /// <summary>Every operator of both plans, once: matched pairs, then added and removed ones.</summary>
    public required IReadOnlyList<PlanNodePair> Pairs { get; init; }

    public IEnumerable<PlanNodePair> Matched => Pairs.Where(p => p.Kind == PlanChangeKind.Matched);

    public IEnumerable<PlanNodePair> Added => Pairs.Where(p => p.Kind == PlanChangeKind.Added);

    public IEnumerable<PlanNodePair> Removed => Pairs.Where(p => p.Kind == PlanChangeKind.Removed);

    /// <summary>The pair an operator of either plan belongs to.</summary>
    public PlanNodePair? Find(PlanNode node) => Pairs.FirstOrDefault(p => p.Before == node || p.After == node);

    /// <summary>The headline: total cost (else execution time) moved this way.</summary>
    public PlanVerdict Overall => new[] { "Total cost", "Execution time" }
        .Select(name => Statement.FirstOrDefault(m => m.Name == name && m.IsComparable)?.Verdict ?? PlanVerdict.Same)
        .FirstOrDefault(v => v != PlanVerdict.Same);

    /// <summary>The plans are the same shape and the same numbers: nothing to see.</summary>
    public bool IsIdentical => Pairs.All(p => p.Kind == PlanChangeKind.Matched && !p.Replaced && p.Verdict == PlanVerdict.Same)
        && Statement.All(m => m.Verdict == PlanVerdict.Same);

    /// <summary>The comparison in plain words: "Cost 12.4 → 3.1 (-75%); Clustered Index Scan on Orders replaced by Index Seek on Orders".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            var cost = Statement.FirstOrDefault(m => m.Name == "Total cost" && m.IsComparable);
            var time = Statement.FirstOrDefault(m => m.Name == "Execution time" && m.IsComparable);
            if (cost is not null) parts.Add(Headline("Cost", cost));
            if (time is not null && time.Verdict != PlanVerdict.Same) parts.Add(Headline("Time", time));
            if (cost is null && time is null)
            {
                var count = Statement.First(m => m.Name == "Operators");
                parts.Add($"{PlanFormat.Number(count.Before ?? 0, "0")} → {PlanFormat.Number(count.After ?? 0, "0")} operators");
            }

            var changes = new List<string>();
            foreach (var p in Pairs.Where(p => p.Replaced))
                changes.Add($"{PlanNodePair.Describe(p.Before!)} replaced by {PlanNodePair.Describe(p.After!)}");
            foreach (var p in Added) changes.Add($"{PlanNodePair.Describe(p.After!)} added");
            foreach (var p in Removed) changes.Add($"{PlanNodePair.Describe(p.Before!)} removed");
            if (changes.Count == 0 && IsIdentical) changes.Add("same plan");
            parts.AddRange(changes.Take(3));
            if (changes.Count > 3) parts.Add($"{changes.Count - 3} more change{(changes.Count - 3 == 1 ? "" : "s")}");
            return string.Join("; ", parts);
        }
    }

    private static string Headline(string label, PlanMetricDelta m)
    {
        var text = $"{label} {m.BeforeText} → {m.AfterText}";
        return m.Verdict == PlanVerdict.Same || m.Percent is not { } p ? text + (m.Verdict == PlanVerdict.Same ? " (no change)" : "") : $"{text} ({PlanFormat.Signed(p * 100, "0")}%)";
    }
}

/// <summary>Number and delta text shared by the summary and the view models.</summary>
public static class PlanFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Value(double v, PlanUnit unit) => unit switch
    {
        PlanUnit.Cost => Cost(v),
        PlanUnit.Milliseconds => Ms(v),
        PlanUnit.Percent => (v * 100).ToString(v < 0.01 && v > 0 ? "0.#" : "0", Inv) + "%",
        _ => Count(v),
    };

    public static string Cost(double v) =>
        v == 0 ? "0" : v >= 100 ? v.ToString("0", Inv) : v >= 10 ? v.ToString("0.#", Inv) : v >= 1 ? v.ToString("0.##", Inv) : v >= 0.001 ? v.ToString("0.####", Inv) : v.ToString("0.###E+0", Inv);

    // Every figure of the plan views is invariant (the UI is English): "1,200 rows", "5.12", "71.4%", never "71,4%" next to "5.12".
    public static string Ms(double ms) =>
        ms >= 10_000 ? (ms / 1000).ToString("0.#", Inv) + " s" : ms >= 100 ? ms.ToString("0", Inv) + " ms" : ms >= 1 ? ms.ToString("0.#", Inv) + " ms" : ms.ToString("0.###", Inv) + " ms";

    /// <summary>"71.4%" from 71.4 (a percentage, not a share).</summary>
    public static string Percent(double percent, string format = "0.#") => percent.ToString(format, Inv) + "%";

    /// <summary>"1,200" or "12.5M": a row or execution count.</summary>
    public static string Count(double v) => v >= 1e7 ? (v / 1e6).ToString("0.#", Inv) + "M" : v.ToString("N0", Inv);

    /// <summary>Short row count for the arrows: "840", "84K", "1.2M".</summary>
    public static string Compact(double v) =>
        v >= 1e6 ? (v / 1e6).ToString("0.#", Inv) + "M" : v >= 1e5 ? (v / 1e3).ToString("0", Inv) + "K" : v >= 1e4 ? (v / 1e3).ToString("0.#", Inv) + "K" : v.ToString("N0", Inv);

    /// <summary>A plain number with up to <paramref name="format"/> decimals, invariant ("5.12").</summary>
    public static string Number(double v, string format = "0.####") => v.ToString(format, Inv);

    /// <summary>"+12" or "-64" with a real minus; <paramref name="format"/> is a .NET number format.</summary>
    public static string Signed(double v, string format) => (v > 0 ? "+" : v < 0 ? "-" : "") + Math.Abs(v).ToString(format, Inv);

    public static string Signed(double v, PlanUnit unit) => (v > 0 ? "+" : v < 0 ? "-" : "") + Value(Math.Abs(v), unit);

    /// <summary>Object label without brackets, quotes or the schema noise: "[dbo].[Orders]" gives "Orders".</summary>
    /// <summary>The table of an operator's object label: "[Orders].[PK_Orders] (o)" gives "Orders", "public.orders (idx) as o" gives "orders".</summary>
    public static string TableName(string label)
    {
        var s = ShortObject(label);
        var alias = s.IndexOf(" as ", StringComparison.OrdinalIgnoreCase);
        if (alias >= 0) s = s[..alias];
        var paren = s.IndexOf(" (", StringComparison.Ordinal);
        if (paren >= 0) s = s[..paren];
        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.FirstOrDefault(p => !p.Equals("dbo", StringComparison.OrdinalIgnoreCase) && !p.Equals("public", StringComparison.OrdinalIgnoreCase)) ?? s;
    }

    public static string ShortObject(string label) => label.Replace("[", "").Replace("]", "").Replace("\"", "");
}
