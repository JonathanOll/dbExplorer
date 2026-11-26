using System.Text;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Export;

/// <summary>Everything that decides what the written text or file looks like.</summary>
public sealed record ExportSpec(ExportKind Kind)
{
    /// <summary>Placeholder table name used when none is given.</summary>
    public const string UnnamedTable = "table_name";

    /// <summary>
    /// Delimiter, header, NULL text... TSV always uses a tab; CSV uses the delimiter given here, or a comma when it is the
    /// default tab.
    /// </summary>
    public DelimitedOptions Text { get; init; } = new();

    /// <summary>Dialect of the SQL kinds.</summary>
    public DbKind Database { get; init; } = DbKind.Sqlite;

    /// <summary>Target of INSERT and UPDATE.</summary>
    public TableRef? Table { get; init; }

    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    public ExportEncoding Encoding { get; init; } = ExportEncoding.Automatic;
}

/// <summary>Feeds a writer: <see cref="RowWriter.Begin"/>, then every row. The exporter calls <see cref="RowWriter.End"/>.</summary>
public delegate Task RowSource(RowWriter sink, CancellationToken ct);

/// <param name="Rows">Rows written.</param>
/// <param name="Warning">Something about the written file the user should know, or null.</param>
public sealed record ExportOutcome(long Rows, string? Warning);

public static class Exporter
{
    private const int ProgressStep = 500;

    /// <summary>Rows already in memory as a source.</summary>
    public static RowSource FromRows(IReadOnlyList<ExportColumn> columns, IEnumerable<object?[]> rows) => (sink, ct) =>
    {
        sink.Begin(columns);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            sink.Write(row);
        }
        return Task.CompletedTask;
    };

    /// <summary>The text of the rows (clipboard, preview): no trailing line break.</summary>
    public static string ToText(ExportSpec spec, IReadOnlyList<ExportColumn> columns, IEnumerable<object?[]> rows)
    {
        var text = new StringWriter();
        var writer = TextRowWriter(spec, text, trailing: false);
        writer.Begin(columns);
        foreach (var row in rows) writer.Write(row);
        writer.End();
        var result = text.ToString();
        // The delimited writer already leaves the last line open; a value may legitimately end with a line break there.
        return spec.Kind is ExportKind.Csv or ExportKind.Tsv or ExportKind.Delimited ? result : result.TrimEnd('\n');
    }

    /// <summary>
    /// Writes the rows to <paramref name="path"/> off the calling thread. Everything goes to a temporary file next to it,
    /// which replaces the target only when complete: a cancelled or failed export leaves no partial file, and does not
    /// destroy a file that was already there. <paramref name="progress"/> receives the number of rows written.
    /// </summary>
    public static Task<ExportOutcome> ToFileAsync(
        ExportSpec spec, string path, RowSource source, IProgress<long>? progress, CancellationToken ct) => Task.Run(async () =>
    {
        var partial = path + "." + Guid.NewGuid().ToString("N")[..8] + ".partial";
        try
        {
            ExportOutcome outcome;
            await using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                if (spec.Kind == ExportKind.Xlsx)
                {
                    using var xlsx = new XlsxWriter(stream, spec.Text.Header);
                    outcome = await RunAsync(xlsx, source, progress, ct).ConfigureAwait(false);
                }
                else
                {
                    await using var text = new StreamWriter(stream, EncodingOf(spec), 1 << 16, leaveOpen: true);
                    outcome = await RunAsync(TextRowWriter(spec, text), source, progress, ct).ConfigureAwait(false);
                }
            }
            ct.ThrowIfCancellationRequested();
            File.Move(partial, path, overwrite: true);
            return outcome;
        }
        catch (Exception e) when (e is not OperationCanceledException && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);   // a provider reports an interrupted command with its own exception
        }
        finally
        {
            try { File.Delete(partial); }   // nothing to delete after a successful move
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }, CancellationToken.None);

    private static async Task<ExportOutcome> RunAsync(RowWriter writer, RowSource source, IProgress<long>? progress, CancellationToken ct)
    {
        var counter = new CountingWriter(writer, progress, ct);
        await source(counter, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        counter.End();
        progress?.Report(counter.Rows);
        return new ExportOutcome(counter.Rows, writer.Warning);
    }

    /// <summary>The writer of a text format.</summary>
    public static RowWriter TextRowWriter(ExportSpec spec, TextWriter output, bool trailing = true)
    {
        var table = spec.Table ?? new TableRef(null, ExportSpec.UnnamedTable);
        DelimitedWriter Delimited(string delimiter) => new(output, spec.Text with { Delimiter = delimiter }) { TrailingSeparator = trailing };
        return spec.Kind switch
        {
            ExportKind.Csv => Delimited(spec.Text.Delimiter == "\t" ? "," : spec.Text.Delimiter),
            ExportKind.Tsv => Delimited("\t"),
            ExportKind.Delimited => Delimited(spec.Text.Delimiter),
            ExportKind.Json => new JsonRowWriter(output),
            ExportKind.Markdown => new MarkdownWriter(output, spec.Text.NullText, spec.Text.DateFormat),
            ExportKind.SqlInsert => new SqlInsertWriter(output, spec.Database, table),
            ExportKind.SqlUpdate => new SqlUpdateWriter(output, spec.Database, table, spec.KeyColumns),
            ExportKind.InList => new InListWriter(output, spec.Database),
            ExportKind.ColumnNames => new ColumnNamesWriter(output),
            _ => throw new ExportException($"{spec.Kind} is not a text format."),
        };
    }

    public static Encoding EncodingOf(ExportSpec spec) => spec.Encoding switch
    {
        ExportEncoding.Utf8 => new UTF8Encoding(false),
        ExportEncoding.Utf8Bom => new UTF8Encoding(true),
        ExportEncoding.Utf16 => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
        ExportEncoding.Windows1252 => CodePagesEncodingProvider.Instance.GetEncoding(1252)!,
        // A byte order mark makes a spreadsheet read accents right; JSON must not have one.
        _ => new UTF8Encoding(spec.Kind is ExportKind.Csv or ExportKind.Tsv),
    };

    public static string Extension(ExportKind kind) => kind switch
    {
        ExportKind.Csv => "csv",
        ExportKind.Tsv => "tsv",
        ExportKind.Json => "json",
        ExportKind.Xlsx => "xlsx",
        ExportKind.Markdown => "md",
        ExportKind.SqlInsert or ExportKind.SqlUpdate or ExportKind.InList => "sql",
        _ => "txt",
    };

    private sealed class CountingWriter(RowWriter inner, IProgress<long>? progress, CancellationToken ct) : RowWriter
    {
        public long Rows { get; private set; }

        public override void Begin(IReadOnlyList<ExportColumn> columns) => inner.Begin(columns);

        public override void Write(object?[] row)
        {
            ct.ThrowIfCancellationRequested();
            inner.Write(row);
            if (++Rows % ProgressStep == 0) progress?.Report(Rows);
        }

        public override void End() => inner.End();
    }
}
