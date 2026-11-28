using System.Data.Common;
using DbExplorer.Core.Config;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Plans;
using DbExplorer.Core.Querying;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;
using DbExplorer.Core.Tunnels;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Claude;

/// <summary>
/// The database work of the MCP server. Every connection is opened read-only where the engine has such a mode, every
/// statement runs in a read-only context that is rolled back, and nothing can ask the user anything: an unknown SSH host
/// key is refused and interactive sign-in is rejected before connecting.
/// </summary>
public sealed class ClaudeDatabase(ISecretProtector protector, KnownHostsStore knownHosts) : IDisposable
{
    // A value longer than this is refused by SQLite itself: randomblob(1e9) must not exhaust the memory.
    private const int SqliteMaxValueLength = 4_000_000;
    private static readonly TimeSpan SchemaLifetime = TimeSpan.FromSeconds(60);

    private sealed class Open(ConnectionConfig config, Session session)
    {
        public ConnectionConfig Config { get; } = config;
        public Session Session { get; } = session;
        public IReadOnlyList<TableInfo>? Schema { get; set; }
        public DateTime SchemaAt { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Open> _open = new();

    /// <summary>The tables and views, read again after a minute.</summary>
    public async Task<IReadOnlyList<TableInfo>> SchemaAsync(ConnectionConfig config, CancellationToken ct)
    {
        var open = For(config);
        if (open.Schema is { } known && DateTime.UtcNow - open.SchemaAt < SchemaLifetime) return known;
        var schema = await open.Session.ReadSchemaAsync(ct).ConfigureAwait(false);
        open.SchemaAt = DateTime.UtcNow;
        return open.Schema = schema;
    }

    public async Task<IReadOnlyList<RoutineInfo>> RoutinesAsync(ConnectionConfig config, CancellationToken ct)
    {
        var session = For(config).Session;
        await using var connection = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await session.Driver.LoadRoutinesAsync(connection, ct).ConfigureAwait(false);
    }

    public Task<string> ViewDefinitionAsync(ConnectionConfig config, TableRef view, CancellationToken ct) =>
        For(config).Session.LoadViewDefinitionAsync(view, ct);

    public Task<string> RoutineDefinitionAsync(ConnectionConfig config, RoutineInfo routine, CancellationToken ct) =>
        For(config).Session.LoadRoutineDefinitionAsync(routine, ct);

    /// <summary>
    /// Runs one statement that <see cref="ClaudeSqlPolicy"/> accepted (checked again here), or asks for its estimated
    /// plan, which runs nothing. Throws <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public Task<StatementResult> RunAsync(ConnectionConfig config, string sql, int maxRows, PlanMode plan, TimeSpan timeout,
        CancellationToken ct)
    {
        if (plan == PlanMode.Actual) throw new ArgumentException("An actual plan runs the statement.", nameof(plan));
        if (ClaudeSqlPolicy.Refuse(sql, config.Kind) is { } why) throw new ClaudeRefusedException(why);
        var statement = ScriptSplitter.Split(sql, config.Kind)[0];
        var session = For(config).Session;

        // Off the caller's thread: SQLite runs synchronously.
        return Task.Run(async () =>
        {
            var kind = config.Kind;
            await using var connection = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
            if (connection is SqliteConnection { Handle: { } handle })
                SQLitePCL.raw.sqlite3_limit(handle, SQLitePCL.raw.SQLITE_LIMIT_LENGTH, SqliteMaxValueLength);

            if (plan != PlanMode.None) await PlanCapture.EnableAsync(connection, kind, plan, ct).ConfigureAwait(false);
            DbTransaction? tx = null;
            try
            {
                tx = await TableDataLoader.BeginReadOnlyContextAsync(connection, kind, ct).ConfigureAwait(false);
                if (kind == DbKind.Postgres)
                {
                    // The server stops the statement even if the cancel request is lost; strings are read as the lexer reads them.
                    await using var settings = connection.CreateCommand();
                    settings.Transaction = tx;
                    settings.CommandText = $"SET LOCAL statement_timeout = {(long)timeout.TotalMilliseconds}; " +
                                           "SET LOCAL standard_conforming_strings = on";
                    await settings.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                var options = new ScriptOptions(maxRows, ReadOnly: true, kind, Plan: plan);
                await foreach (var result in ScriptRunner.RunAsync(connection, [statement], options, ct, tx).ConfigureAwait(false))
                    return result;
                throw new OperationCanceledException(ct);
            }
            finally
            {
                if (tx is not null)
                {
                    try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { /* nothing was committed; the connection is closed right after */ }
                    await tx.DisposeAsync().ConfigureAwait(false);
                }
                if (plan != PlanMode.None) await PlanCapture.DisableAsync(connection, kind, plan).ConfigureAwait(false);
            }
        }, ct);
    }

    // One session (and SSH tunnel) per connection, replaced when the stored settings change.
    private Open For(ConnectionConfig config)
    {
        if (ClaudePolicy.CannotConnectUnattended(config) is { } why) throw new ClaudeRefusedException(why);
        var readOnly = config with { ReadOnly = true };
        lock (_gate)
        {
            if (_open.TryGetValue(config.Id, out var open))
            {
                if (open.Config == readOnly) return open;
                open.Session.Dispose();
                _open.Remove(config.Id);
            }

            var driver = DriverFactory.For(config.Kind).WithSecrets(protector);
            // No prompt: a server whose key the user has not accepted in the app is refused.
            var link = ConnectionLink.For(readOnly, driver, SshTunnels.Factory(knownHosts, protector, null));
            return _open[config.Id] = new Open(readOnly, new Session(readOnly, link, [], protector));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var open in _open.Values) open.Session.Dispose();
            _open.Clear();
        }
    }
}

/// <summary>A call that is not allowed; the message is shown to Claude.</summary>
public sealed class ClaudeRefusedException(string message) : Exception(message);
