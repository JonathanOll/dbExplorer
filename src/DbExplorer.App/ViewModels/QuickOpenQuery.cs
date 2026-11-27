using DbExplorer.Core.Metadata;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// One typed query, split on spaces: every word must match something of a row (object name, schema, parent table, or what
/// its <see cref="QuickOpenScope"/> names), in any order. Built for each keystroke; remembers the scope matches.
/// <para>
/// A score is made of a class (a word is the object's name, a word is found in the name, neither), a bonus for the active
/// connection, and the sum of the word scores. A typed word that the object matches counts as the object, not as its place.
/// </para>
/// </summary>
internal sealed class QuickOpenQuery
{
    private const int ClassStep = 1_000_000;
    private const int ActiveBonus = 500_000;    // below a class step: a better match elsewhere still wins
    private const int ScopePenalty = 40;        // at equal quality the object beats its place
    private const int WeakPenalty = 60;         // folder, group, environment: below connection and database
    private const int ScopeTypo = -101_000;     // under every strict score, like the typo score of a name

    private readonly Dictionary<QuickOpenScope, int?[]> _scopes = new(ReferenceEqualityComparer.Instance);
    private readonly bool[] _typoPlaces;
    private readonly int?[] _parent;
    private QuickOpenColumnOwner? _owner;

    /// <param name="places">Every connection that can be searched: a word naming one of them exactly is not read as a typo of another.</param>
    public QuickOpenQuery(string text, IEnumerable<QuickOpenScope>? places = null)
    {
        Words = Split(text);
        _parent = new int?[Words.Length];
        _typoPlaces = new bool[Words.Length];
        var all = places?.ToArray() ?? [];
        for (var i = 0; i < Words.Length; i++) _typoPlaces[i] = !all.Any(p => Names(p, Words[i]));
    }

    public string[] Words { get; }

    public static string[] Split(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The word is part of one of the names of the scope (short words name nothing: they are in almost every name).</summary>
    public static bool Names(QuickOpenScope scope, string word) =>
        word.Length >= 3 && (Has(scope.Connection, word) || Has(scope.Database, word) || scope.Others.Any(o => Has(o, word)));

    /// <summary>The word is a name of the scope with a typing mistake.</summary>
    public static bool NearlyNames(QuickOpenScope scope, string word) =>
        FuzzyMatcher.Typo(word, scope.Connection) is not null || FuzzyMatcher.Typo(word, scope.Database) is not null
        || scope.Others.Any(o => FuzzyMatcher.Typo(word, o) is not null);

    private static bool Has(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>The score of a row that is not a column, or null when a word matches nothing of it.</summary>
    public int? Score(QuickOpenItem item)
    {
        if (Words.Length == 0) return 0;
        var qualified = item.DisplayLabel;
        var name = item.Name ?? qualified;
        var sum = 0;
        var rank = 0;
        var anyObject = false;
        for (var i = 0; i < Words.Length; i++)
        {
            var word = Words[i];
            var dot = word.LastIndexOf('.');
            var own = Fuzzy(word, qualified);
            if (own is null && dot >= 0) own = Fuzzy(word, item.Label);   // "connection.table"
            var hit = 0;
            if (own is not null)
            {
                if (Same(word, name) || dot >= 0 && Same(word, qualified)) hit = 2;
                else if (dot < 0 && ReferenceEquals(name, qualified) || IsSubsequence(word.AsSpan(dot + 1), name)) hit = 1;
            }

            var score = own;
            if (hit == 0 && item.Scope is { } scope && Place(scope, i) is { } place && !(place <= score)) score = place;
            if (own is null && (score is null || score <= ScopeTypo))
            {
                // A typo in the name beats a typo in the place.
                if (FuzzyMatcher.Typo(word, item.Scope is null ? item.Label : qualified) is { } typo) score = typo;
            }

            if (score is not { } s) return null;
            sum += s;
            rank = Math.Max(rank, hit);
            anyObject |= own is not null;
        }

        // Only a place was typed: its tables first, then its views, then the rest.
        if (!anyObject) sum += item.Type switch { QuickOpenTypes.Tables => 2, QuickOpenTypes.Views => 1, _ => 0 };
        return rank * ClassStep + (item.Scope is { IsActive: true } ? ActiveBonus : 0) + sum;
    }

    /// <summary>
    /// The score of a column, strict (no typo search over so many): at least one word must match the column's name, the
    /// others may match its table or its place.
    /// </summary>
    public int? Score(in QuickOpenColumn column)
    {
        if (Words.Length == 0) return 0;
        var (owner, name) = column;
        if (!ReferenceEquals(owner, _owner))
        {
            _owner = owner;
            for (var i = 0; i < Words.Length; i++)
            {
                var table = Fuzzy(Words[i], owner.Table);
                var place = owner.Scope is { } scope ? Place(scope, i) : null;
                if (place <= ScopeTypo) place = null;
                _parent[i] = Max(table, place);
            }
        }

        var sum = 0;
        var rank = 0;
        for (var i = 0; i < Words.Length; i++)
        {
            var word = Words[i];
            var dot = word.LastIndexOf('.');
            int? score = null;
            if (dot < 0)
            {
                score = Fuzzy(word, name);
                if (score is not null) rank = Math.Max(rank, Same(word, name) ? 2 : 1);
                else score = _parent[i];
            }
            else if (IsSubsequence(word.AsSpan(dot + 1), name))   // "orders.total": the table is part of what is typed
            {
                score = Fuzzy(word, string.Concat(owner.Prefix, ".", name));
                if (score is not null) rank = Math.Max(rank, word.AsSpan(dot + 1).Equals(name, StringComparison.OrdinalIgnoreCase) ? 2 : 1);
            }
            else score = _parent[i];

            if (score is not { } s) return null;
            sum += s;
        }

        return rank == 0 ? null : rank * ClassStep + (owner.Scope is { IsActive: true } ? ActiveBonus : 0) + sum;
    }

    /// <summary>How well a name fits what was typed: exact 3, start 2, inside 1, scattered letters 0, only by typo -1.</summary>
    public int Quality(string name)
    {
        if (Words.Length == 0) return 2;
        var best = -1;
        foreach (var word in Words)
        {
            var term = word.AsSpan(word.LastIndexOf('.') + 1);
            var span = name.AsSpan();
            var quality = span.Equals(term, StringComparison.OrdinalIgnoreCase) ? 3
                : span.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 2
                : span.Contains(term, StringComparison.OrdinalIgnoreCase) ? 1
                : IsSubsequence(term, name) ? 0
                : -1;
            best = Math.Max(best, quality);
        }

        return best;
    }

    // One word gets the best alignment; several words are scored in one pass each, to stay fast on every keystroke.
    private int? Fuzzy(string word, string text) =>
        Words.Length == 1 ? FuzzyMatcher.Score(word, text) : FuzzyMatcher.ScoreFast(word, text);

    private int? Place(QuickOpenScope scope, int word)
    {
        if (!_scopes.TryGetValue(scope, out var scores))
        {
            scores = new int?[Words.Length];
            for (var i = 0; i < Words.Length; i++) scores[i] = PlaceScore(scope, Words[i], _typoPlaces[i]);
            _scopes.Add(scope, scores);
        }

        return scores[word];
    }

    private static int? PlaceScore(QuickOpenScope scope, string word, bool typos)
    {
        var best = Max(NameScore(word, scope.Connection, abbreviated: true), NameScore(word, scope.Database, abbreviated: true));
        foreach (var other in scope.Others) best = Max(best, NameScore(word, other, abbreviated: false) - WeakPenalty);
        if (best is { } score) return score - ScopePenalty;
        return typos && NearlyNames(scope, word) ? ScopeTypo : null;
    }

    // Stricter than a name: exact, start, inside, or an abbreviation that starts like it ("rst2"); scattered letters name nothing.
    private static int? NameScore(string word, string text, bool abbreviated)
    {
        if (text.Length == 0) return null;
        if (Same(word, text)) return 300;
        var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (at >= 0) return (at == 0 ? 200 : 150) - text.Length;
        return abbreviated && word.Length >= 3 && char.ToLowerInvariant(word[0]) == char.ToLowerInvariant(text[0]) && IsSubsequence(word, text)
            ? 50 - text.Length
            : null;
    }

    private static int? Max(int? a, int? b) => a is null || b > a ? b : a;

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static bool IsSubsequence(ReadOnlySpan<char> word, string text)
    {
        var found = 0;
        for (var i = 0; i < text.Length && found < word.Length; i++)
            if (text[i] == word[found] || char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(word[found])) found++;
        return found == word.Length;
    }
}
