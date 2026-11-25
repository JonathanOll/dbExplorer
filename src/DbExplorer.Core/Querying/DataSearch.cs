using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;

namespace DbExplorer.Core.Querying;

public enum DataMatchMode { Contains, Exact, StartsWith }

/// <param name="Text">The value looked for (compared as text: numbers, dates and GUIDs are cast by the database).</param>
/// <param name="Schema">Only the tables of this schema (exact name, case-insensitive); null or empty = all.</param>
/// <param name="TableContains">Only tables whose name contains this text; null or empty = all.</param>
/// <param name="MaxRowsPerTable">Rows kept per table (the query itself is limited to it).</param>
/// <param name="MaxParallel">Tables searched at the same time, each on its own connection of the session pool (1 to 4).</param>
/// <param name="TableBudget">A table that takes longer than this is abandoned and reported; null = no limit (only Cancel stops it).</param>
public sealed record DataSearchOptions(
    string Text,
    DataMatchMode Mode = DataMatchMode.Contains,
    string? Schema = null,
    string? TableContains = null,
    int MaxRowsPerTable = 20,
    int MaxParallel = 4,
    TimeSpan? TableBudget = null);

/// <summary>One cell of a result row preview. <see cref="IsMatch"/> marks the cell that matched.</summary>
public sealed record DataCell(string Column, string Text, bool IsMatch);

/// <summary>One matching cell. <see cref="Value"/> is the cell as text (what a filter on it uses); <see cref="Preview"/> the row.</summary>
public sealed record DataMatch(TableRef Table, string Column, string Value, IReadOnlyList<DataCell> Preview);

/// <param name="Table">The table just finished (null for the first report).</param>
public sealed record DataSearchProgress(int TablesDone, int TablesTotal, TableRef? Table);

public sealed record DataSearchFailure(TableRef Table, string Message);

/// <summary>How a search ended. A cancelled search keeps what it found.</summary>
public sealed record DataSearchSummary(
    int TablesTotal, int TablesSearched, int MatchCount, bool Cancelled,
    IReadOnlyList<TableRef> TimedOut, IReadOnlyList<DataSearchFailure> Failures);

/// <summary>
/// Finds a value in the data of a connected database. One bounded, read-only, parameterised SELECT per table: the text columns
/// (and number, GUID and date columns the text can match) are cast to text and compared with LIKE / = in one OR-ed WHERE; the same
/// SELECT returns a 0/1 flag per column so each hit is attributed to its column. Binary columns are never read. Tables run in
/// parallel (at most <see cref="DataSearchOptions.MaxParallel"/> connections), each in the read-only context of
/// <see cref="TableDataLoader.BeginReadOnlyContextAsync"/>. A big database can take a long time: callers report progress and offer Cancel.
/// </summary>
public static class DataSearch
{
    public const int MaxPreviewColumns = 6;
    private const int MaxCellText = 120;
    private const int MaxValueText = 400;

    private enum Kind { Text, Number, Guid, Date }

    /// <summary>The tables a search would visit (views excluded: their rows are the tables' rows), after the schema / name filters and
    /// the columns the text could possibly match.</summary>
    public static IReadOnlyList<TableInfo> Candidates(IReadOnlyList<TableInfo> schema, DataSearchOptions options) =>
        schema.Where(t => !t.IsView && Matches(t.Ref, options) && SearchableColumns(t, options.Text).Count > 0).ToList();

    private static bool Matches(TableRef r, DataSearchOptions o) =>
        (string.IsNullOrWhiteSpace(o.Schema) || string.Equals(r.Schema, o.Schema.Trim(), StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrWhiteSpace(o.TableContains) || r.Name.Contains(o.TableContains.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The columns worth casting: text-like, plus number / GUID / date columns when the text could appear in their text form.</summary>
    public static IReadOnlyList<ColumnInfo> SearchableColumns(TableInfo table, string text) =>
        table.Columns.Where(c => KindOf(c) is { } kind && CanContain(kind, text)).ToList();

    private static Kind? KindOf(ColumnInfo c)
    {
        var type = c.DataType.ToLowerInvariant();
        string[] binary = ["blob", "binary", "image", "bytea", "geometry", "geography", "hierarchyid", "rowversion"];
        if (c.ClrType == typeof(byte[]) || c.ClrType == typeof(Stream) || binary.Any(type.Contains)) return null;
        if (c.ClrType == typeof(string)) return Kind.Text;
        if (c.ClrType == typeof(Guid)) return Kind.Guid;
        if (c.ClrType == typeof(DateTime) || c.ClrType == typeof(DateTimeOffset) || c.ClrType == typeof(DateOnly) || c.ClrType == typeof(TimeOnly)
            || c.ClrType == typeof(TimeSpan)) return Kind.Date;
        if (c.ClrType == typeof(bool)) return null;
        if (c.ClrType.IsPrimitive || c.ClrType == typeof(decimal)) return Kind.Number;
        return type.Contains("char") || type.Contains("text") || type.Contains("json") || type.Contains("xml") || type.Contains("enum") ? Kind.Text : null;
    }

    // A number is never found by text with a letter in it (except the exponent), nor a date by "abc": skip those columns.
    private static bool CanContain(Kind kind, string text) => kind switch
    {
        Kind.Text => true,
        Kind.Number => text.All(ch => char.IsDigit(ch) || ch is '.' or ',' or '-' or '+' or 'e' or 'E' or ' '),
        Kind.Guid => text.All(ch => Uri.IsHexDigit(ch) || ch is '-' or '{' or '}'),
        _ => text.All(ch => char.IsDigit(ch) || ch is '-' or ':' or '/' or ' ' or '.' or 'T' or 't' or 'Z' or 'z' or '+' or ',' or 'A' or 'a' or 'P' or 'p' or 'M' or 'm'),
    };

    /// <summary>
    /// Searches <paramref name="session"/>. <paramref name="onTable"/> receives the matches of each table as soon as its query ends (not
    /// called for a table without matches); calls are serialised. Never throws for a failing table (see
    /// <see cref="DataSearchSummary.Failures"/>) nor for cancellation (<see cref="DataSearchSummary.Cancelled"/>).
    /// </summary>
    public static async Task<DataSearchSummary> RunAsync(
        Session session, DataSearchOptions options, Action<TableRef, IReadOnlyList<DataMatch>> onTable,
        IProgress<DataSearchProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.Text)) throw new ArgumentException("Nothing to search for.", nameof(options));
        var tables = Candidates(session.Schema, options);
        var maxParallel = Math.Clamp(options.MaxParallel, 1, 4);
        var rows = Math.Clamp(options.MaxRowsPerTable, 1, 1000);
        var gate = new object();
        var timedOut = new List<TableRef>();
        var failures = new List<DataSearchFailure>();
        var done = 0;
        var searched = 0;
        var total = 0;

        progress?.Report(new DataSearchProgress(0, tables.Count, null));
        using var limiter = new SemaphoreSlim(maxParallel);

        async Task SearchTableAsync(TableInfo table)
        {
            try
            {
                await limiter.WaitAsync(ct);
            }
            catch (OperationCanceledException) { return; }

            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (options.TableBudget is { } limit) budget.CancelAfter(limit);
                try
                {
                    var found = await Task.Run(() => SearchTableCoreAsync(session, table, options, rows, budget.Token), CancellationToken.None);
                    lock (gate)
                    {
                        searched++;
                        total += found.Count;
                        if (found.Count > 0) onTable(table.Ref, found);
                    }
                }
                catch (Exception) when (ct.IsCancellationRequested) { return; }   // the user stopped the search
                catch (OperationCanceledException)
                {
                    lock (gate) timedOut.Add(table.Ref);   // only the table's own budget ran out
                }
                catch (Exception e) when (e is DbException or InvalidOperationException or IOException)
                {
                    lock (gate) failures.Add(new DataSearchFailure(table.Ref, e.Message));
                }

                var n = Interlocked.Increment(ref done);
                progress?.Report(new DataSearchProgress(n, tables.Count, table.Ref));
            }
            finally
            {
                limiter.Release();
            }
        }

        await Task.WhenAll(tables.Select(SearchTableAsync));
        return new DataSearchSummary(tables.Count, searched, total, ct.IsCancellationRequested, timedOut, failures);
    }

    private static async Task<List<DataMatch>> SearchTableCoreAsync(
        Session session, TableInfo table, DataSearchOptions options, int maxRows, CancellationToken ct)
    {
        var driver = session.Driver;
        var searchable = SearchableColumns(table, options.Text);
        var readable = table.Columns.Where(c => KindOf(c) is not null).OrderBy(c => c.Ordinal).ToList();
        // Preview: the key first, then the leading columns, up to six; the matching column is added per hit.
        var preview = readable.Where(c => c.IsPrimaryKey).Concat(readable.Where(c => !c.IsPrimaryKey)).Take(MaxPreviewColumns).ToList();
        var selected = preview.Concat(readable.Except(preview)).ToList();   // all readable columns: a hit may be in any of them

        var select = new StringBuilder("SELECT ");
        select.Append(string.Join(", ", selected.Select(c => driver.Quote(c.Name))));
        var tests = searchable.Select(c => Test(driver, c, options.Mode)).ToList();
        for (var i = 0; i < tests.Count; i++) select.Append($", CASE WHEN {tests[i]} THEN 1 ELSE 0 END");
        select.Append($" FROM {driver.QuoteTable(table.Ref)} WHERE ").Append(string.Join(" OR ", tests));
        var orderBy = string.Join(", ", table.Columns.Where(c => c.IsPrimaryKey).OrderBy(c => c.Ordinal).Select(c => driver.Quote(c.Name)));
        var sql = driver.PageSql(select.ToString(), orderBy, 0, maxRows);

        await using var connection = await session.OpenConnectionAsync(ct);
        var tx = await TableDataLoader.BeginReadOnlyContextAsync(connection, driver.Kind, ct);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            CommandTimeouts.Unbounded(cmd);
            var p = cmd.CreateParameter();
            p.ParameterName = driver.ParamPrefix + "p";
            p.DbType = DbType.String;
            p.Value = Pattern(driver, options);
            cmd.Parameters.Add(p);

            var result = new List<DataMatch>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var cells = new string[selected.Count];
                for (var i = 0; i < cells.Length; i++) cells[i] = FormatCell(reader.IsDBNull(i) ? null : reader.GetValue(i));
                for (var i = 0; i < searchable.Count; i++)
                {
                    if (Convert.ToInt64(reader.GetValue(selected.Count + i), CultureInfo.InvariantCulture) == 0) continue;
                    var column = searchable[i];
                    var index = selected.IndexOf(column);
                    var shown = preview.Select(c => new DataCell(c.Name, Truncate(cells[selected.IndexOf(c)], MaxCellText), c == column)).ToList();
                    if (!preview.Contains(column)) shown.Add(new DataCell(column.Name, Truncate(cells[index], MaxCellText), true));
                    result.Add(new DataMatch(table.Ref, column.Name, Truncate(cells[index], MaxValueText), shown));
                }
            }
            return result;
        }
        finally
        {
            if (tx is not null)
            {
                try { await tx.RollbackAsync(CancellationToken.None); }
                catch (Exception) { /* nothing was written; the connection is closed next */ }
                await tx.DisposeAsync();
            }
        }
    }

    private static string Test(IDbDriver driver, ColumnInfo column, DataMatchMode mode)
    {
        var cast = driver.CastToText(driver.Quote(column.Name));
        return mode == DataMatchMode.Exact
            ? $"{cast} = {driver.ParamPrefix}p"
            : $"{cast} LIKE {driver.ParamPrefix}p ESCAPE '\\'";
    }

    private static string Pattern(IDbDriver driver, DataSearchOptions o) => o.Mode switch
    {
        DataMatchMode.Exact => o.Text,
        DataMatchMode.StartsWith => driver.EscapeLike(o.Text) + "%",
        _ => "%" + driver.EscapeLike(o.Text) + "%",
    };

    private static string FormatCell(object? value)
    {
        var text = value switch
        {
            null => "NULL",
            byte[] => "(binary)",
            DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        return Truncate(text.ReplaceLineEndings(" "), MaxValueText);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
