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
    public string Delimiter { get; init; } = "\t";

    public string RowSeparator { get; init; } = "\r\n";

    public bool Header { get; init; } = true;

    public bool RowNumbers { get; init; }

    public QuoteMode Quoting { get; init; } = QuoteMode.WhenNeeded;

    /// <summary>What a NULL is written as; empty by default.</summary>
    public string NullText { get; init; } = "";

    /// <summary>.NET format of dates and timestamps; empty means ISO 8601.</summary>
    public string DateFormat { get; init; } = "";

    /// <summary>
    /// Puts an apostrophe before a text that a spreadsheet would run as a formula (it starts with =, +, - or @). Off by
    /// default because it changes the data.
    /// </summary>
    public bool GuardFormulas { get; init; }

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
    public DelimitedOptions Copy { get; init; } = new();

    public ExportKind Format { get; init; } = ExportKind.Csv;

    public ExportEncoding Encoding { get; init; } = ExportEncoding.Automatic;

    public string CsvDelimiter { get; init; } = ",";

    public bool Header { get; init; } = true;

    public string NullText { get; init; } = "";

    public string DateFormat { get; init; } = "";

    public bool GuardFormulas { get; init; }

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
