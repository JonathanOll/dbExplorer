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

        // Cheap reject: greedy subsequence check. It also gives the first position each letter can take.
        Span<int> first = m <= StackLimit ? stackalloc int[m] : new int[m];
        var at = 0;
        for (var i = 0; i < m; i++)
        {
            var next = Next(candidate.AsSpan(at), query[i]);
            if (next < 0) return null;
            first[i] = at + next;
            at = first[i] + 1;
        }

        // And the last one, from the end. Outside [first, last] a letter takes part in no alignment, so the DP skips it.
        Span<int> last = m <= StackLimit ? stackalloc int[m] : new int[m];
        at = n;
        for (var i = m - 1; i >= 0; i--)
            last[i] = at = Previous(candidate.AsSpan(0, at), query[i]);

        Span<int> prev = n <= StackLimit ? stackalloc int[n] : new int[n];
        Span<int> cur = n <= StackLimit ? stackalloc int[n] : new int[n];

        // Row 0: best score with query[0] matched at candidate[j].
        for (var j = first[0]; j <= last[0]; j++)
            prev[j] = Eq(candidate[j], query[0]) ? MatchScore + PositionBonus(candidate, j) - GapPenalty * j : NoMatch;

        for (var i = 1; i < m; i++)
        {
            var q = query[i];
            var lower = char.ToLowerInvariant(q);   // once per row: this is the hot loop of every keystroke
            int start = first[i], previousLast = last[i - 1], end = last[i];
            // gapBest = max over k < j-1 of prev[k] - GapPenalty * (j-1-k): best predecessor with at least one skipped char.
            var gapBest = NoMatch;
            for (var j = first[i - 1]; j <= end; j++)
            {
                // prev[j - 1], when the row before has a value there.
                var before = j > first[i - 1] && j - 1 <= previousLast ? prev[j - 1] : NoMatch;
                if (j >= start)
                {
                    var best = NoMatch;
                    var c = candidate[j];
                    if (c == q || char.ToLowerInvariant(c) == lower)
                    {
                        if (before > NoMatch) best = before + ConsecutiveBonus;
                        if (gapBest > best) best = gapBest;
                        if (best > NoMatch) best += MatchScore + PositionBonus(candidate, j);
                    }
                    cur[j] = best;
                }

                // Advance gapBest so it is valid for j + 1.
                var candidateBest = before > gapBest ? before : gapBest;
                gapBest = candidateBest > NoMatch ? candidateBest - GapPenalty : NoMatch;
            }

            var tmp = prev;
            prev = cur;
            cur = tmp;
        }

        var result = NoMatch;
        for (var j = first[m - 1]; j <= last[m - 1]; j++)
            if (prev[j] > result) result = prev[j];

        return result > NoMatch ? result - LengthPenalty * n : null;
    }

    // The first and the last character equal to the letter, whatever its case; on the framework's vectorised search for
    // ASCII letters (the Kelvin sign is the one other character that lowers to one of them).
    private static int Next(ReadOnlySpan<char> text, char letter)
    {
        if (letter < 128)
        {
            var lower = char.ToLowerInvariant(letter);
            var upper = char.ToUpperInvariant(letter);
            return text.IndexOfAny(lower, upper, lower == 'k' ? '\u212A' : upper);
        }
        for (var i = 0; i < text.Length; i++)
            if (Eq(text[i], letter)) return i;
        return -1;
    }

    private static int Previous(ReadOnlySpan<char> text, char letter)
    {
        if (letter < 128)
        {
            var lower = char.ToLowerInvariant(letter);
            var upper = char.ToUpperInvariant(letter);
            return text.LastIndexOfAny(lower, upper, lower == 'k' ? '\u212A' : upper);
        }
        for (var i = text.Length - 1; i >= 0; i--)
            if (Eq(text[i], letter)) return i;
        return -1;
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
    /// The query has at most <see cref="MaxTolerantLength"/> letters, so a whole column of the edit matrix fits in the bits of
    /// one word and each letter of the candidate costs a few operations (Hyyrö's bit-parallel optimal string alignment).
    /// </summary>
    private static int? EditsToContain(string query, string candidate, int allowed)
    {
        var m = query.Length;
        var n = candidate.Length;
        // A substring needs at least m - allowed letters.
        if (n == 0 || n < m - allowed) return null;

        // Cheap reject: each letter of the query that the candidate does not hold at all costs one edit.
        var missing = 0;
        foreach (var letter in query)
            if (Next(candidate, letter) < 0 && ++missing > allowed) return null;

        Span<char> q = stackalloc char[MaxTolerantLength];
        // For each ASCII character, in either case, the positions of the query that hold it.
        Span<int> positions = stackalloc int[128];
        positions.Clear();
        for (var i = 0; i < m; i++)
        {
            q[i] = char.ToLowerInvariant(query[i]);
            if (q[i] >= 128) continue;
            positions[q[i]] |= 1 << i;
            if (char.IsAsciiLetterLower(q[i])) positions[q[i] - 32] |= 1 << i;
        }

        var bottom = 1 << (m - 1);
        int vertical = -1, negative = 0, diagonal = 0, previous = 0;
        var distance = m;
        var result = m;   // the empty substring: every letter deleted
        for (var j = 0; j < n; j++)
        {
            var c = candidate[j];
            int match;
            if (c < 128) match = positions[c];
            else
            {
                match = 0;
                var lower = char.ToLowerInvariant(c);
                for (var i = 0; i < m; i++)
                    if (q[i] == lower) match |= 1 << i;
            }

            var swap = ((~diagonal & match) << 1) & previous;
            diagonal = swap | (((match & vertical) + vertical) ^ vertical) | match | negative;
            var up = negative | ~(diagonal | vertical);
            var down = diagonal & vertical;
            if ((up & bottom) != 0) distance++;
            else if ((down & bottom) != 0) distance--;
            // No carry into the first row: a match may start anywhere in the candidate.
            up <<= 1;
            down <<= 1;
            vertical = down | ~(diagonal | up);
            negative = up & diagonal;
            previous = match;
            if (distance < result) result = distance;
        }

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
