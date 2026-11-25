using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DbExplorer.App.ViewModels;

public enum ExportFormat { Csv, Json }

/// <summary>
/// Turns the rows of a result set into text: TSV for the clipboard (it pastes into a spreadsheet as cells), CSV and JSON for a
/// file. Values are written in the invariant culture, so a file reads the same on every machine. A NULL is an empty field in
/// TSV and CSV, and <c>null</c> in JSON.
/// </summary>
public static class ResultExport
{
    /// <summary>Header line, then one line per row; tab separated. A cell with a tab, a line break or a quote is quoted.</summary>
    public static string ToTsv(IReadOnlyList<ResultColumn> columns, IEnumerable<object?[]> rows) =>
        Delimited(columns, rows, '\t', "\r\n");

    /// <summary>RFC 4180: comma separated, CRLF line ends, a field with a comma, a quote, a line break or an edge space is quoted.</summary>
    public static string ToCsv(IReadOnlyList<ResultColumn> columns, IEnumerable<object?[]> rows) =>
        Delimited(columns, rows, ',', "\r\n");

    /// <summary>An array with one object per row, keyed by column name. Numbers and booleans stay JSON numbers and booleans.</summary>
    public static string ToJson(IReadOnlyList<ResultColumn> columns, IEnumerable<object?[]> rows)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                for (var i = 0; i < columns.Count; i++) WriteJson(writer, columns[i].Name, i < row.Length ? row[i] : null);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The text of one cell: dates as ISO 8601, binary as 0x hex, everything else as the invariant text.</summary>
    public static string FormatCell(object? value) => value switch
    {
        null or DBNull => "",
        string s => s,
        bool b => b ? "true" : "false",
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string Delimited(IReadOnlyList<ResultColumn> columns, IEnumerable<object?[]> rows, char separator, string lineEnd)
    {
        var sb = new StringBuilder();
        sb.AppendJoin(separator, columns.Select(c => Quote(c.Name, separator))).Append(lineEnd);
        foreach (var row in rows)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (i > 0) sb.Append(separator);
                sb.Append(Quote(FormatCell(i < row.Length ? row[i] : null), separator));
            }
            sb.Append(lineEnd);
        }
        return sb.ToString();
    }

    private static string Quote(string text, char separator)
    {
        var needs = text.Length > 0
            && (text.IndexOfAny([separator, '"', '\r', '\n']) >= 0 || text[0] == ' ' || text[^1] == ' '
                || (separator == '\t' && text.Contains('\t')));
        return needs ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    private static void WriteJson(Utf8JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case null or DBNull: writer.WriteNull(name); break;
            case bool b: writer.WriteBoolean(name, b); break;
            case sbyte or byte or short or ushort or int or uint or long: writer.WriteNumber(name, Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case ulong u: writer.WriteNumber(name, u); break;
            case decimal m: writer.WriteNumber(name, m); break;
            case double d when double.IsFinite(d): writer.WriteNumber(name, d); break;
            case float f when float.IsFinite(f): writer.WriteNumber(name, f); break;
            default: writer.WriteString(name, FormatCell(value)); break;
        }
    }
}
