using System.Globalization;
using DbExplorer.Core.Querying;

namespace DbExplorer.App.ViewModels;

/// <param name="Text">What the cell shows.</param>
/// <param name="Tooltip">Full value when <paramref name="Text"/> is a shortened form of it; otherwise null.</param>
public readonly record struct FormattedCell(string Text, string? Tooltip, bool IsNull, bool IsBinary, bool IsNumber);

/// <summary>
/// Turns a raw driver value into display text. It looks only at the RUNTIME type of the value, never at the column's
/// declared CLR type: Microsoft.Data.Sqlite returns DATETIME as string and BOOLEAN as long although the schema says
/// DateTime/bool. Everything is invariant-culture so a grid reads the same on any machine.
/// </summary>
public static class CellFormatter
{
    public const int MaxCellChars = 200;
    public const int MaxTooltipChars = 4000;
    public const int BinaryPreviewBytes = 32;

    public static FormattedCell Format(object? value)
    {
        switch (value)
        {
            case null:
                return new FormattedCell("NULL", null, IsNull: true, IsBinary: false, IsNumber: false);

            case byte[] bytes:
                var preview = "0x" + Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, BinaryPreviewBytes));
                return bytes.Length > BinaryPreviewBytes
                    ? new FormattedCell(preview + "…", bytes.Length.ToString(CultureInfo.InvariantCulture) + " bytes", false, true, false)
                    : new FormattedCell(preview, null, false, true, false);

            case string s:
                return FormatText(s);

            default:
                return new FormattedCell(ToInvariantText(value), null, false, false, IsNumeric(value));
        }
    }

    /// <summary>
    /// Filter-box text that selects rows equal to <paramref name="value"/>: "null" for NULL, otherwise "=" followed by
    /// the invariant text (the "=" also keeps the string "null" from being read as the NULL filter).
    /// Binary values cannot be filtered on.
    /// </summary>
    public static string FilterText(object? value, string column = "")
    {
        return value switch
        {
            null => "null",
            byte[] => throw new FilterException(column, "binary values cannot be used as a filter."),
            _ => "=" + ToInvariantText(value),
        };
    }

    /// <summary>Filter-box text that drops the rows equal to <paramref name="value"/>: "!null" for NULL, otherwise "!=" and the text.</summary>
    public static string ExcludeText(object? value, string column = "")
    {
        return value switch
        {
            null => "!null",
            byte[] => throw new FilterException(column, "binary values cannot be used as a filter."),
            _ => "!=" + ToInvariantText(value),
        };
    }

    private static FormattedCell FormatText(string s)
    {
        var newline = s.AsSpan().IndexOfAny('\r', '\n');
        if (newline < 0 && s.Length <= MaxCellChars) return new FormattedCell(s, null, false, false, false);

        var firstLine = newline < 0 ? s : s[..newline];
        string text;
        if (firstLine.Length > MaxCellChars) text = firstLine[..MaxCellChars] + "…";
        else text = newline < 0 ? firstLine : firstLine + " …";

        var tooltip = s.Length > MaxTooltipChars ? s[..MaxTooltipChars] + "…" : s;
        return new FormattedCell(text, tooltip, false, false, false);
    }

    private static string ToInvariantText(object value) => value switch
    {
        bool b => b ? "true" : "false",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static bool IsNumeric(object value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;
}
