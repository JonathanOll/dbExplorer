namespace DbExplorer.Core.Metadata;

/// <summary>
/// Case-insensitive subsequence matcher for type-to-filter pickers. Runs on every keystroke over thousands
/// of candidates, so it rejects non-matches with a single linear scan and scores matches with a DP over
/// two small rolling rows (stack-allocated for normal identifier lengths).
/// </summary>
public static class FuzzyMatcher
{
    private const int MatchScore = 16;
    private const int ConsecutiveBonus = 12;
    private const int StartBonus = 20;        // match on the first character of the candidate
    private const int BoundaryBonus = 14;     // match after a separator or on a camelCase hump
    private const int GapPenalty = 2;         // per skipped character between/before matches
    private const int LengthPenalty = 1;      // per candidate character, prefers shorter candidates
    private const int NoMatch = int.MinValue / 2;
    private const int StackLimit = 256;

    /// <summary>Returns a score (higher is better), or null when <paramref name="query"/> is not a subsequence of <paramref name="candidate"/>.</summary>
    public static int? Score(string query, string candidate)
    {
        if (query.Length == 0) return 0;
        var n = candidate.Length;
        var m = query.Length;
        if (m > n) return null;

        // Cheap reject: greedy subsequence check.
        var qi = 0;
        for (var i = 0; i < n && qi < m; i++)
            if (Eq(candidate[i], query[qi])) qi++;
        if (qi < m) return null;

        Span<int> prev = n <= StackLimit ? stackalloc int[n] : new int[n];
        Span<int> cur = n <= StackLimit ? stackalloc int[n] : new int[n];

        // Row 0: best score with query[0] matched at candidate[j].
        for (var j = 0; j < n; j++)
            prev[j] = Eq(candidate[j], query[0]) ? MatchScore + PositionBonus(candidate, j) - GapPenalty * j : NoMatch;

        for (var i = 1; i < m; i++)
        {
            var q = query[i];
            // gapBest = max over k < j-1 of prev[k] - GapPenalty * (j-1-k): best predecessor with at least one skipped char.
            var gapBest = NoMatch;
            for (var j = 0; j < n; j++)
            {
                var best = NoMatch;
                if (Eq(candidate[j], q))
                {
                    if (j > 0 && prev[j - 1] > NoMatch) best = prev[j - 1] + ConsecutiveBonus;
                    if (gapBest > best) best = gapBest;
                    if (best > NoMatch) best += MatchScore + PositionBonus(candidate, j);
                }
                cur[j] = best;

                // Advance gapBest so it is valid for j + 1.
                var candidateBest = j > 0 ? Math.Max(gapBest, prev[j - 1]) : gapBest;
                gapBest = candidateBest > NoMatch ? candidateBest - GapPenalty : NoMatch;
            }

            var tmp = prev;
            prev = cur;
            cur = tmp;
        }

        var result = NoMatch;
        for (var j = 0; j < n; j++)
            if (prev[j] > result) result = prev[j];

        return result > NoMatch ? result - LengthPenalty * n : null;
    }

    /// <summary>Best <paramref name="take"/> matches ordered by score (descending) then text; an empty query yields the first items alphabetically.</summary>
    public static IReadOnlyList<T> Rank<T>(string query, IEnumerable<T> items, Func<T, string> text, int take)
    {
        if (take <= 0) return [];

        var scored = new List<(int Score, string Text, T Item)>();
        foreach (var item in items)
        {
            var t = text(item);
            var s = Score(query, t);
            if (s is { } score) scored.Add((score, t, item));
        }

        scored.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : StringComparer.OrdinalIgnoreCase.Compare(a.Text, b.Text);
        });

        var count = Math.Min(take, scored.Count);
        var result = new T[count];
        for (var i = 0; i < count; i++) result[i] = scored[i].Item;
        return result;
    }

    private static bool Eq(char a, char b) => a == b || char.ToLowerInvariant(a) == char.ToLowerInvariant(b);

    private static int PositionBonus(string s, int j)
    {
        if (j == 0) return StartBonus;
        var prev = s[j - 1];
        var c = s[j];
        if (!char.IsLetterOrDigit(prev)) return BoundaryBonus;
        if (char.IsLower(prev) && char.IsUpper(c)) return BoundaryBonus;
        return 0;
    }
}
