namespace DbExplorer.Core.Export;

/// <summary>The shapes rows can be written in, for the clipboard and for files.</summary>
public enum ExportKind { Csv, Tsv, Json, Xlsx, SqlInsert, Markdown, SqlUpdate, InList, ColumnNames, Delimited }

/// <summary>When a delimited field is put between double quotes.</summary>
public enum QuoteMode { WhenNeeded, Always, Never }

/// <summary>Encoding of an exported text file. Automatic: UTF-8, with a byte order mark for CSV and TSV only.</summary>
public enum ExportEncoding { Automatic, Utf8, Utf8Bom, Utf16, Windows1252 }

/// <summary>How delimited text is written (CSV, TSV, "Advanced copy").</summary>
public sealed record DelimitedOptions
{
    public string Delimiter { get; set; } = "\t";

    public string RowSeparator { get; set; } = "\r\n";

    public bool Header { get; set; } = true;

    public bool RowNumbers { get; set; }

    public QuoteMode Quoting { get; set; } = QuoteMode.WhenNeeded;

    /// <summary>What a NULL is written as; empty by default.</summary>
    public string NullText { get; set; } = "";

    /// <summary>.NET format of dates and timestamps; empty means ISO 8601.</summary>
    public string DateFormat { get; set; } = "";

    /// <summary>
    /// Puts an apostrophe before a text that a spreadsheet would run as a formula (it starts with =, +, - or @). Off by
    /// default because it changes the data.
    /// </summary>
    public bool GuardFormulas { get; set; }

    /// <summary>A hand-edited config may hold nulls or unknown values: they become the defaults.</summary>
    public DelimitedOptions Normalized() => this with
    {
        Delimiter = string.IsNullOrEmpty(Delimiter) ? "\t" : Delimiter,
        RowSeparator = string.IsNullOrEmpty(RowSeparator) ? "\r\n" : RowSeparator,
        Quoting = Enum.IsDefined(Quoting) ? Quoting : QuoteMode.WhenNeeded,
        NullText = NullText ?? "",
        DateFormat = DateFormat ?? "",
    };
}

/// <summary>What the "Advanced copy" and "Export" dialogs remember between uses (stored in the app config).</summary>
public sealed record ExportPreferences
{
    public DelimitedOptions Copy { get; set; } = new();

    public ExportKind Format { get; set; } = ExportKind.Csv;

    public ExportEncoding Encoding { get; set; } = ExportEncoding.Automatic;

    public string CsvDelimiter { get; set; } = ",";

    public bool Header { get; set; } = true;

    public string NullText { get; set; } = "";

    public string DateFormat { get; set; } = "";

    public bool GuardFormulas { get; set; }

    public ExportPreferences Normalized() => this with
    {
        Copy = (Copy ?? new()).Normalized(),
        Format = Enum.IsDefined(Format) ? Format : ExportKind.Csv,
        Encoding = Enum.IsDefined(Encoding) ? Encoding : ExportEncoding.Automatic,
        CsvDelimiter = string.IsNullOrEmpty(CsvDelimiter) ? "," : CsvDelimiter,
        NullText = NullText ?? "",
        DateFormat = DateFormat ?? "",
    };
}
