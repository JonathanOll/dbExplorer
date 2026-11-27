namespace DbExplorer.Core.Comparing;

/// <summary>The key columns proposed for a comparison, and in words why (shown to the user, who can change them).</summary>
public sealed record KeyGuess(IReadOnlyList<ColumnPair> Keys, string Reason);

/// <summary>
/// Proposes the columns that identify a row: the primary key when it is among the columns and unique in the loaded rows,
/// else a column (or two) without NULL whose values are unique on both sides, else nothing (match by position).
/// </summary>
public static class KeyGuesser
{
    // Pairs of columns are only tried among the first ones: keys come first, and each try reads every row.
    private const int PairColumns = 5;
    private const int CancelEvery = 1024;

    public static KeyGuess Guess(ComparedData left, ComparedData right, IReadOnlyList<ColumnPair> pairs, CancellationToken ct = default)
    {
        foreach (var (data, isLeft) in new[] { (left, true), (right, false) })
        {
            var declared = data.Columns.Select((c, i) => (c.IsPrimaryKey, i)).Where(c => c.IsPrimaryKey).Select(c => c.i).ToList();
            if (declared.Count == 0) continue;
            var keys = declared.Select(i => pairs.FirstOrDefault(p => (isLeft ? p.Left : p.Right) == i)).OfType<ColumnPair>().ToList();
            if (keys.Count == declared.Count && Unique(left, right, keys, allowNull: true, ct))
                return new KeyGuess(keys, $"Matched by the primary key: {Names(keys)}.");
        }

        // Names that read like an identifier first; fractional numbers are a poor identity, whatever their values.
        var candidates = pairs
            .Where(p => !IsFractional(left, p.Left) && !IsFractional(right, p.Right))
            .OrderBy(p => Rank(p.Name))
            .ThenBy(p => p.Left)
            .ToList();
        foreach (var pair in candidates)
            if (Unique(left, right, [pair], allowNull: false, ct))
                return new KeyGuess([pair], $"Matched by {pair.Name}: its values are unique in the loaded rows.");

        var first = candidates.OrderBy(p => p.Left).Take(PairColumns).ToList();
        for (var i = 0; i < first.Count; i++)
        for (var j = i + 1; j < first.Count; j++)
        {
            List<ColumnPair> keys = [first[i], first[j]];
            if (Unique(left, right, keys, allowNull: false, ct))
                return new KeyGuess(keys, $"Matched by {Names(keys)}: together they are unique in the loaded rows.");
        }

        return new KeyGuess([], "No column identifies the rows, so they are matched by position.");
    }

    private static string Names(IEnumerable<ColumnPair> keys) => string.Join(", ", keys.Select(k => k.Name));

    private static int Rank(string name) =>
        name.Equals("id", StringComparison.OrdinalIgnoreCase) ? 0
        : name.EndsWith("id", StringComparison.OrdinalIgnoreCase) ? 1
        : name.EndsWith("key", StringComparison.OrdinalIgnoreCase) || name.EndsWith("code", StringComparison.OrdinalIgnoreCase) ? 2
        : 3;

    private static bool IsFractional(ComparedData data, int column)
    {
        for (var i = 0; i < data.Rows.Count && i < 100; i++)
            if (ValueMatcher.ValueAt(data.Rows[i], column) is { } value and not DBNull)
                return value is float or double or decimal or byte[];
        return false;
    }

    private static bool Unique(ComparedData left, ComparedData right, IReadOnlyList<ColumnPair> keys, bool allowNull, CancellationToken ct) =>
        left.Rows.Count + right.Rows.Count > 0 && Unique(left.Rows, keys, true, allowNull, ct) && Unique(right.Rows, keys, false, allowNull, ct);

    private static bool Unique(
        IReadOnlyList<object?[]> rows, IReadOnlyList<ColumnPair> keys, bool isLeft, bool allowNull, CancellationToken ct)
    {
        var seen = new HashSet<object>(KeyEquality.Instance);
        for (var r = 0; r < rows.Count; r++)
        {
            if (r % CancelEvery == 0) ct.ThrowIfCancellationRequested();
            var values = new object?[keys.Count];
            for (var k = 0; k < keys.Count; k++)
            {
                values[k] = ValueMatcher.ValueAt(rows[r], isLeft ? keys[k].Left : keys[k].Right);
                if (!allowNull && values[k] is null or DBNull) return false;
            }
            if (!seen.Add(values)) return false;   // stops at the first repeat: most columns are rejected at once
        }
        return true;
    }

    private sealed class KeyEquality : IEqualityComparer<object>
    {
        public static readonly KeyEquality Instance = new();

        public new bool Equals(object? x, object? y) =>
            ((System.Collections.IStructuralEquatable)x!).Equals(y, System.Collections.StructuralComparisons.StructuralEqualityComparer);

        public int GetHashCode(object obj) =>
            ((System.Collections.IStructuralEquatable)obj).GetHashCode(System.Collections.StructuralComparisons.StructuralEqualityComparer);
    }
}
