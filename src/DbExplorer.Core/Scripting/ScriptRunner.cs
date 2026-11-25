using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;

namespace DbExplorer.Core.Scripting;

public sealed record ResultSet(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, bool Truncated);

/// <param name="Sets">Result sets returned by the statement; empty for statements that return no columns.</param>
/// <param name="RowsAffected">Null when the provider reports none (SELECT, DDL).</param>
/// <param name="Error">Null on success; the failure message otherwise.</param>
/// <param name="Messages">What the server said while this statement ran (see <see cref="ScriptOptions.Statistics"/>); null when
/// messages were not captured.</param>
public sealed record StatementResult(
    ScriptStatement Statement,
    IReadOnlyList<ResultSet> Sets,
    int? RowsAffected,
    TimeSpan Duration,
    string? Error,
    IReadOnlyList<ServerMessage>? Messages = null);

/// <param name="Kind">Database kind of the connection, when known; lets the read-only guard lex like that database.
/// Null makes the guard use its most conservative, kind-agnostic check.</param>
/// <param name="Statistics">SQL Server only: the run sets <c>STATISTICS IO / TIME</c> on the connection around the statements and
/// captures the server's messages into <see cref="StatementResult.Messages"/>. Ignored for other kinds by
/// <see cref="ScriptConnection"/> and the session overload of <see cref="ScriptRunner.RunAsync(Session, IReadOnlyList{ScriptStatement}, ScriptOptions, CancellationToken)"/>.
/// The connection overload trusts it: the caller applies it only to SQL Server.</param>
public sealed record ScriptOptions(int MaxRows = 1000, bool ReadOnly = false, DbKind? Kind = null, bool Statistics = false);

public static class ScriptRunner
{
    /// <summary>
    /// Runs the statements in order on an open connection. A failing or refused statement yields a result with
    /// <see cref="StatementResult.Error"/> set and ends the script. Cancelling cancels the running command and ends
    /// the enumeration quietly: no result is yielded for the interrupted statement and nothing is thrown.
    /// </summary>
    public static async IAsyncEnumerable<StatementResult> RunAsync(
        DbConnection c,
        IReadOnlyList<ScriptStatement> stmts,
        ScriptOptions o,
        [EnumeratorCancellation] CancellationToken ct,
        DbTransaction? transaction = null)
    {
        // Only while the user's statements run: what the statistics settings themselves print never lands on a statement.
        using var capture = o.Statistics ? ServerMessageCapture.Start(c) : null;
        foreach (var stmt in stmts)
        {
            if (ct.IsCancellationRequested) yield break;

            capture?.Drain();
            var result = o.ReadOnly && IsRefused(stmt, o)
                ? new StatementResult(stmt, [], null, TimeSpan.Zero, "Refused: this connection is read-only, write statements are not allowed.")
                : await ExecuteAsync(c, transaction, stmt, o, ct);

            if (result is null) yield break;   // cancelled
            if (capture is not null) result = result with { Messages = MergeMessages(capture.Drain(), result.Messages) };
            yield return result;
            if (result.Error is not null) yield break;
        }
    }

    /// <summary>
    /// Opens a connection of the session for the duration of the enumeration and runs the statements on it. When
    /// <see cref="ScriptOptions.ReadOnly"/> is set the connection is also put in a read-only context
    /// (<see cref="Querying.TableDataLoader.BeginReadOnlyContextAsync"/>) as defence in depth behind the lexical guard.
    /// Throws what opening the connection throws (<see cref="ObjectDisposedException"/> for a closed session);
    /// cancelling ends the enumeration quietly or throws <see cref="OperationCanceledException"/> while connecting.
    /// </summary>
    public static async IAsyncEnumerable<StatementResult> RunAsync(
        Session session,
        IReadOnlyList<ScriptStatement> stmts,
        ScriptOptions o,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
        var statistics = o.Statistics && session.Driver.Kind == DbKind.SqlServer;
        o = o with { Statistics = statistics };
        // Before the read-only transaction (a command needs cmd.Transaction while one is pending; SET options outlive it anyway).
        if (statistics) await StatisticsSession.EnableAsync(connection, ct).ConfigureAwait(false);
        var tx = o.ReadOnly
            ? await Querying.TableDataLoader.BeginReadOnlyContextAsync(connection, session.Driver.Kind, ct).ConfigureAwait(false)
            : null;
        try
        {
            await foreach (var result in RunAsync(connection, stmts, o, ct, tx).ConfigureAwait(false))
                yield return result;
        }
        finally
        {
            if (tx is not null)
            {
                try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { /* nothing was committed; the connection is about to be closed */ }
                await tx.DisposeAsync().ConfigureAwait(false);
            }
            // The connection is closed right after: the provider's pool reset undoes the options even when this fails.
            if (statistics) await StatisticsSession.DisableAsync(connection).ConfigureAwait(false);
        }
    }

    private static bool IsRefused(ScriptStatement stmt, ScriptOptions o) =>
        o.Kind is { } kind ? ReadOnlyGuard.IsWrite(stmt.Text, kind) : ReadOnlyGuard.IsWrite(stmt.Text);

    /// <summary>Runs one statement. Returns null when it was interrupted by cancellation.</summary>
    private static async Task<StatementResult?> ExecuteAsync(
        DbConnection c, DbTransaction? transaction, ScriptStatement stmt, ScriptOptions o, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        CancelPump? pump = null;
        try
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = stmt.Text;
            Querying.CommandTimeouts.Unbounded(cmd);   // the statement ends or the user cancels it
            pump = new CancelPump(cmd);
            using var cancelRegistration = ct.Register(pump.OnCancel);

            var sets = new List<ResultSet>();
            int affected;
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                do
                {
                    ct.ThrowIfCancellationRequested();
                    if (reader.FieldCount == 0) continue;

                    var set = await ReadSetAsync(reader, o.MaxRows, ct);
                    sets.Add(set);
                    if (set.Truncated)
                    {
                        // Do not drain the remaining rows (possibly millions): abort the command instead.
                        TryCancel(cmd);
                        break;
                    }
                } while (await reader.NextResultAsync(ct));

                await reader.CloseAsync();
                affected = reader.RecordsAffected;
            }

            return new StatementResult(stmt, sets, affected >= 0 ? affected : null, watch.Elapsed, null);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            // SqlClient folds the info messages still pending when an error arrives into the exception (and Message joins them
            // all): with statistics on they go back to the messages and the error text keeps only the real errors.
            if (o.Statistics && ServerMessageCapture.SplitSqlException(ex) is { } split)
                return new StatementResult(stmt, [], null, watch.Elapsed, split.Error, split.Messages);
            return new StatementResult(stmt, [], null, watch.Elapsed, ex.Message);
        }
        finally
        {
            pump?.Stop();   // after the command is disposed: no interrupt may reach the statements that follow on this connection
        }
    }

    private static IReadOnlyList<ServerMessage> MergeMessages(IReadOnlyList<ServerMessage> raised, IReadOnlyList<ServerMessage>? fromException)
    {
        if (fromException is not { Count: > 0 }) return raised;
        var known = raised.Select(m => m.Text).ToHashSet(StringComparer.Ordinal);
        return [.. raised, .. fromException.Where(m => !known.Contains(m.Text))];   // an event and the exception never say a line twice
    }

    private static async Task<ResultSet> ReadSetAsync(DbDataReader reader, int maxRows, CancellationToken ct)
    {
        var columns = new string[reader.FieldCount];
        for (var i = 0; i < columns.Length; i++) columns[i] = reader.GetName(i);

        var rows = new List<object?[]>();
        var truncated = false;
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count >= maxRows)
            {
                truncated = true;
                break;
            }
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(Array.ConvertAll(values, v => v is DBNull ? null : v));
        }
        return new ResultSet(columns, rows, truncated);
    }

    private static void TryCancel(DbCommand cmd)
    {
        try { cmd.Cancel(); }
        catch (Exception) { /* cancellation is best effort; the command may already be finished or disposed */ }

        InterruptSqlite(cmd);
    }

    // Microsoft.Data.Sqlite's Cancel() cannot stop a statement that is already running inside sqlite3_step (a
    // slow query that returns no row yet would keep going for ever); interrupting the connection does.
    private static void InterruptSqlite(DbCommand cmd)
    {
        try
        {
            if (cmd.Connection is Microsoft.Data.Sqlite.SqliteConnection { Handle: { IsInvalid: false } handle })
                SQLitePCL.raw.sqlite3_interrupt(handle);
        }
        catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// Cancels one command. sqlite3_interrupt does nothing when no statement is running yet, so a cancel landing between "the
    /// command is prepared" and "sqlite3_step starts" would be lost and an endless statement would then run for ever (a script
    /// tab closed right after a run was started leaked a busy thread holding the database file). For SQLite the interrupt is
    /// therefore repeated until <see cref="Stop"/> says the command is done. Interrupts and <see cref="Stop"/> are serialised, so
    /// none can reach a later statement of the same connection.
    /// </summary>
    private sealed class CancelPump(DbCommand cmd)
    {
        private readonly object _gate = new();
        private bool _stopped;

        public void OnCancel()
        {
            TryCancel(cmd);
            if (cmd.Connection is not Microsoft.Data.Sqlite.SqliteConnection) return;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(5).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_stopped) return;
                        InterruptSqlite(cmd);
                    }
                }
            });
        }

        public void Stop()
        {
            lock (_gate) _stopped = true;
        }
    }
}
