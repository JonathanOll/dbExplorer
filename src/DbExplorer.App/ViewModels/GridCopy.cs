using DbExplorer.Core.Export;

namespace DbExplorer.App.ViewModels;

/// <param name="Text">What goes to the clipboard.</param>
/// <param name="Message">What the status bar says about it.</param>
public sealed record CopyResult(string Text, string Message);

/// <summary>The clipboard text of a grid: the selected cells, or every loaded row when nothing is selected.</summary>
public static class GridCopy
{
    /// <summary>Tab separated, as a spreadsheet pastes it. One cell without header is its value alone, never quoted.</summary>
    public static CopyResult Plain(ExportContext context, bool header)
    {
        var (columns, rows) = context.Data(ExportScope.Selection, ExportKind.Tsv);
        if (!header && columns.Count == 1 && rows.Count == 1)
            return new CopyResult(ValueText.Format(rows[0].Length > 0 ? rows[0][0] : null), "Copied the value.");
        var spec = new ExportSpec(ExportKind.Tsv) { Text = new DelimitedOptions { Header = header } };
        return new CopyResult(Exporter.ToText(spec, columns, rows),
            $"Copied {Rows(rows.Count)}{(header ? " with the column names" : "")}.");
    }

    /// <summary>"Copy as": throws <see cref="ExportException"/> when the rows cannot be written that way.</summary>
    public static CopyResult As(ExportContext context, ExportKind kind)
    {
        var (columns, rows) = context.Data(ExportScope.Selection, kind);
        var spec = new ExportSpec(kind) { Database = context.Kind, Table = context.Table, KeyColumns = context.KeyColumns };
        var message = kind switch
        {
            ExportKind.ColumnNames => $"Copied {columns.Count} column name(s).",
            ExportKind.InList => $"Copied an IN list of the column {(columns.Count > 0 ? columns[0].Name : "")} ({Rows(rows.Count)}, NULL left out).",
            ExportKind.SqlInsert or ExportKind.SqlUpdate when context.Table is null =>
                $"Copied {Rows(rows.Count)} as SQL: replace {ExportSpec.UnnamedTable} with the table name.",
            _ => $"Copied {Rows(rows.Count)} as {Name(kind)}.",
        };
        return new CopyResult(Exporter.ToText(spec, columns, rows), message);
    }

    public static string Name(ExportKind kind) => kind switch
    {
        ExportKind.Csv => "CSV",
        ExportKind.Tsv => "TSV",
        ExportKind.Json => "JSON",
        ExportKind.Xlsx => "Excel (XLSX)",
        ExportKind.Markdown => "Markdown",
        ExportKind.SqlInsert => "SQL INSERT",
        ExportKind.SqlUpdate => "SQL UPDATE",
        ExportKind.InList => "IN (…) list",
        ExportKind.ColumnNames => "column names",
        _ => "text",
    };

    internal static string Rows(long n) => StatementResultViewModel.RowsText(n);
}
