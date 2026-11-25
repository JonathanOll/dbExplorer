namespace DbExplorer.Core.Plans;

/// <summary>
/// Compares two plans of the same query (before and after an index or a rewrite). Operators are matched by structure: both trees
/// are walked in parallel, the children of a matched pair are aligned in order by how alike they are (same operator on the same
/// table first), and what finds no partner is added or removed. Works on plans of any engine; a metric absent on both sides is skipped.
/// </summary>
public static class PlanComparer
{
    private static readonly HashSet<string> SchemaNoise = ["dbo", "public", "main", "sys", "temp"];

    public static PlanDiff Compare(PlanStatement baseline, PlanStatement candidate)
    {
        var pairs = new List<PlanNodePair>();
        var matches = new List<(PlanNode A, PlanNode B)>();
        var removed = new List<PlanNode>();
        var added = new List<PlanNode>();

        if (baseline.Root is { } a && candidate.Root is { } b) AlignChildren([a], [b], matches, removed, added);
        else
        {
            if (baseline.Root is { } onlyA) removed.AddRange(onlyA.Descendants());
            if (candidate.Root is { } onlyB) added.AddRange(onlyB.Descendants());
        }

        foreach (var (x, y) in matches.OrderBy(m => m.A.Id)) pairs.Add(PairOf(baseline, candidate, x, y));
        foreach (var n in removed.OrderBy(n => n.Id))
            pairs.Add(new PlanNodePair { Kind = PlanChangeKind.Removed, Before = n, Metrics = Metrics(baseline, candidate, n, null).ToList() });
        foreach (var n in added.OrderBy(n => n.Id))
            pairs.Add(new PlanNodePair { Kind = PlanChangeKind.Added, After = n, Metrics = Metrics(baseline, candidate, null, n).ToList() });

        return new PlanDiff { Baseline = baseline, Candidate = candidate, Statement = StatementDeltas(baseline, candidate), Pairs = pairs };
    }

    // ---- structure ----

    private static void Match(PlanNode a, PlanNode b, List<(PlanNode, PlanNode)> matches, List<PlanNode> removed, List<PlanNode> added)
    {
        matches.Add((a, b));
        AlignChildren(a.Children, b.Children, matches, removed, added);
    }

    private static void AlignChildren(IReadOnlyList<PlanNode> left, IReadOnlyList<PlanNode> right,
        List<(PlanNode, PlanNode)> matches, List<PlanNode> removed, List<PlanNode> added)
    {
        var pairedLeft = new PlanNode?[right.Count];   // by right index
        var pairedRight = new PlanNode?[left.Count];
        var wrapped = new bool[left.Count];

        // 1. order-preserving alignment that maximises the total likeness (a longest common subsequence with scores)
        var n = left.Count;
        var m = right.Count;
        var best = new double[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
            {
                var s = Score(left[i], right[j]);
                best[i, j] = Math.Max(Math.Max(best[i + 1, j], best[i, j + 1]), s > 0 ? s + best[i + 1, j + 1] : 0);
            }
        for (int i = 0, j = 0; i < n && j < m;)
        {
            var s = Score(left[i], right[j]);
            if (s > 0 && Math.Abs(best[i, j] - (s + best[i + 1, j + 1])) < 1e-9) { pairedLeft[j] = left[i]; pairedRight[i] = right[j]; i++; j++; }
            else if (best[i + 1, j] >= best[i, j + 1]) i++;
            else j++;
        }

        // 1b. like-for-like that crossed over (the two inputs of a join swapped sides)
        var crossed = (from i in Enumerable.Range(0, n) where pairedRight[i] is null
                       from j in Enumerable.Range(0, m) where pairedLeft[j] is null
                       let score = Score(left[i], right[j]) where score > 0
                       orderby score descending, i, j
                       select (i, j)).ToList();
        foreach (var (i, j) in crossed)
            if (pairedRight[i] is null && pairedLeft[j] is null) { pairedRight[i] = right[j]; pairedLeft[j] = left[i]; }

        // 2. what is left: a wrapper operator added or removed on top of a match (a Sort inserted over a scan), then any like-for-like
        for (var i = 0; i < n; i++)
        {
            if (pairedRight[i] is not null) continue;
            for (var j = 0; j < m; j++)
            {
                if (pairedLeft[j] is not null) continue;
                if (FindThroughWrapper(left[i], right[j]) is { } hit)
                {
                    Wrap(left[i], right[j], hit, matches, removed, added);
                    pairedRight[i] = right[j];
                    pairedLeft[j] = left[i];
                    wrapped[i] = true;
                    break;
                }
            }
        }

        for (var i = 0; i < n; i++)
        {
            if (wrapped[i]) continue;   // Wrap matched it already
            if (pairedRight[i] is { } r) Match(left[i], r, matches, removed, added);
            else removed.AddRange(left[i].Descendants());
        }
        for (var j = 0; j < m; j++)
            if (pairedLeft[j] is null) added.AddRange(right[j].Descendants());
    }

    private enum Side { None, Left, Right }

    /// <summary>When one side has an operator the other lacks right above a good match (single-child chains only).</summary>
    private static (Side Side, PlanNode Inner)? FindThroughWrapper(PlanNode a, PlanNode b)
    {
        var bestScore = 0.0;
        (Side, PlanNode)? hit = null;
        for (var x = SingleChild(b); x is not null; x = SingleChild(x))
        {
            var s = Score(a, x);
            if (s >= 40 && s > bestScore) { bestScore = s; hit = (Side.Right, x); }
        }
        for (var x = SingleChild(a); x is not null; x = SingleChild(x))
        {
            var s = Score(x, b);
            if (s >= 40 && s > bestScore) { bestScore = s; hit = (Side.Left, x); }
        }
        return hit;
    }

    private static PlanNode? SingleChild(PlanNode n) => n.Children.Count == 1 ? n.Children[0] : null;

    private static void Wrap(PlanNode a, PlanNode b, (Side Side, PlanNode Inner) hit,
        List<(PlanNode, PlanNode)> matches, List<PlanNode> removed, List<PlanNode> added)
    {
        if (hit.Side == Side.Right)
        {
            // b ... inner: the operators above inner are new
            for (var x = b; x != hit.Inner; x = x.Children[0]) added.Add(x);
            Match(a, hit.Inner, matches, removed, added);
        }
        else
        {
            for (var x = a; x != hit.Inner; x = x.Children[0]) removed.Add(x);
            Match(hit.Inner, b, matches, removed, added);
        }
    }

    // ---- likeness ----

    /// <summary>0 for operators that should not be paired; higher is more alike.</summary>
    internal static double Score(PlanNode a, PlanNode b)
    {
        var sameName = string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        var sameLogical = string.Equals(a.Logical ?? a.Name, b.Logical ?? b.Name, StringComparison.OrdinalIgnoreCase);
        if (a.Object is null || b.Object is null)
        {
            if (a.Object is null && b.Object is null)
                return sameName ? (sameLogical ? 60 : 50) : sameLogical ? 20 : 0;
            // an operator with a table never pairs with one without (a Sort is not a scan)
            return 0;
        }

        var exact = string.Equals(Norm(a.Object), Norm(b.Object), StringComparison.OrdinalIgnoreCase);
        if (exact) return sameName ? 100 : 55;
        if (SharesTable(a.Object, b.Object)) return sameName ? 70 : 45;   // other index of the same table, or a scan turned into a seek
        return 0;   // two different tables are two different operators
    }

    private static string Norm(string obj)
    {
        var s = obj;
        var alias = s.IndexOf(" as ", StringComparison.OrdinalIgnoreCase);
        if (alias >= 0) s = s[..alias];
        return ShortName(s);
    }

    private static string ShortName(string s) => PlanFormat.ShortObject(s).Trim().ToLowerInvariant();

    private static HashSet<string> Tokens(string obj)
    {
        var s = obj;
        var alias = s.IndexOf(" as ", StringComparison.OrdinalIgnoreCase);
        if (alias >= 0) s = s[..alias];
        var paren = s.IndexOf(" (", StringComparison.Ordinal);
        var table = paren >= 0 ? s[..paren] : s;
        return ShortName(table).Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !SchemaNoise.Contains(t)).ToHashSet();
    }

    /// <summary>The same table: SQL Server labels read "Table.Index", PostgreSQL "schema.table (index)"; the first non-schema token is the table.</summary>
    private static bool SharesTable(string a, string b)
    {
        var ta = Tokens(a).FirstOrDefault();
        var tb = Tokens(b).FirstOrDefault();
        return ta is not null && ta == tb;
    }

    // ---- numbers ----

    private static PlanNodePair PairOf(PlanStatement baseline, PlanStatement candidate, PlanNode a, PlanNode b)
    {
        var metrics = Metrics(baseline, candidate, a, b).ToList();
        PlanMetricDelta? primary = null;
        if (baseline.HasCost && candidate.HasCost) primary = metrics.FirstOrDefault(m => m.Name == "Own cost");
        primary ??= metrics.FirstOrDefault(m => m.Name == "Own time" && m.IsComparable);
        primary ??= metrics.FirstOrDefault(m => m.Name == "Logical reads" && m.IsComparable);
        return new PlanNodePair { Kind = PlanChangeKind.Matched, Before = a, After = b, Metrics = metrics, Primary = primary, Properties = PropertyDeltas(a, b) };
    }

    private static IEnumerable<PlanMetricDelta> Metrics(PlanStatement baseline, PlanStatement candidate, PlanNode? a, PlanNode? b)
    {
        var withCost = baseline.HasCost && candidate.HasCost;
        if (withCost)
        {
            yield return new PlanMetricDelta("Own cost", PlanUnit.Cost, a?.OwnCost, b?.OwnCost);
            yield return new PlanMetricDelta("Share of the cost", PlanUnit.Percent, a?.CostShare, b?.CostShare, LowerIsBetter: false);
        }
        if (Present(a?.EstimatedRows, b?.EstimatedRows)) yield return new PlanMetricDelta("Estimated rows", PlanUnit.Rows, a?.EstimatedRows, b?.EstimatedRows, false);
        if (Present(a?.ActualRows, b?.ActualRows)) yield return new PlanMetricDelta("Actual rows", PlanUnit.Rows, a?.ActualRows, b?.ActualRows, false);
        if (Present(a?.OwnTimeMs, b?.OwnTimeMs)) yield return new PlanMetricDelta("Own time", PlanUnit.Milliseconds, a?.OwnTimeMs, b?.OwnTimeMs);
        if (Present(a?.LogicalReads, b?.LogicalReads)) yield return new PlanMetricDelta("Logical reads", PlanUnit.Pages, a?.LogicalReads, b?.LogicalReads);
    }

    private static bool Present(double? x, double? y) => x is not null || y is not null;

    private static IReadOnlyList<PlanPropertyDelta> PropertyDeltas(PlanNode a, PlanNode b)
    {
        // Costs, rows and times are in the metric rows already; the rest is listed when it differs.
        static bool Skip(PlanProperty p) => p.Group is "Costs" or "Rows" or "Time" or "I/O";
        var before = a.Properties.Where(p => !Skip(p)).GroupBy(p => (p.Group, p.Name)).ToDictionary(g => g.Key, g => g.First().Value);
        var after = b.Properties.Where(p => !Skip(p)).GroupBy(p => (p.Group, p.Name)).ToDictionary(g => g.Key, g => g.First().Value);
        var result = new List<PlanPropertyDelta>();
        foreach (var key in before.Keys.Concat(after.Keys).Distinct())
        {
            before.TryGetValue(key, out var x);
            after.TryGetValue(key, out var y);
            if (x != y) result.Add(new PlanPropertyDelta(key.Group, key.Name, x, y));
        }
        return result;
    }

    private static IReadOnlyList<PlanMetricDelta> StatementDeltas(PlanStatement a, PlanStatement b)
    {
        var list = new List<PlanMetricDelta>();
        void Add(string name, PlanUnit unit, double? x, double? y, bool lowerIsBetter = true)
        {
            if (x is null && y is null) return;
            list.Add(new PlanMetricDelta(name, unit, x, y, lowerIsBetter));
        }

        if (a.HasCost && b.HasCost) Add("Total cost", PlanUnit.Cost, a.TotalCost, b.TotalCost);
        Add("Estimated rows", PlanUnit.Rows, a.Root?.EstimatedRows, b.Root?.EstimatedRows, false);
        Add("Actual rows", PlanUnit.Rows, a.Root?.ActualRows, b.Root?.ActualRows, false);
        Add("Execution time", PlanUnit.Milliseconds, a.ExecutionTimeMs, b.ExecutionTimeMs);
        Add("Planning time", PlanUnit.Milliseconds, a.PlanningTimeMs, b.PlanningTimeMs);
        Add("Logical reads", PlanUnit.Pages, Reads(a), Reads(b));
        Add("Operators", PlanUnit.Count, a.Nodes.Count(), b.Nodes.Count(), false);
        return list;
    }

    private static double? Reads(PlanStatement s) => s.Nodes.Any(n => n.LogicalReads is not null) ? s.Nodes.Sum(n => n.LogicalReads ?? 0) : null;
}
