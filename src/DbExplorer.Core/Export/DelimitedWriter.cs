using System.Globalization;

namespace DbExplorer.Core.Export;

/// <summary>CSV, TSV and any other delimited text. RFC 4180 quoting; NULL is never quoted, so it stays apart from text.</summary>
public sealed class DelimitedWriter(TextWriter output, DelimitedOptions options) : RowWriter
{
    private int _columns;
    private long _row;
    private bool _any;

    /// <summary>Whether the last line also gets a row separator (files do, the clipboard does not).</summary>
    public bool TrailingSeparator { get; init; } = true;

    public override void Begin(IReadOnlyList<ExportColumn> columns)
    {
        _columns = columns.Count;
        if (!options.Header) return;
        StartLine();
        if (options.RowNumbers) output.Write(Quote("#") + (_columns > 0 ? options.Delimiter : ""));
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0) output.Write(options.Delimiter);
            output.Write(Quote(Guard(columns[i].Name)));
        }
    }

    public override void Write(object?[] row)
    {
        StartLine();
        _row++;
        if (options.RowNumbers) output.Write(Quote(_row.ToString(CultureInfo.InvariantCulture)) + (_columns > 0 ? options.Delimiter : ""));
        for (var i = 0; i < _columns; i++)
        {
            if (i > 0) output.Write(options.Delimiter);
            var value = Value(row, i);
            if (value is null)
            {
                output.Write(options.NullText);
                continue;
            }
            var text = ValueText.Format(value, options.DateFormat);
            if (value is string or char) text = Guard(text);
            // A text equal to the NULL marker is quoted: otherwise it reads back as NULL.
            output.Write(Quote(text, force: options.NullText.Length > 0 && text == options.NullText));
        }
    }

    public override void End()
    {
        if (_any && TrailingSeparator) output.Write(options.RowSeparator);
    }

    private void StartLine()
    {
        if (_any) output.Write(options.RowSeparator);
        _any = true;
    }

    private string Guard(string text) => options.GuardFormulas && IsFormulaLike(text) ? "'" + text : text;

    /// <summary>A spreadsheet may read this text as a formula (full-width signs included, and a leading tab or line break).</summary>
    public static bool IsFormulaLike(string text) =>
        text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n' or '＝' or '＋' or '－' or '＠';

    private string Quote(string text, bool force = false)
    {
        var quote = options.Quoting switch
        {
            QuoteMode.Always => true,
            QuoteMode.Never => false,
            _ => force || (text.Length > 0
                && (text.Contains(options.Delimiter, StringComparison.Ordinal) || text.Contains(options.RowSeparator, StringComparison.Ordinal)
                    || text.AsSpan().IndexOfAny('"', '\r', '\n') >= 0 || text[0] == ' ' || text[^1] == ' ')),
        };
        return quote ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}
