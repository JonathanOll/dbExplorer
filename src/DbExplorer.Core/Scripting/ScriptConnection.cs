using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using DbExplorer.Core.Sessions;

namespace DbExplorer.Core.Scripting;

/// <summary>
/// The one dedicated connection of a script tab. It is opened by the first run (not when the tab is created) and kept for
/// the later runs, so a manual transaction (<c>BEGIN TRAN</c> ... <c>ROLLBACK</c>), temp tables, variables and SET options
/// survive from one Ctrl+Enter / F5 to the next. It is released when the tab closes (<see cref="Dispose"/>, which rolls an
/// open transaction back), when the session is closed or disposed (<see cref="Session.Dispose"/>), when the connection
/// settings change (<see cref="Session.UpdateConfig"/> with another read-only flag) and when it is found broken: the next
/// run then opens a new one and <see cref="Reopened"/> says that the session state was reset.
/// </summary>
/// <remarks>
/// The connection is taken from the provider's pool and held for the life of the tab. Runs are sequential (a second run
/// while one is active throws). On read-only connections every run still gets its own read-only context, torn down when
/// the run ends, exactly as with a per-run connection; the connection itself is private to the tab so nothing leaks.
/// After a cancellation the connection may be unusable (SQL Server, PostgreSQL): it is probed before the next run and
/// replaced when the probe fails.
/// </remarks>
public sealed class ScriptConnection : IDisposable
{
    private readonly object _gate = new();
    private DbConnection? _connection;
    private bool _everOpened;
    private bool _running;
    private volatile bool _disposed;
    private volatile bool _invalidated;
    private bool _suspect;

    public ScriptConnection(Session session)
    {
        Session = session;
        session.Register(this);
    }

    public Session Session { get; }

    /// <summary>True when the last run had to open a replacement for a connection that was lost, invalidated or unusable.</summary>
    public bool Reopened { get; private set; }

    public const string ResetMessage =
        "The connection of this tab was reset after an aborted statement: any open transaction was aborted and rolled back " +
        "(a later COMMIT will not commit it). Temp tables and variables are gone.";

    /// <summary>
    /// True when the last run ended with the connection found unusable after a cancelled or truncated statement (a result cut
    /// at the row limit cancels its command): it was dropped, so an open transaction is gone. Read once the run's enumeration
    /// has ended. The next run opens a new connection without a second warning.
    /// </summary>
    public bool ConnectionReset { get; private set; }

    /// <summary>True from the moment a run starts until it has ended (tests wait on it instead of sleeping).</summary>
    internal bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>The connection currently held, or null before the first run and after it was dropped.</summary>
    internal DbConnection? CurrentConnection => _connection;

    /// <summary>
    /// Best-effort: true when the held connection has a transaction left open by the user's script (so releasing the
    /// connection will roll it back). Never changes state and never runs while a script is active (it holds the same
    /// "running" guard as a run, so a concurrent <see cref="Dispose"/> waits for it to finish before dropping the connection).
    /// SQL Server reads <c>@@TRANCOUNT</c>. PostgreSQL sends two separate <c>SELECT now()</c> commands: inside a transaction
    /// both return the transaction's start time (equal), in autocommit each statement is its own transaction (different);
    /// <c>statement_timestamp()</c> cannot be used, it moves at Bind/Execute under the extended protocol. SQLite tries a
    /// <c>BEGIN</c> (refused inside a transaction) and rolls it straight back. The failure mode is a false negative: false when
    /// there is no usable connection, the probe fails (e.g. a PostgreSQL transaction already aborted) or the answer is unclear.
    /// The answer only feeds a status message.
    /// </summary>
    public async Task<bool> HasOpenTransactionAsync()
    {
        DbConnection? connection;
        lock (_gate)
        {
            if (_disposed || _running) return false;
            if (_connection is not { State: ConnectionState.Open } held) return false;
            connection = held;
            _running = true;   // a run now throws "already running" (the tab refuses runs while it switches) and Dispose defers the drop
        }

        try
        {
            return await ProbeTransactionAsync(connection).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                if (_disposed || _connection is { State: not ConnectionState.Open }) DropConnection();
            }
        }
    }

    private async Task<bool> ProbeTransactionAsync(DbConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = 5;
        switch (Session.Driver.Kind)
        {
            case Model.DbKind.SqlServer:
                cmd.CommandText = "SELECT @@TRANCOUNT";
                return Convert.ToInt32(await cmd.ExecuteScalarAsync().ConfigureAwait(false)) > 0;
            case Model.DbKind.Postgres:
            {
                cmd.CommandText = "SELECT now()";
                var first = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                var second = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                return first is not null and not DBNull && first.Equals(second);
            }
            default:
                cmd.CommandText = "BEGIN";
                try { await cmd.ExecuteNonQueryAsync().ConfigureAwait(false); }
                catch (Exception e) { return e.Message.Contains("within a transaction", StringComparison.OrdinalIgnoreCase); }
                cmd.CommandText = "ROLLBACK";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                return false;
        }
    }

    /// <summary>The connection settings changed: the next run uses a fresh connection. Safe to call from any thread.</summary>
    internal void Invalidate() => _invalidated = true;

    /// <summary>
    /// Runs the statements on the dedicated connection (see <see cref="ScriptRunner.RunAsync(DbConnection, IReadOnlyList{ScriptStatement}, ScriptOptions, CancellationToken, DbTransaction?)"/>).
    /// Throws <see cref="ObjectDisposedException"/> once closed (also when its session was closed) and whatever opening the
    /// connection throws; cancelling ends the enumeration quietly or throws while connecting.
    /// </summary>
    public async IAsyncEnumerable<StatementResult> RunAsync(
        IReadOnlyList<ScriptStatement> stmts, ScriptOptions o, [EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_running) throw new InvalidOperationException("A script is already running on this connection.");
            _running = true;
        }

        try
        {
            Reopened = false;
            ConnectionReset = false;
            var connection = await EnsureConnectionAsync(ct).ConfigureAwait(false);
            DbTransaction? tx = null;
            try
            {
                if (o.ReadOnly)
                    tx = await Querying.TableDataLoader.BeginReadOnlyContextAsync(connection, Session.Driver.Kind, ct).ConfigureAwait(false);
                await foreach (var result in ScriptRunner.RunAsync(connection, stmts, o, ct, tx).ConfigureAwait(false))
                {
                    if (result.Sets.Any(s => s.Truncated)) _suspect = true;   // the command was aborted before its end
                    yield return result;
                }
            }
            finally
            {
                // A cancelled statement or a truncated result set (the command is aborted) may leave the connection unusable.
                if (ct.IsCancellationRequested) _suspect = true;
                await TearDownReadOnlyContextAsync(connection, tx, o).ConfigureAwait(false);
                // Probe now, not at the next run: the abort (PostgreSQL: the cancel request fails a statement inside the user's
                // transaction, which then aborts it) must be reported by THIS run, while the user still knows what ran.
                if (_suspect) await CheckAfterAbortAsync(connection).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                if (_disposed || _connection is { State: not ConnectionState.Open }) DropConnection();
            }
        }
    }

    private async Task<DbConnection> EnsureConnectionAsync(CancellationToken ct)
    {
        if (_invalidated)
        {
            _invalidated = false;
            DropConnection();
        }
        else if (_connection is { State: not ConnectionState.Open })
        {
            DropConnection();
        }
        else if (_connection is not null && _suspect && !await ProbeAsync(_connection).ConfigureAwait(false))
        {
            DropConnection();
        }
        _suspect = false;

        if (_connection is { } alive) return alive;

        var opened = await Session.OpenConnectionAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed)
            {
                opened.Dispose();
                throw new ObjectDisposedException(nameof(ScriptConnection));
            }
            Reopened = _everOpened;
            _everOpened = true;
            _connection = opened;
        }
        return opened;
    }

    private async Task CheckAfterAbortAsync(DbConnection connection)
    {
        _suspect = false;
        if (connection.State == ConnectionState.Open && await ProbeAsync(connection).ConfigureAwait(false)) return;
        if (_disposed) return;
        lock (_gate)
        {
            if (!ReferenceEquals(_connection, connection)) return;
            DropConnection();
            _everOpened = false;   // the loud message is this run's: the next run's new connection is not a second "reopened"
            ConnectionReset = true;
        }
    }

    private static async Task<bool> ProbeAsync(DbConnection connection)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.CommandTimeout = 5;
            await cmd.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task TearDownReadOnlyContextAsync(DbConnection connection, DbTransaction? tx, ScriptOptions o)
    {
        if (tx is not null)
        {
            try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { /* nothing was committed; an unusable connection is replaced by the next run */ }
            await tx.DisposeAsync().ConfigureAwait(false);
        }
        else if (o.ReadOnly && Session.Driver.Kind == Model.DbKind.Sqlite && connection.State == ConnectionState.Open)
        {
            // PRAGMA query_only is a property of the connection, which now outlives the run: undo it.
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "PRAGMA query_only = OFF";
                await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) { _suspect = true; }
        }
    }

    /// <summary>
    /// Releases the held connection. SQLite closes the file (an open transaction is rolled back with it). SQL Server and
    /// PostgreSQL connections go back to the provider's pool, where the reset that undoes a transaction may only happen when
    /// the physical connection is next reused (locks held until then): an explicit best-effort ROLLBACK is sent first, off the
    /// calling thread (this runs on the UI thread when a tab closes).
    /// </summary>
    private void DropConnection()
    {
        var connection = _connection;
        _connection = null;
        if (connection is null) return;

        var rollback = Session.Driver.Kind switch
        {
            Model.DbKind.SqlServer => "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION",
            Model.DbKind.Postgres => "ROLLBACK",
            _ => null,
        };
        if (rollback is null || connection.State != ConnectionState.Open)
        {
            DisposeQuietly(connection);
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = rollback;
                cmd.CommandTimeout = 5;
                cmd.ExecuteNonQuery();
            }
            catch (Exception) { /* best effort: the connection may be broken, the pool discards it then */ }
            finally { DisposeQuietly(connection); }
        });
    }

    private static void DisposeQuietly(DbConnection connection)
    {
        try { connection.Dispose(); }
        catch (Exception) { /* already broken */ }
    }

    /// <summary>
    /// Closes the connection (an open transaction is rolled back). A run in progress ends first: it releases the
    /// connection when it finishes.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_running) DropConnection();
        }
        Session.Unregister(this);
    }
}
