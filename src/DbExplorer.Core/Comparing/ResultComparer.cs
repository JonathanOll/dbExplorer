using System.Text;

namespace DbExplorer.Core.Comparing;

/// <summary>Compares the rows of two results, matched by key columns or by position. Pure and cancellable: run it off the UI thread.</summary>
public static class ResultComparer
{
    private const int ExampleKeys = 3;
    private const int CancelEvery = 1024;

    public static ComparisonResult Compare(
        ComparedData left, ComparedData right, ComparisonOptions options, CancellationToken ct = default)
    {
        var matchers = options.Compared.Select(p => ValueMatcher.For(left, right, p, options)).ToArray();
        var duplicates = DuplicateKeys.None;
        var partners = options.Keys.Count == 0
            ? ByPosition(left.Rows.Count, right.Rows.Count)
            : ByKey(left, right, options, out duplicates, ct);

        // Right rows without a partner go after the matched left row that precedes them on the right.
        var matched = new int[right.Rows.Count];
        Array.Fill(matched, -1);
        for (var l = 0; l < partners.Length; l++)
            if (partners[l] >= 0) matched[partners[l]] = l;
        var addedAfter = new Dictionary<int, List<int>>();
        var anchor = -1;
        for (var r = 0; r < matched.Length; r++)
        {
            if (matched[r] >= 0)
            {
                anchor = matched[r];
                continue;
            }
            if (!addedAfter.TryGetValue(anchor, out var list)) addedAfter[anchor] = list = [];
            list.Add(r);
        }

        var rows = new List<ComparedRow>(Math.Max(left.Rows.Count, right.Rows.Count));
        int same = 0, changed = 0, added = 0, removed = 0;
        var cells = new List<int>();

        void Added(int after)
        {
            if (!addedAfter.TryGetValue(after, out var list)) return;
            foreach (var r in list) rows.Add(new ComparedRow(RowChange.Added, -1, r, []));
            added += list.Count;
        }

        Added(-1);
        for (var l = 0; l < partners.Length; l++)
        {
            if (l % CancelEvery == 0) ct.ThrowIfCancellationRequested();
            var r = partners[l];
            if (r < 0)
            {
                rows.Add(new ComparedRow(RowChange.Removed, l, -1, []));
                removed++;
            }
            else
            {
                cells.Clear();
                for (var c = 0; c < matchers.Length; c++)
                {
                    var pair = options.Compared[c];
                    if (!matchers[c].Same(ValueMatcher.ValueAt(left.Rows[l], pair.Left), ValueMatcher.ValueAt(right.Rows[r], pair.Right)))
                        cells.Add(c);
                }
                if (cells.Count == 0)
                {
                    rows.Add(new ComparedRow(RowChange.Same, l, r, []));
                    same++;
                }
                else
                {
                    rows.Add(new ComparedRow(RowChange.Changed, l, r, cells.ToArray()));
                    changed++;
                }
            }
            Added(l);
        }

        return new ComparisonResult(rows, same, changed, added, removed, duplicates);
    }

    // For each left row, the right row it is compared with (-1: none).
    private static int[] ByPosition(int left, int right)
    {
        var partners = new int[left];
        for (var i = 0; i < left; i++) partners[i] = i < right ? i : -1;
        return partners;
    }

    private static int[] ByKey(
        ComparedData left, ComparedData right, ComparisonOptions options, out DuplicateKeys duplicates, CancellationToken ct)
    {
        var matchers = options.Keys.Select(p => ValueMatcher.For(left, right, p, options)).ToArray();
        var builder = new StringBuilder();
        var examples = new List<string>();

        // Key -> the right rows that have it, taken in order.
        var index = new Dictionary<string, Slot>(right.Rows.Count, StringComparer.Ordinal);
        var rightRepeated = 0;
        for (var r = 0; r < right.Rows.Count; r++)
        {
            if (r % CancelEvery == 0) ct.ThrowIfCancellationRequested();
            var key = Key(builder, right.Rows[r], options.Keys, matchers, left: false);
            if (!index.TryGetValue(key, out var slot))
            {
                index[key] = new Slot { First = r };
                continue;
            }
            if (slot.More is null)
            {
                slot.More = new Queue<int>();
                rightRepeated++;
                Example(examples, right.Rows[r], options.Keys, left: false);
            }
            slot.More.Enqueue(r);
            rightRepeated++;
        }

        var partners = new int[left.Rows.Count];
        var seen = new Dictionary<string, int>(left.Rows.Count, StringComparer.Ordinal);
        var leftRepeated = 0;
        for (var l = 0; l < left.Rows.Count; l++)
        {
            if (l % CancelEvery == 0) ct.ThrowIfCancellationRequested();
            var key = Key(builder, left.Rows[l], options.Keys, matchers, left: true);
            ref var count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(seen, key, out _);
            count++;
            if (count == 2)
            {
                leftRepeated += 2;
                Example(examples, left.Rows[l], options.Keys, left: true);
            }
            else if (count > 2) leftRepeated++;

            partners[l] = -1;
            if (!index.TryGetValue(key, out var slot)) continue;
            if (slot.First >= 0)
            {
                partners[l] = slot.First;
                slot.First = -1;
            }
            else if (slot.More is { Count: > 0 }) partners[l] = slot.More.Dequeue();
        }

        duplicates = leftRepeated + rightRepeated == 0 ? DuplicateKeys.None : new DuplicateKeys(leftRepeated, rightRepeated, examples);
        return partners;
    }

    private sealed class Slot
    {
        public int First;
        public Queue<int>? More;
    }

    // Length-prefixed so that no value can look like a separator.
    private static string Key(StringBuilder sb, object?[] row, IReadOnlyList<ColumnPair> keys, ValueMatcher[] matchers, bool left)
    {
        sb.Clear();
        for (var i = 0; i < keys.Count; i++)
        {
            var text = matchers[i].Normalise(ValueMatcher.ValueAt(row, left ? keys[i].Left : keys[i].Right));
            if (text is null) sb.Append('N');
            else sb.Append('V').Append(text.Length).Append(':').Append(text);
        }
        return sb.ToString();
    }

    private static void Example(List<string> examples, object?[] row, IReadOnlyList<ColumnPair> keys, bool left)
    {
        if (examples.Count >= ExampleKeys) return;
        var text = string.Join(", ", keys.Select(k =>
        {
            var value = ValueMatcher.ValueAt(row, left ? k.Left : k.Right);
            var shown = value is null or DBNull ? "NULL" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            return $"{k.Name} = {(shown.Length > 40 ? shown[..40] + "…" : shown)}";
        }));
        if (!examples.Contains(text)) examples.Add(text);
    }
}
