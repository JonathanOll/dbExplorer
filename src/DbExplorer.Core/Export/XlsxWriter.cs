using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace DbExplorer.Core.Export;

/// <summary>
/// A minimal streaming .xlsx writer (a zip of XML parts), so no spreadsheet library is needed. Rows go straight to the
/// compressed stream. Texts are inline strings, which a spreadsheet never evaluates as formulas. A new sheet starts when
/// one is full. Numbers a double cannot hold exactly (more than 15 digits) are written as text.
/// </summary>
public sealed class XlsxWriter : RowWriter, IDisposable
{
    public const int MaxSheetRows = 1_048_576;
    public const int MaxColumns = 16_384;
    public const int MaxCellChars = 32_767;

    private const int StyleHeader = 1, StyleDate = 2, StyleDateTime = 3, StyleTime = 4;
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageNs = "http://schemas.openxmlformats.org/package/2006";
    private const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml";

    private readonly ZipArchive _zip;
    private readonly bool _header;
    private readonly int _rowsPerSheet;
    private IReadOnlyList<ExportColumn> _columns = [];
    private string[] _letters = [];
    private StreamWriter? _sheet;
    private bool _closed;
    private int _sheets;
    private int _row;
    private long _cut;

    /// <param name="output">The file; left open for the caller to close.</param>
    /// <param name="header">Column names in a bold, frozen first row.</param>
    /// <param name="rowsPerSheet">Lowered by tests only.</param>
    public XlsxWriter(Stream output, bool header = true, int rowsPerSheet = MaxSheetRows)
    {
        _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        _header = header;
        _rowsPerSheet = rowsPerSheet;
    }

    public override string? Warning => _cut == 0 ? null
        : $"{_cut.ToString("N0", CultureInfo.InvariantCulture)} cell(s) were cut at {MaxCellChars} characters, the most a spreadsheet cell holds.";

    public override void Begin(IReadOnlyList<ExportColumn> columns)
    {
        if (columns.Count > MaxColumns) throw new ExportException($"A spreadsheet holds at most {MaxColumns} columns.");
        _columns = columns;
        _letters = Enumerable.Range(0, columns.Count).Select(ColumnLetters).ToArray();
        StartSheet();
    }

    public override void Write(object?[] row)
    {
        if (_row >= _rowsPerSheet)
        {
            EndSheet();
            StartSheet();
        }
        StartRow();
        for (var i = 0; i < _columns.Count; i++)
            if (Value(row, i) is { } value) Cell(i, value);
        _sheet!.Write("</row>");
    }

    public override void End()
    {
        EndSheet();
        var sheets = Enumerable.Range(1, _sheets).ToList();
        Part("[Content_Types].xml",
            $"<Types xmlns=\"{PackageNs}/content-types\">"
            + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
            + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
            + $"<Override PartName=\"/xl/workbook.xml\" ContentType=\"{ContentType}.sheet.main+xml\"/>"
            + $"<Override PartName=\"/xl/styles.xml\" ContentType=\"{ContentType}.styles+xml\"/>"
            + string.Concat(sheets.Select(n => $"<Override PartName=\"/xl/worksheets/sheet{n}.xml\" ContentType=\"{ContentType}.worksheet+xml\"/>"))
            + "</Types>");
        Part("_rels/.rels",
            $"<Relationships xmlns=\"{PackageNs}/relationships\">"
            + $"<Relationship Id=\"rId1\" Type=\"{RelNs}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Part("xl/workbook.xml",
            $"<workbook xmlns=\"{Ns}\" xmlns:r=\"{RelNs}\"><sheets>"
            + string.Concat(sheets.Select(n => $"<sheet name=\"{(n == 1 ? "Result" : $"Result {n}")}\" sheetId=\"{n}\" r:id=\"rId{n}\"/>"))
            + "</sheets></workbook>");
        Part("xl/_rels/workbook.xml.rels",
            $"<Relationships xmlns=\"{PackageNs}/relationships\">"
            + string.Concat(sheets.Select(n => $"<Relationship Id=\"rId{n}\" Type=\"{RelNs}/worksheet\" Target=\"worksheets/sheet{n}.xml\"/>"))
            + $"<Relationship Id=\"rId{_sheets + 1}\" Type=\"{RelNs}/styles\" Target=\"styles.xml\"/></Relationships>");
        Part("xl/styles.xml",
            $"<styleSheet xmlns=\"{Ns}\">"
            + "<numFmts count=\"3\"><numFmt numFmtId=\"164\" formatCode=\"yyyy\\-mm\\-dd\"/>"
            + "<numFmt numFmtId=\"165\" formatCode=\"yyyy\\-mm\\-dd\\ hh:mm:ss\"/><numFmt numFmtId=\"166\" formatCode=\"hh:mm:ss\"/></numFmts>"
            + "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>"
            + "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
            + "<borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs>"
            + "<cellXfs count=\"5\"><xf/><xf fontId=\"1\" applyFont=\"1\"/><xf numFmtId=\"164\" applyNumberFormat=\"1\"/>"
            + "<xf numFmtId=\"165\" applyNumberFormat=\"1\"/><xf numFmtId=\"166\" applyNumberFormat=\"1\"/></cellXfs>"
            + "</styleSheet>");
        _closed = true;
        _zip.Dispose();   // writes the zip directory
    }

    /// <summary>Releases the archive of an export that did not reach <see cref="End"/> (the caller deletes the file).</summary>
    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            _sheet?.Dispose();
            _zip.Dispose();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    private void StartSheet()
    {
        _sheets++;
        _row = 0;
        var entry = _zip.CreateEntry($"xl/worksheets/sheet{_sheets}.xml", CompressionLevel.Fastest);
        _sheet = new StreamWriter(entry.Open(), new UTF8Encoding(false), 1 << 16);
        _sheet.Write($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"{Ns}\">");
        if (_header)
            _sheet.Write("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        _sheet.Write("<sheetData>");
        if (!_header) return;
        StartRow();
        for (var i = 0; i < _columns.Count; i++) TextCell(i, _columns[i].Name, StyleHeader);
        _sheet.Write("</row>");
    }

    private void EndSheet()
    {
        _sheet!.Write("</sheetData></worksheet>");
        _sheet.Dispose();
        _sheet = null;
    }

    private void StartRow()
    {
        _row++;
        _sheet!.Write("<row r=\"");
        _sheet.Write(_row);
        _sheet.Write("\">");
    }

    private void Cell(int column, object value)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (value)
        {
            case bool b: Raw(column, b ? "1" : "0", " t=\"b\""); break;
            case sbyte or byte or short or ushort or int or uint: Raw(column, ((IFormattable)value).ToString(null, inv)); break;
            case long l when l is >= -999_999_999_999_999 and <= 999_999_999_999_999: Raw(column, l.ToString(inv)); break;
            case decimal m when SignificantDigits(m) <= 15: Raw(column, m.ToString(inv)); break;
            case double d when double.IsFinite(d): Raw(column, d.ToString("R", inv)); break;
            case float f when float.IsFinite(f): Raw(column, f.ToString("R", inv)); break;
            // Serial dates are only right from March 1900 on (Excel counts a 29 February 1900 that never was).
            case DateTime dt when dt >= new DateTime(1900, 3, 1):
                Date(column, dt, dt.TimeOfDay == TimeSpan.Zero ? StyleDate : StyleDateTime); break;
            case DateOnly day when day >= new DateOnly(1900, 3, 1): Date(column, day.ToDateTime(TimeOnly.MinValue), StyleDate); break;
            case TimeOnly time: Raw(column, (time.Ticks / (double)TimeSpan.TicksPerDay).ToString("R", inv), $" s=\"{StyleTime}\""); break;
            default: TextCell(column, ValueText.Format(value), 0); break;
        }
    }

    private void Date(int column, DateTime value, int style) =>
        Raw(column, value.ToOADate().ToString("R", CultureInfo.InvariantCulture), $" s=\"{style}\"");

    private static int SignificantDigits(decimal m) =>
        Math.Abs(m).ToString(CultureInfo.InvariantCulture).Replace(".", "").Trim('0').Length;

    private void Reference(int column)
    {
        _sheet!.Write("<c r=\"");
        _sheet.Write(_letters[column]);
        _sheet.Write(_row);
        _sheet.Write('"');
    }

    private void Raw(int column, string value, string attributes = "")
    {
        Reference(column);
        _sheet!.Write(attributes);
        _sheet.Write("><v>");
        _sheet.Write(value);
        _sheet.Write("</v></c>");
    }

    private void TextCell(int column, string text, int style)
    {
        if (text.Length > MaxCellChars)
        {
            var length = char.IsHighSurrogate(text[MaxCellChars - 1]) ? MaxCellChars - 1 : MaxCellChars;
            text = text[..length];
            _cut++;
        }
        Reference(column);
        if (style != 0) _sheet!.Write($" s=\"{style}\"");
        _sheet!.Write(" t=\"inlineStr\"><is><t xml:space=\"preserve\">");
        _sheet.Write(Escape(text));
        _sheet.Write("</t></is></c>");
    }

    /// <summary>
    /// XML escaping, plus Excel's <c>_xHHHH_</c> form for the characters XML cannot hold (and for a literal text that looks
    /// like that form).
    /// </summary>
    internal static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '\r': sb.Append("&#13;"); break;
                case '_' when LooksEscaped(text, i): sb.Append("_x005F_"); break;
                case '\t' or '\n': sb.Append(c); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        sb.Append(c).Append(text[++i]);
                    }
                    else if (c < 0x20 || char.IsSurrogate(c) || c is '￾' or '￿')
                    {
                        sb.Append("_x").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)).Append('_');
                    }
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static bool LooksEscaped(string s, int i) =>
        i + 6 < s.Length && s[i + 1] == 'x' && s[i + 6] == '_'
        && char.IsAsciiHexDigit(s[i + 2]) && char.IsAsciiHexDigit(s[i + 3]) && char.IsAsciiHexDigit(s[i + 4]) && char.IsAsciiHexDigit(s[i + 5]);

    /// <summary>0 is "A", 25 "Z", 26 "AA".</summary>
    internal static string ColumnLetters(int index)
    {
        var letters = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) letters = (char)('A' + (n - 1) % 26) + letters;
        return letters;
    }

    private void Part(string name, string xml)
    {
        using var writer = new StreamWriter(_zip.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
        writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        writer.Write(xml);
    }
}
