using System.Text.RegularExpressions;

namespace DbExplorer.Core.Querying;

public enum FilterOp { Contains, Eq, Ne, Lt, Gt, Le, Ge, IsNull, NotNull, In }

/// <summary>A parsed column filter. <see cref="Values"/> holds the raw (not yet typed) operand text.</summary>
public record FilterExpr(FilterOp Op, IReadOnlyList<string> Values);

/// <summary>
/// Parses the text typed in a column filter box: <c>text</c> (contains), <c>=v</c>, <c>!=v</c>, <c>&lt;v</c>,
/// <c>&gt;v</c>, <c>&lt;=v</c>, <c>&gt;=v</c>, <c>null</c>, <c>!null</c>, <c>in (a, b, c)</c>.
/// </summary>
public static partial class FilterParser
{
    // Two-character operators first so that "<=" is not read as "<" followed by "=3".
    private static readonly (string Prefix, FilterOp Op)[] Operators =
    [
        ("!=", FilterOp.Ne), ("<=", FilterOp.Le), (">=", FilterOp.Ge),
        ("=", FilterOp.Eq), ("<", FilterOp.Lt), (">", FilterOp.Gt),
    ];

    [GeneratedRegex(@"^in\s*\((.*)\)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InPattern();

    /// <summary>Returns null for empty or whitespace-only text (no filter).</summary>
    public static FilterExpr? Parse(string text)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s)) return null;

        if (s.Equals("null", StringComparison.OrdinalIgnoreCase)) return new FilterExpr(FilterOp.IsNull, []);
        if (s.Equals("!null", StringComparison.OrdinalIgnoreCase)) return new FilterExpr(FilterOp.NotNull, []);

        foreach (var (prefix, op) in Operators)
        {
            if (s.StartsWith(prefix, StringComparison.Ordinal))
                return new FilterExpr(op, [s[prefix.Length..].Trim()]);
        }

        var m = InPattern().Match(s);
        if (m.Success)
        {
            var values = m.Groups[1].Value.Split(',')
                .Select(v => v.Trim())
                .Where(v => v.Length > 0)
                .ToList();
            if (values.Count > 0) return new FilterExpr(FilterOp.In, values);
        }

        return new FilterExpr(FilterOp.Contains, [s]);
    }
}
