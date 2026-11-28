using DbExplorer.Core.Metadata;

namespace DbExplorer.Core.Tests;

/// <summary>The matcher takes shortcuts to stay fast on every keystroke: they must not change a single score.</summary>
public class FuzzyMatcherEquivalenceTests
{
    private const string Alphabet = "abkAK_. éÉ\u212A1";

    private static string Text(Random random, int min, int max) =>
        new(Enumerable.Range(0, random.Next(min, max + 1)).Select(_ => Alphabet[random.Next(Alphabet.Length)]).ToArray());

    [Fact]
    public void Score_EqualsTheFullAlignment()
    {
        var random = new Random(7);
        for (var i = 0; i < 20_000; i++)
        {
            var (query, candidate) = (Text(random, 1, 6), Text(random, 1, 24));
            Assert.True(Reference(query, candidate) == FuzzyMatcher.Score(query, candidate), $"'{query}' in '{candidate}'");
        }
    }

    [Fact]
    public void Typo_EqualsTheEditMatrix()
    {
        var random = new Random(11);
        for (var i = 0; i < 40_000; i++)
        {
            var (query, candidate) = (Text(random, 4, 12), Text(random, 0, 24));
            var allowed = query.Length <= 6 ? 1 : 2;
            var edits = Edits(query.ToLowerInvariant(), candidate.ToLowerInvariant());
            int? expected = candidate.Length > 0 && edits <= allowed ? -100_000 - 1_000 * edits - candidate.Length : null;
            Assert.True(expected == FuzzyMatcher.Typo(query, candidate), $"'{query}' in '{candidate}'");
        }
    }

    // The same scoring over every cell, without any shortcut.
    private static int? Reference(string query, string candidate)
    {
        const int none = int.MinValue / 2;
        int n = candidate.Length, m = query.Length;
        if (m > n) return null;
        bool Eq(int i, int j) => char.ToLowerInvariant(query[i]) == char.ToLowerInvariant(candidate[j]);
        int Bonus(int j) => j == 0 ? 20 : !char.IsLetterOrDigit(candidate[j - 1]) ? 14
            : char.IsLower(candidate[j - 1]) && char.IsUpper(candidate[j]) ? 14 : 0;
        var d = new int[m, n];
        for (var i = 0; i < m; i++)
            for (var j = 0; j < n; j++)
            {
                d[i, j] = none;
                if (!Eq(i, j)) continue;
                if (i == 0) { d[i, j] = 16 + Bonus(j) - 2 * j; continue; }
                var best = j > 0 && d[i - 1, j - 1] > none ? d[i - 1, j - 1] + 12 : none;
                for (var k = 0; k < j - 1; k++)
                    if (d[i - 1, k] > none) best = Math.Max(best, d[i - 1, k] - 2 * (j - 1 - k));
                if (best > none) d[i, j] = best + 16 + Bonus(j);
            }
        var result = Enumerable.Range(0, n).Max(j => d[m - 1, j]);
        return result > none ? result - n : null;
    }

    // Optimal string alignment of the query against any substring of the candidate.
    private static int Edits(string q, string c)
    {
        var d = new int[q.Length + 1, c.Length + 1];
        for (var i = 1; i <= q.Length; i++)
        {
            d[i, 0] = i;
            for (var j = 1; j <= c.Length; j++)
            {
                d[i, j] = Math.Min(d[i - 1, j - 1] + (q[i - 1] == c[j - 1] ? 0 : 1), Math.Min(d[i - 1, j], d[i, j - 1]) + 1);
                if (i > 1 && j > 1 && q[i - 1] == c[j - 2] && q[i - 2] == c[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return Enumerable.Range(0, c.Length + 1).Min(j => d[q.Length, j]);
    }
}
