using System.Data.Common;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;

namespace DbExplorer.Core.Querying;

/// <summary>The raw WHERE text typed by the user is not a single read-only condition.</summary>
public sealed class RawWhereException(string message) : Exception(message);

/// <param name="ColumnNames">Names as reported by the reader, in result order.</param>
/// <param name="HasMore">True when the query returned more than the requested page size (a next page exists).</param>
public sealed record TablePage(
    IReadOnlyList<string> ColumnNames, IReadOnlyList<object?[]> Rows, bool HasMore, TimeSpan Duration);

/// <summary>
/// Executes the table-view SELECT / COUNT built by <see cref="TableQueryBuilder"/>.
/// Limit: full LOB values (blob/text/json) are materialised for every row of a page, so tables with huge blob columns can be slow or large.
/// This is the execution layer that
/// guards the one piece of unparameterised text, <see cref="TableQuery.RawWhere"/>:
/// <list type="number">
/// <item>It must be exactly one statement (<see cref="ScriptSplitter"/> with the connection's dialect: ';' and GO
/// lines separate statements). The statement text returned by the splitter is what is used, so a trailing ';' or
/// '-- comment' cannot break the generated query.</item>
/// <item>"SELECT 1 WHERE " + text must not look like a write (<see cref="ReadOnlyGuard"/>, dialect-aware). This runs on
/// every connection, not only read-only ones: T-SQL needs no ';' between statements, so
/// <c>1=1) DELETE FROM t WHERE (1=1</c> is one statement to the splitter and two to SQL Server. A table-data query
/// is a SELECT; a write word in its condition is never legitimate.</item>
/// <item>Defence in depth: the query runs in a read-only context (<see cref="BeginReadOnlyContextAsync"/>): SQLite
/// <c>PRAGMA query_only</c>, PostgreSQL <c>READ ONLY</c> transaction, SQL Server a transaction that is always rolled
/// back (no read-only mode exists; rolling back undoes any DML or DDL that slipped past the lexer).</item>
/// </list>
/// </summary>
public static class TableDataLoader
{
    /// <summary>
    /// Validates the raw WHERE text and returns the text to embed (null = no condition).
    /// Throws <see cref="RawWhereException"/> when it must not run.
    /// </summary>
    public static string? PrepareRawWhere(string? rawWhere, DbKind kind)
    {
        if (string.IsNullOrWhiteSpace(rawWhere)) return null;

        var statements = ScriptSplitter.Split(rawWhere, kind);
        if (statements.Count == 0) return null; // only comments
        if (statements.Count > 1)
            throw new RawWhereException("The WHERE box takes a single condition; statement separators (';' or GO) are not allowed.");

        var text = statements[0].Text;
        if (ReadOnlyGuard.IsWrite("SELECT 1 WHERE " + text, kind))
            throw new RawWhereException("The WHERE condition looks like it contains a write statement; only read conditions are allowed here.");
        return text;
    }

    /// <summary>Loads one page: <paramref name="pageSize"/> rows starting at <c>q.Offset</c>; <c>q.Limit</c> is ignored.</summary>
    public static Task<TablePage> LoadPageAsync(Session session, TableQuery q, int pageSize, CancellationToken ct)
    {
        var kind = session.Driver.Kind;
        q = q with { RawWhere = PrepareRawWhere(q.RawWhere, kind), Limit = pageSize + 1 };
        var command = TableQueryBuilder.Build(session.Driver, q);

        return Task.Run(async () =>
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            return await InReadOnlyContextAsync(session, async (connection, tx) =>
            {
                await using var cmd = CreateCommand(connection, tx, command);
                await using var reader = await cmd.ExecuteReaderAsync(ct);

                var names = new string[reader.FieldCount];
                for (var i = 0; i < names.Length; i++) names[i] = reader.GetName(i);

                var rows = new List<object?[]>(Math.Min(pageSize + 1, 256));
                while (rows.Count <= pageSize && await reader.ReadAsync(ct))
                {
                    var values = new object[names.Length];
                    reader.GetValues(values);
                    rows.Add(Array.ConvertAll(values, v => v is DBNull ? null : v));
                }

                var hasMore = rows.Count > pageSize;
                if (hasMore) rows.RemoveAt(rows.Count - 1);
                return new TablePage(names, rows, hasMore, started.Elapsed);
            }, ct);
        }, ct);
    }

    /// <summary>COUNT(*) of the rows matching the query's filters (offset, limit and sort are ignored).</summary>
    public static Task<long> CountAsync(Session session, TableQuery q, CancellationToken ct)
    {
        q = q with { RawWhere = PrepareRawWhere(q.RawWhere, session.Driver.Kind) };
        var command = TableQueryBuilder.BuildCount(session.Driver, q);

        return Task.Run(() => InReadOnlyContextAsync(session, async (connection, tx) =>
        {
            await using var cmd = CreateCommand(connection, tx, command);
            var scalar = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
        }, ct), ct);
    }

    /// <summary>
    /// Puts <paramref name="c"/> in a mode where it cannot persist writes. Returns the transaction to attach to
    /// every command (null for SQLite); the caller disposes it, which rolls it back.
    /// </summary>
    public static async Task<DbTransaction?> BeginReadOnlyContextAsync(DbConnection c, DbKind kind, CancellationToken ct)
    {
        switch (kind)
        {
            case DbKind.Sqlite:
                // Per-connection setting; SQLite connections are not pooled, so it dies with the connection.
                await using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA query_only = ON";
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                return null;

            case DbKind.Postgres:
            {
                var tx = await c.BeginTransactionAsync(ct);
                try
                {
                    await using var cmd = c.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "SET TRANSACTION READ ONLY";
                    await cmd.ExecuteNonQueryAsync(ct);
                    return tx;
                }
                catch
                {
                    await tx.DisposeAsync();
                    throw;
                }
            }

            default:
                // SQL Server: no read-only mode. The transaction is never committed, so it is rolled back on dispose.
                return await c.BeginTransactionAsync(ct);
        }
    }

    private static async Task<T> InReadOnlyContextAsync<T>(
        Session session, Func<DbConnection, DbTransaction?, Task<T>> body, CancellationToken ct)
    {
        await using var connection = await session.OpenConnectionAsync(ct);
        var tx = await BeginReadOnlyContextAsync(connection, session.Driver.Kind, ct);
        try
        {
            return await body(connection, tx);
        }
        finally
        {
            if (tx is not null)
            {
                try { await tx.RollbackAsync(CancellationToken.None); }
                catch (Exception) { /* nothing was committed; the connection is about to be closed */ }
                await tx.DisposeAsync();
            }
        }
    }

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? tx, SqlCommandSpec spec)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = spec.Sql;
        foreach (var p in spec.Params)
            cmd.Parameters.Add(ParameterTyping.Create(cmd, p));
        return cmd;
    }
}
