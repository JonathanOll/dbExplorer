using System.Globalization;
using System.Text;
using DbExplorer.Core.Groups;

namespace DbExplorer.Core.Comparing;

/// <summary>How the text of a column pair is read: as the dates or GUIDs the other side holds, or as plain text.</summary>
internal enum ValueKind { Plain, Temporal, Guid }

/// <summary>
/// "Same value" for one column pair across providers: the canonical form of <see cref="GroupResults.Canonical"/> (int equals
/// bigint equals decimal), dates and GUIDs held as text (SQLite) equal to typed ones, and the text options.
/// </summary>
internal sealed class ValueMatcher(ValueKind kind, bool ignoreCase, bool ignoreSpaces)
{
    // Enough to meet a non-null value without reading a large column that is all text.
    private const int SniffRows = 1000;

    public static ValueMatcher For(ComparedData left, ComparedData right, ColumnPair pair, ComparisonOptions options)
    {
        var kind = Sniff(left.Rows, pair.Left);
        if (kind == ValueKind.Plain) kind = Sniff(right.Rows, pair.Right);
        return new ValueMatcher(kind, options.IgnoreCase, options.IgnoreSpaces);
    }

    private static ValueKind Sniff(IReadOnlyList<object?[]> rows, int column)
    {
        for (var i = 0; i < rows.Count && i < SniffRows; i++)
        {
            switch (ValueAt(rows[i], column))
            {
                case DateTime or DateTimeOffset or DateOnly: return ValueKind.Temporal;
                case System.Guid: return ValueKind.Guid;
            }
        }
        return ValueKind.Plain;
    }

    public static object? ValueAt(object?[] row, int column) => column < row.Length ? row[column] : null;

    public bool Same(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return true;
        // Most cells are equal values of one type: no text is built for them.
        if (left is not null && right is not null && left.GetType() == right.GetType() && left.Equals(right)) return true;
        return string.Equals(Normalise(left), Normalise(right), StringComparison.Ordinal);
    }

    /// <summary>The comparison text of a value; null for NULL.</summary>
    public string? Normalise(object? value)
    {
        switch (value)
        {
            case null or DBNull:
                return null;
            case string text:
                return Text(text);
            case char c:
                return Text(c.ToString());
            case DateOnly date:
                return GroupResults.Canonical(date.ToDateTime(TimeOnly.MinValue));
            case TimeOnly time:
                return GroupResults.Canonical(time.ToTimeSpan());
            default:
                return GroupResults.Canonical(value);
        }
    }

    private string Text(string text)
    {
        if (kind == ValueKind.Temporal
            && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment))
            return GroupResults.Canonical(moment.Kind == DateTimeKind.Local ? moment.ToUniversalTime() : moment);
        if (kind == ValueKind.Guid && System.Guid.TryParse(text, out var guid)) return GroupResults.Canonical(guid);
        if (ignoreSpaces) text = CollapseSpaces(text);
        return ignoreCase ? text.ToUpperInvariant() : text;
    }

    private static string CollapseSpaces(string text)
    {
        var result = new StringBuilder(text.Length);
        var pending = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pending = result.Length > 0;
                continue;
            }
            if (pending) result.Append(' ');
            pending = false;
            result.Append(c);
        }
        return result.Length == text.Length && text.AsSpan().SequenceEqual(result.ToString()) ? text : result.ToString();
    }
}
