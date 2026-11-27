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
            var lower = char.ToLowerInvariant(q);   // once per row: this is the hot loop of every keystroke
            // gapBest = max over k < j-1 of prev[k] - GapPenalty * (j-1-k): best predecessor with at least one skipped char.
            var gapBest = NoMatch;
            for (var j = 0; j < n; j++)
            {
                var best = NoMatch;
                var c = candidate[j];
                if (c == q || char.ToLowerInvariant(c) == lower)
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

    /// <summary>
    /// A cheaper <see cref="Score"/> on the same scale, for callers that score several words per candidate: the same
    /// matches, but one alignment only (the word as written when the candidate contains it, else the first occurrence of each
    /// letter) instead of the best of all.
    /// </summary>
    public static int? ScoreFast(string query, string candidate)
    {
        var m = query.Length;
        if (m == 0) return 0;
        var n = candidate.Length;
        if (m > n) return null;

        var at = candidate.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            var whole = MatchScore * m + ConsecutiveBonus * (m - 1) - GapPenalty * at - LengthPenalty * n;
            for (var k = at; k < at + m; k++) whole += PositionBonus(candidate, k);
            return whole;
        }

        // The first occurrence of each letter, on the framework's vectorised search.
        var score = -LengthPenalty * n;
        var last = -1;
        for (var qi = 0; qi < m; qi++)
        {
            var c = query[qi];
            var skip = candidate.AsSpan(last + 1).IndexOfAny(char.ToLowerInvariant(c), char.ToUpperInvariant(c));
            if (skip < 0) return null;
            var i = last + 1 + skip;
            score += MatchScore + PositionBonus(candidate, i);
            if (qi > 0 && skip == 0) score += ConsecutiveBonus;
            else score -= GapPenalty * skip;
            last = i;
        }

        return score;
    }

    // Typo tolerance: below every strict score (those stay above about -1000), then closer matches first.
    private const int TolerantBase = -100_000;
    private const int TolerantErrorPenalty = 1_000;
    private const int MaxTolerantLength = 12;   // longer queries stay strict: typo search costs a DP per candidate

    /// <summary>
    /// <see cref="Score"/>, and when the query is not a subsequence of the candidate, a typo-tolerant score: the query still
    /// matches when some part of the candidate is within a few edits of it (a wrong, extra or missing letter, or two letters
    /// swapped; 1 edit for 4-6 letters, 2 from 7). Tolerant matches always rank below strict ones. Queries of up to 3 letters
    /// stay strict (otherwise almost everything would match), and so do queries over 12 letters (cost).
    /// </summary>
    public static int? ScoreTolerant(string query, string candidate)
    {
        return Score(query, candidate) ?? Typo(query, candidate);
    }

    /// <summary>The typo-tolerant half of <see cref="ScoreTolerant"/>, for a caller that already knows the strict score is null.</summary>
    public static int? Typo(string query, string candidate)
    {
        var m = query.Length;
        var allowed = m <= 3 || m > MaxTolerantLength ? 0 : m <= 6 ? 1 : 2;
        if (allowed == 0) return null;
        var errors = EditsToContain(query, candidate, allowed);
        return errors is { } e ? TolerantBase - TolerantErrorPenalty * e - candidate.Length : null;
    }

    /// <summary>
    /// Smallest number of edits (insert, delete, replace, swap of neighbours; case-insensitive) that turns <paramref name="query"/>
    /// into some substring of <paramref name="candidate"/>, or null when it exceeds <paramref name="allowed"/>.
    /// </summary>
    private static int? EditsToContain(string query, string candidate, int allowed)
    {
        var m = query.Length;
        var n = candidate.Length;
        if (n == 0) return null;

        // Lower-case both once: the inner loop then compares plain chars.
        Span<char> q = m <= StackLimit ? stackalloc char[m] : new char[m];
        Span<char> c = n <= StackLimit ? stackalloc char[n] : new char[n];
        for (var i = 0; i < m; i++) q[i] = char.ToLowerInvariant(query[i]);
        for (var j = 0; j < n; j++) c[j] = char.ToLowerInvariant(candidate[j]);

        // Cheap reject: every edit but a swap changes at most one letter, so at least m - allowed query letters must exist
        // in the candidate (counted as a multiset; buckets by the low 7 bits, collisions only make this weaker, never wrong).
        Span<int> seen = stackalloc int[128];
        seen.Clear();
        for (var j = 0; j < n; j++) seen[c[j] & 127]++;
        var shared = 0;
        for (var i = 0; i < m; i++)
            if (seen[q[i] & 127] > 0) { seen[q[i] & 127]--; shared++; }
        if (shared < m - allowed) return null;

        // d[i][j]: edits to align query[..i] with a substring of candidate ending at j (it may start anywhere).
        var width = n + 1;
        Span<int> older = width <= StackLimit ? stackalloc int[width] : new int[width];
        Span<int> prev = width <= StackLimit ? stackalloc int[width] : new int[width];
        Span<int> cur = width <= StackLimit ? stackalloc int[width] : new int[width];
        prev.Clear();   // row 0: an empty query matches anywhere for free

        for (var i = 1; i <= m; i++)
        {
            cur[0] = i;
            var rowBest = i;
            var qi = q[i - 1];
            for (var j = 1; j <= n; j++)
            {
                var best = prev[j - 1] + (qi == c[j - 1] ? 0 : 1);
                var del = prev[j] + 1;
                if (del < best) best = del;
                var ins = cur[j - 1] + 1;
                if (ins < best) best = ins;
                if (i > 1 && j > 1 && qi == c[j - 2] && q[i - 2] == c[j - 1] && older[j - 2] + 1 < best) best = older[j - 2] + 1;
                cur[j] = best;
                if (best < rowBest) rowBest = best;
            }

            if (rowBest > allowed) return null;   // no alignment can get back under the limit
            var tmp = older;
            older = prev;
            prev = cur;
            cur = tmp;
        }

        var result = int.MaxValue;
        for (var j = 0; j <= n; j++) result = Math.Min(result, prev[j]);
        return result <= allowed ? result : null;
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
