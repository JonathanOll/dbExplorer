using System.Collections;
using System.Globalization;
using System.Text;

namespace DbExplorer.Core.Export;

/// <summary>The plain text of a raw driver value, invariant culture, so a file reads the same on every machine.</summary>
public static class ValueText
{
    /// <summary>Dates as ISO 8601 (or <paramref name="dateFormat"/>), binary as 0x hex, NULL as empty.</summary>
    public static string Format(object? value, string? dateFormat = null)
    {
        var inv = CultureInfo.InvariantCulture;
        if (!string.IsNullOrEmpty(dateFormat) && value is DateTime or DateTimeOffset or DateOnly)
        {
            try { return ((IFormattable)value).ToString(dateFormat, inv); }
            catch (FormatException) { throw new ExportException($"'{dateFormat}' is not a valid date format for these values."); }
        }
        return value switch
        {
            null or DBNull => "",
            string s => s,
            bool b => b ? "true" : "false",
            DateTime d => d.ToString(d.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.FFFFFFF", inv),
            DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", inv),
            DateOnly d => d.ToString("yyyy-MM-dd", inv),
            TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", inv),
            TimeSpan t => t.ToString("c", inv),
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            BitArray bits => Bits(bits),
            Array array => "{" + string.Join(",", array.Cast<object?>().Select(v => v is null ? "NULL" : Format(v))) + "}",
            IFormattable f => f.ToString(null, inv),
            _ => Convert.ToString(value, inv) ?? "",
        };
    }

    internal static string Bits(BitArray bits)
    {
        var sb = new StringBuilder(bits.Length);
        foreach (bool bit in bits) sb.Append(bit ? '1' : '0');
        return sb.ToString();
    }
}
