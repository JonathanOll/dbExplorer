using DbExplorer.Core.Metadata;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// How well a command answers what was typed, with the quick open's ranking (<see cref="FuzzyMatcher"/>, typo tolerance
/// included) applied to the title, then to the category and the keywords, and which characters of the title to mark.
/// </summary>
public static class CommandMatcher
{
    private const int CategoryPenalty = 300;
    private const int KeywordPenalty = 600;
    private const int WordsPenalty = 900;

    /// <summary>Scores below this come from a typo-tolerant match.</summary>
    private const int TypoScore = -50_000;

    /// <summary>A score (higher is better), or null when the command does not match.</summary>
    public static int? Score(string query, AppCommand command)
    {
        query = query.Trim();
        if (query.Length == 0) return 0;

        var title = FuzzyMatcher.ScoreTolerant(query, command.Title);
        if (title > TypoScore) return title;

        // "script format": only with a space, else letters scattered over category and title would outrank a typo in the title.
        if (query.Contains(' ') && FuzzyMatcher.Score(query, $"{command.Category} {command.Title}") is { } withCategory)
            return withCategory - CategoryPenalty;

        var words = $"{command.Category} {command.Keywords}".Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // A keyword must be typed from its start: letters scattered over a keyword list would match almost anything.
        var keyword = words.Where(w => w.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .Select(w => FuzzyMatcher.Score(query, w)).Max();
        if (keyword is { } k) return k - KeywordPenalty;

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 1)
        {
            // Words in any order: "tab close", "script format".
            var sum = 0;
            var all = true;
            foreach (var token in tokens)
            {
                if (command.Title.Contains(token, StringComparison.OrdinalIgnoreCase)) sum += FuzzyMatcher.Score(token, command.Title) ?? 0;
                else if (!words.Any(w => w.StartsWith(token, StringComparison.OrdinalIgnoreCase))) { all = false; break; }
            }
            if (all) return sum - WordsPenalty;
        }

        if (title is not null) return title;
        return words.Select(w => FuzzyMatcher.ScoreTolerant(query, w)).Max() is { } typo ? typo - KeywordPenalty : null;
    }

    /// <summary>
    /// Indexes of <paramref name="title"/> to draw as matched: the typed text in one piece (preferably at a word start), else
    /// each typed word in one piece, else the letters in order, else (a typo) the closest stretch of the title.
    /// </summary>
    public static IReadOnlyList<int> Positions(string title, string query)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        if (Find(title, query) is var whole and >= 0) return Enumerable.Range(whole, query.Length).ToList();

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var places = tokens.Select(t => (At: Find(title, t), t.Length)).ToList();
        if (places.All(p => p.At >= 0))
            return places.SelectMany(p => Enumerable.Range(p.At, p.Length)).Distinct().Order().ToList();

        var letters = string.Concat(tokens);
        var inOrder = InOrder(title, letters);
        return inOrder.Count > 0 ? inOrder : Closest(title, letters);
    }

    private static bool Same(char a, char b) => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    private static int Find(string title, string part)
    {
        var first = -1;
        for (var i = title.IndexOf(part, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = title.IndexOf(part, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (i == 0 || !char.IsLetterOrDigit(title[i - 1])) return i;
            if (first < 0) first = i;
        }
        return first;
    }

    // Every letter in order, each continuing the previous run or else starting a word ("nss": New SQL script); empty when some
    // letter cannot be placed at all.
    private static List<int> InOrder(string title, string letters)
    {
        var found = new List<int>(letters.Length);
        var at = 0;
        foreach (var letter in letters)
        {
            var pick = -1;
            if (found.Count > 0 && at < title.Length && Same(title[at], letter)) pick = at;
            for (var i = at; pick < 0 && i < title.Length; i++)
                if (Same(title[i], letter) && (i == 0 || !char.IsLetterOrDigit(title[i - 1]))) pick = i;
            for (var i = at; pick < 0 && i < title.Length; i++)
                if (Same(title[i], letter)) pick = i;
            if (pick < 0) return Greedy(title, letters);
            found.Add(pick);
            at = pick + 1;
        }
        return found;
    }

    // Leftmost placement: succeeds whenever the letters are a subsequence (jumping to a word start may overshoot).
    private static List<int> Greedy(string title, string letters)
    {
        var found = new List<int>(letters.Length);
        for (var i = 0; i < title.Length && found.Count < letters.Length; i++)
            if (Same(title[i], letters[found.Count])) found.Add(i);
        return found.Count == letters.Length ? found : [];
    }

    // The stretch of the title sharing the longest common subsequence with the typed letters; nothing when it shares too little.
    private static List<int> Closest(string title, string letters)
    {
        List<int> best = [];
        for (var start = 0; start < title.Length; start++)
        {
            var common = Common(title, start, Math.Min(title.Length - start, letters.Length + 1), letters);
            if (common.Count > best.Count) best = common;
        }
        return best.Count >= 3 && best.Count >= letters.Length - 2 ? best : [];
    }

    private static List<int> Common(string title, int start, int length, string letters)
    {
        var table = new int[length + 1, letters.Length + 1];
        for (var i = length - 1; i >= 0; i--)
            for (var j = letters.Length - 1; j >= 0; j--)
                table[i, j] = Same(title[start + i], letters[j])
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
        var found = new List<int>();
        for (int i = 0, j = 0; i < length && j < letters.Length;)
        {
            if (Same(title[start + i], letters[j])) { found.Add(start + i); i++; j++; }
            else if (table[i + 1, j] >= table[i, j + 1]) i++;
            else j++;
        }
        return found;
    }
}
