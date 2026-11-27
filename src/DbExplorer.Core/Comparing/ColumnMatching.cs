namespace DbExplorer.Core.Comparing;

/// <summary>The columns of two results put side by side.</summary>
/// <param name="Pairs">Columns of both sides, in the order of the left one.</param>
/// <param name="TypeNotes">Pairs whose types differ ("id: INTEGER / BIGINT"); their values are still compared by value.</param>
public sealed record ColumnMatch(
    IReadOnlyList<ColumnPair> Pairs,
    IReadOnlyList<string> OnlyLeft,
    IReadOnlyList<string> OnlyRight,
    IReadOnlyList<string> TypeNotes);

/// <summary>Matches columns by name, ignoring case; a name repeated on a side is matched in order of occurrence.</summary>
public static class ColumnMatching
{
    public static ColumnMatch Match(ComparedData left, ComparedData right)
    {
        var free = new Dictionary<string, Queue<int>>(StringComparer.OrdinalIgnoreCase);
        for (var r = 0; r < right.Columns.Count; r++)
        {
            if (!free.TryGetValue(right.Columns[r].Name, out var queue)) free[right.Columns[r].Name] = queue = new Queue<int>();
            queue.Enqueue(r);
        }

        var pairs = new List<ColumnPair>();
        var onlyLeft = new List<string>();
        var notes = new List<string>();
        var used = new HashSet<int>();
        for (var l = 0; l < left.Columns.Count; l++)
        {
            var name = left.Columns[l].Name;
            if (!free.TryGetValue(name, out var queue) || queue.Count == 0)
            {
                onlyLeft.Add(name);
                continue;
            }
            var r = queue.Dequeue();
            used.Add(r);
            pairs.Add(new ColumnPair(name, l, r));
            var a = TypeOf(left, l);
            var b = TypeOf(right, r);
            if (a is not null && b is not null && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) notes.Add($"{name}: {a} / {b}");
        }

        var onlyRight = right.Columns.Select((c, i) => (c.Name, i)).Where(c => !used.Contains(c.i)).Select(c => c.Name).ToList();
        return new ColumnMatch(pairs, onlyLeft, onlyRight, notes);
    }

    // The declared type, else the type of the first value (a script result declares none).
    private static string? TypeOf(ComparedData data, int column)
    {
        if (data.Columns[column].TypeName is { Length: > 0 } declared) return declared;
        for (var i = 0; i < data.Rows.Count && i < 100; i++)
            if (ValueMatcher.ValueAt(data.Rows[i], column) is { } value and not DBNull) return value.GetType().Name;
        return null;
    }
}
