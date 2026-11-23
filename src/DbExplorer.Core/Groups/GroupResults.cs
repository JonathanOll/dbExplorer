using System.Globalization;
using System.Text;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Groups;

/// <summary>One group member's result set, tagged with the database it came from.</summary>
public sealed record MemberSet(string Database, ResultSet Set);

public sealed record MergedResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Rows,
    IReadOnlyList<string> Warnings,
    bool Truncated);

/// <summary>Pure logic that merges and compares the per-member results of a group run.</summary>
public static class GroupResults
{
    public const string DatabaseColumn = "Database";
    public const string PresentInColumn = "Present in";

    private const string NullToken = "\u0000<null>\u0000";

    public static MergedResult Merge(IReadOnlyList<MemberSet> sets)
    {
        var (columns, maps, warnings) = Align(sets);
        var rows = new List<object?[]>();
        for (var m = 0; m < sets.Count; m++)
        {
            foreach (var row in sets[m].Set.Rows)
            {
                var projected = new object?[columns.Count + 1];
                projected[0] = sets[m].Database;
                Project(row, maps[m], projected, 1);
                rows.Add(projected);
            }
        }

        return new MergedResult(
            Prepend(DatabaseColumn, columns), rows, warnings, sets.Any(s => s.Set.Truncated));
    }

    public static MergedResult Differences(IReadOnlyList<MemberSet> sets)
    {
        var (columns, maps, warnings) = Align(sets);

        // Canonical row key -> (first-seen values, per-member occurrence counts); insertion order is kept.
        var order = new List<string>();
        var entries = new Dictionary<string, (object?[] Values, int[] Counts)>(StringComparer.Ordinal);
        for (var m = 0; m < sets.Count; m++)
        {
            foreach (var row in sets[m].Set.Rows)
            {
                var values = new object?[columns.Count];
                Project(row, maps[m], values, 0);
                var key = RowKey(values);
                if (!entries.TryGetValue(key, out var entry))
                {
                    entry = (values, new int[sets.Count]);
                    entries[key] = entry;
                    order.Add(key);
                }
                entry.Counts[m]++;
            }
        }

        var rows = new List<object?[]>();
        foreach (var key in order)
        {
            var (values, counts) = entries[key];
            if (counts.All(c => c == counts[0])) continue;

            var holders = Enumerable.Range(0, sets.Count).Where(i => counts[i] > 0).ToList();
            var annotate = holders.Select(i => counts[i]).Distinct().Count() > 1;
            var presentIn = string.Join(", ", holders.Select(i =>
                annotate ? $"{sets[i].Database}×{counts[i]}" : sets[i].Database));

            var output = new object?[columns.Count + 1];
            output[0] = presentIn;
            Array.Copy(values, 0, output, 1, values.Length);
            rows.Add(output);
        }

        return new MergedResult(
            Prepend(PresentInColumn, columns), rows, warnings, sets.Any(s => s.Set.Truncated));
    }

    /// <summary>Comparison key for one cell value; equal keys mean "same value" across providers.</summary>
    public static string Canonical(object? value)
    {
        switch (value)
        {
            case null:
            case DBNull:
                return NullToken;
            case string s:
                return s;
            case bool b:
                return b ? "1" : "0";
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return NormaliseDecimal(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
            case decimal d:
                return NormaliseDecimal(d);
            case float f:
                return NormaliseFloating(f.ToString("R", CultureInfo.InvariantCulture));
            case double db:
                return NormaliseFloating(db.ToString("R", CultureInfo.InvariantCulture));
            case DateTime dt:
                // "O" keeps the Kind: Utc ends in "Z" (same text as a DateTimeOffset in UTC), others stay as is.
                return dt.ToString("O", CultureInfo.InvariantCulture);
            case DateTimeOffset dto:
                return dto.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            case Guid g:
                return g.ToString("D").ToLowerInvariant();
            case byte[] bytes:
                return Convert.ToHexString(bytes);
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? NullToken;
        }
    }

    private static string NormaliseDecimal(decimal d)
    {
        if (d == 0) return "0";
        // Dividing by 1.000...0 (28 zeros) strips trailing zeros from the scale.
        return (d / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
    }

    private static string NormaliseFloating(string roundTrip)
    {
        // "R" yields e.g. "1", "1.5", "1E+20" or "NaN"; reuse the decimal form when it fits.
        if (decimal.TryParse(roundTrip, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return NormaliseDecimal(d);
        return roundTrip;
    }

    private static string RowKey(object?[] values)
    {
        // Length-prefixed so no value can masquerade as a separator.
        var sb = new StringBuilder();
        foreach (var v in values)
        {
            if (v is null or DBNull)
            {
                sb.Append('N');
                continue;
            }
            var text = Canonical(v);
            sb.Append('V').Append(text.Length).Append(':').Append(text);
        }
        return sb.ToString();
    }

    private static (List<string> Columns, List<int[]> Maps, List<string> Warnings) Align(
        IReadOnlyList<MemberSet> sets)
    {
        var columns = new List<string>();
        var maps = new List<int[]>();
        var warnings = new List<string>();
        if (sets.Count == 0) return (columns, maps, warnings);

        var commonNames = new HashSet<string>(sets[0].Set.Columns, StringComparer.OrdinalIgnoreCase);
        for (var m = 1; m < sets.Count; m++)
            commonNames.IntersectWith(sets[m].Set.Columns);

        foreach (var name in sets[0].Set.Columns)
        {
            if (commonNames.Contains(name) && !columns.Contains(name, StringComparer.OrdinalIgnoreCase))
                columns.Add(name);
        }

        foreach (var member in sets)
        {
            var cols = member.Set.Columns;
            maps.Add(columns.Select(c => IndexOf(cols, c)).ToArray());

            var extra = cols
                .Where(c => !commonNames.Contains(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (extra.Count > 0)
                warnings.Add($"{member.Database}: columns not common to all members were left out: {string.Join(", ", extra)}");
        }

        return (columns, maps, warnings);
    }

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static void Project(object?[] row, int[] map, object?[] target, int offset)
    {
        for (var i = 0; i < map.Length; i++)
            target[offset + i] = map[i] < row.Length ? row[map[i]] : null;
    }

    private static IReadOnlyList<string> Prepend(string first, List<string> columns)
    {
        var all = new List<string>(columns.Count + 1) { first };
        all.AddRange(columns);
        return all;
    }
}
