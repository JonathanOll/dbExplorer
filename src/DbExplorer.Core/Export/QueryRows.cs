using System.Data.Common;
using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;

namespace DbExplorer.Core.Export;

/// <summary>
/// "All rows" of an export: runs a read query again, with no row limit, and streams it into a writer. It runs on a new
/// connection in a read-only context, which is always rolled back.
/// </summary>
public static class QueryRows
{
    public const string NotSafeMessage = "The statement may write data, so it is not run again.";

    /// <summary>The statement of a script result can be run again: the read-only guard sees nothing but reads in it.</summary>
    public static bool CanRunAgain(string statement, DbKind kind) => !ReadOnlyGuard.IsWrite(statement, kind);

    /// <summary>The rows of result set number <paramref name="setIndex"/> (1-based) of a script statement.</summary>
    public static RowSource FromStatement(Session session, string statement, int setIndex = 1)
    {
        if (!CanRunAgain(statement, session.Driver.Kind)) throw new ExportException(NotSafeMessage);
        return FromCommand(session, new SqlCommandSpec(statement, []), setIndex);
    }

    /// <summary>Every row of a table query: its filters, WHERE and sort, without paging.</summary>
    public static RowSource FromTable(Session session, TableQuery query)
    {
        query = query with { RawWhere = TableDataLoader.PrepareRawWhere(query.RawWhere, session.Driver.Kind) };
        return FromCommand(session, TableQueryBuilder.BuildAll(session.Driver, query), 1);
    }

    private static RowSource FromCommand(Session session, SqlCommandSpec spec, int setIndex) => async (sink, ct) =>
    {
        await using var connection = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
        var tx = await TableDataLoader.BeginReadOnlyContextAsync(connection, session.Driver.Kind, ct).ConfigureAwait(false);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = spec.Sql;
            CommandTimeouts.Unbounded(cmd);
            foreach (var p in spec.Params) cmd.Parameters.Add(ParameterTyping.Create(cmd, p));
            await ReadAsync(cmd, sink, setIndex, ct).ConfigureAwait(false);
        }
        finally
        {
            if (tx is not null)
            {
                try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { /* nothing was committed; the connection is about to be closed */ }
                await tx.DisposeAsync().ConfigureAwait(false);
            }
        }
    };

    private static async Task ReadAsync(DbCommand cmd, RowWriter sink, int setIndex, CancellationToken ct)
    {
        var pump = new ScriptRunner.CancelPump(cmd);
        try
        {
            using var registration = ct.Register(pump.OnCancel);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            try
            {
                var found = 0;
                do
                {
                    if (reader.FieldCount == 0 || ++found < setIndex) continue;

                    var columns = new ExportColumn[reader.FieldCount];
                    for (var i = 0; i < columns.Length; i++) columns[i] = Column(reader, i);
                    sink.Begin(columns);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var values = new object[columns.Length];
                        reader.GetValues(values);
                        sink.Write(Array.ConvertAll(values, v => v is DBNull ? null : v));
                    }
                    return;
                } while (await reader.NextResultAsync(ct).ConfigureAwait(false));
                throw new ExportException("The statement no longer returns this result set.");
            }
            catch
            {
                // Closing a reader with unread rows reads them all (SQL Server): abort the command first.
                try { cmd.Cancel(); }
                catch (Exception) { /* best effort: the command may be finished already */ }
                throw;
            }
        }
        finally
        {
            pump.Stop();
        }
    }

    private static ExportColumn Column(DbDataReader reader, int i)
    {
        string? type = null;
        Type? clr = null;
        try
        {
            type = reader.GetDataTypeName(i);
            clr = reader.GetFieldType(i);
        }
        catch (Exception) { /* a type the provider cannot describe: the name is enough */ }
        return new ExportColumn(reader.GetName(i), type, clr);
    }
}
