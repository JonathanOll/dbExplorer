using DbExplorer.Core.Config;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Metadata;
using DbExplorer.Core.Model;
using DbExplorer.Core.Tunnels;

namespace DbExplorer.Core.Sessions;

/// <summary>
/// One <see cref="Session"/> per connection. Opening connects, loads the schema and writes the metadata cache.
/// Connecting always runs on the thread pool so a slow or unreachable host never blocks the caller's thread.
/// </summary>
/// <remarks>
/// A connect attempt is shared by all concurrent callers for the same connection settings and runs on its own
/// cancellation source: it is cancelled only when the last waiting caller gives up, when the connection is
/// closed/removed, or when it is superseded by an attempt with different settings. A superseded or closed
/// attempt never registers a session nor writes the metadata cache.
/// </remarks>
public class SessionManager(
    ConfigStore configStore, MetadataCache cache, ISecretProtector protector, Func<DbKind, IDbDriver>? driverFactory = null,
    IHostKeyPrompt? hostKeyPrompt = null, SshTunnelFactory? tunnelFactory = null)
{
    private readonly SshTunnelFactory _tunnels =
        tunnelFactory ?? SshTunnels.Factory(KnownHostsStore.NextTo(configStore.FilePath), protector, hostKeyPrompt);
    private readonly Func<DbKind, IDbDriver> _drivers = driverFactory ?? DriverFactory.For;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Session> _open = new();
    private readonly Dictionary<Guid, Attempt> _pending = new();

    private sealed class Attempt(ConnectionConfig config)
    {
        public ConnectionConfig Config { get; } = config;

        /// <summary>The config of the latest caller that joined (same target as <see cref="Config"/>); guarded by the gate.</summary>
        public ConnectionConfig Latest { get; set; } = config;
        public CancellationTokenSource Cts { get; } = new();
        public int Waiters { get; set; }
        public Task<Session> Task { get; set; } = null!;
    }

    /// <summary>The store the app persists its config with (exposed for consumers that only hold the manager).</summary>
    public ConfigStore ConfigStore { get; } = configStore;

    public IReadOnlyList<Session> Open
    {
        get { lock (_gate) return _open.Values.ToList(); }
    }

    public Session? Find(Guid connectionId)
    {
        lock (_gate) return _open.GetValueOrDefault(connectionId);
    }

    /// <summary>
    /// Returns the open session for <paramref name="c"/>, opening it first when needed. A session or attempt made
    /// with different connection settings (host, password, ...) is replaced, never reused; cosmetic differences
    /// (name, folder, read-only) are not a difference. A failed attempt leaves nothing behind, so the next call
    /// retries. Each (re)open loads a fresh schema and rewrites the metadata cache.
    /// </summary>
    public async Task<Session> GetOrOpenAsync(ConnectionConfig c, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Attempt? attempt;
        Session? stale = null;
        lock (_gate)
        {
            if (_open.TryGetValue(c.Id, out var existing))
            {
                if (SameTarget(existing.Config, c))
                {
                    // Cosmetic differences (name, folder, read-only) take effect now: the caller's config is the latest.
                    if (existing.Config != c) existing.UpdateConfig(c);
                    return existing;
                }
                _open.Remove(c.Id);
                stale = existing;
            }

            // A completed attempt still listed here is stale: a connect that failed fast can finish before its
            // removal continuation ran (it found nothing to remove). Joining it would rethrow the old failure forever.
            if (_pending.TryGetValue(c.Id, out attempt) && (!SameTarget(attempt.Config, c) || attempt.Cts.IsCancellationRequested || attempt.Task.IsCompleted))
            {
                attempt.Cts.Cancel();
                _pending.Remove(c.Id);
                attempt = null;
            }

            if (attempt is null)
            {
                attempt = StartAttempt(c);
                _pending[c.Id] = attempt;
            }
            else
            {
                attempt.Latest = c;   // joined a connect in flight: the session it produces carries the newest cosmetic settings
            }

            attempt.Waiters++;
        }

        stale?.Dispose();
        return await WaitAsync(attempt, ct).ConfigureAwait(false);
    }

    /// <summary>Closes the session (releasing its pooled connections) and abandons any pending connect.</summary>
    public void Close(Guid id)
    {
        Session? session;
        lock (_gate) session = Detach(id);
        session?.Dispose();
    }

    /// <summary>Closes the session and deletes its metadata cache (used when a connection is deleted).</summary>
    public void Remove(Guid id)
    {
        Session? session;
        lock (_gate)
        {
            session = Detach(id);
            cache.Delete(id);
        }

        session?.Dispose();
    }

    /// <summary>Reloads the schema of an open session and rewrites its metadata cache. No-op when not open.</summary>
    public async Task RefreshSchemaAsync(Guid id, CancellationToken ct)
    {
        var session = Find(id);
        if (session is null) return;
        var schema = await Task.Run(async () =>
        {
            await using var connection = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
            return await session.Driver.LoadSchemaAsync(connection, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        lock (_gate)
        {
            if (_open.GetValueOrDefault(id) != session) return; // closed meanwhile: do not resurrect the cache
            TrySaveCache(id, schema);
            session.ReplaceSchema(schema);
        }
    }

    /// <summary>
    /// Reads the procedures and functions of an open session (once, see <see cref="Session.LoadRoutinesAsync"/>) and writes
    /// them to the metadata cache, for the search across connections.
    /// </summary>
    public async Task<IReadOnlyList<RoutineInfo>> LoadRoutinesAsync(Session session, CancellationToken ct)
    {
        var routines = await session.LoadRoutinesAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (_open.GetValueOrDefault(session.Config.Id) != session) return routines;   // closed meanwhile
            try { cache.SaveRoutines(session.Config.Id, routines); }
            catch (Exception) { /* like the table cache: only the search across connections misses them */ }
        }

        return routines;
    }

    /// <summary>Opens a throw-away direct connection with the given plain password to validate the settings.</summary>
    public static Task TestConnectionAsync(ConnectionConfig config, string? plainPassword, CancellationToken ct) =>
        TestAsync(() => new ConnectionLink(DriverFactory.For(config.Kind), null), config, plainPassword, ct);

    /// <summary>
    /// Opens a throw-away connection the way a session would (SSH tunnel, host key check, authentication). A failure is a
    /// <see cref="ConnectionStageException"/> that says which step failed.
    /// </summary>
    public Task TestAsync(ConnectionConfig config, string? plainPassword, CancellationToken ct) =>
        TestAsync(() => ConnectionLink.For(config, _drivers(config.Kind), _tunnels), config, plainPassword, ct);

    private static Task TestAsync(Func<ConnectionLink> createLink, ConnectionConfig config, string? plainPassword, CancellationToken ct) =>
        Task.Run(async () =>
        {
            ConnectionLink? link = null;
            try
            {
                link = createLink();
                await using var connection = await link.OpenAsync(config, plainPassword, ct).ConfigureAwait(false);
                ConnectionPools.Clear(connection);
            }
            catch (Exception e) when (e is not (OperationCanceledException or ConnectionStageException))
            {
                // A failed open leaves an entry in the provider's pool: drop it with the tunnel.
                try
                {
                    using var failed = link?.Build(config, plainPassword);
                    if (failed is not null) ConnectionPools.Clear(failed);
                }
                catch (Exception) { /* the settings are refused outright: no pool exists */ }
                ct.ThrowIfCancellationRequested();
                throw ConnectionStageException.Database(e);
            }
            finally
            {
                link?.Dispose();
            }
        }, ct);

    /// <summary>
    /// True when both configs reach the same database (everything but the name, folder, read-only flag and environment matches,
    /// the password included): a session of one can serve the other.
    /// </summary>
    public static bool SameTarget(ConnectionConfig a, ConnectionConfig b) =>
        a with { Name = b.Name, Folder = b.Folder, ReadOnly = b.ReadOnly, Environment = b.Environment } == b;

    /// <summary>
    /// As <see cref="SameTarget"/> but the password is not compared: the same server, database and login, whatever credential
    /// is stored now. Used to decide that rows loaded from one connection may be written to another.
    /// </summary>
    public static bool SameDatabase(ConnectionConfig a, ConnectionConfig b) =>
        SameTarget(a with { ProtectedPassword = b.ProtectedPassword, Ssh = WithSecretsOf(a.Ssh, b.Ssh) }, b);

    private static SshTunnelConfig? WithSecretsOf(SshTunnelConfig? a, SshTunnelConfig? b) =>
        a is null || b is null ? a : a with { ProtectedPassword = b.ProtectedPassword, ProtectedPassphrase = b.ProtectedPassphrase };

    private void TrySaveCache(Guid id, IReadOnlyList<TableInfo> schema)
    {
        try { cache.Save(id, schema); }
        catch (Exception) { /* disk full, locked or read-only folder: quick-open across connections just lacks this one */ }
    }

    /// <summary>Removes the open session and cancels the pending attempt of a connection. Call under the lock.</summary>
    private Session? Detach(Guid id)
    {
        if (_pending.Remove(id, out var attempt)) attempt.Cts.Cancel();
        return _open.Remove(id, out var session) ? session : null;
    }

    private Attempt StartAttempt(ConnectionConfig c)
    {
        var attempt = new Attempt(c);
        attempt.Task = Task.Run(() => OpenCoreAsync(attempt), attempt.Cts.Token);
        _ = attempt.Task.ContinueWith(t =>
            {
                _ = t.Exception; // observe: an abandoned attempt must not raise an unobserved task exception
                lock (_gate)
                    if (_pending.TryGetValue(c.Id, out var current) && current == attempt) _pending.Remove(c.Id);
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return attempt;
    }

    private async Task<Session> WaitAsync(Attempt attempt, CancellationToken ct)
    {
        try
        {
            return await attempt.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                if (--attempt.Waiters == 0 && !attempt.Task.IsCompleted) attempt.Cts.Cancel();
        }
    }

    private async Task<Session> OpenCoreAsync(Attempt attempt)
    {
        var c = attempt.Config;
        var ct = attempt.Cts.Token;
        var password = string.IsNullOrEmpty(c.ProtectedPassword) || !AuthModes.UsesPassword(c.Auth)
            ? null
            : protector.Unprotect(c.ProtectedPassword);
        var driver = _drivers(c.Kind);
        var link = ConnectionLink.For(c, driver, _tunnels);
        var owned = false;
        try
        {
            await using var connection = await link.OpenAsync(c, password, ct).ConfigureAwait(false);
            var schema = await driver.LoadSchemaAsync(connection, ct).ConfigureAwait(false);

            lock (_gate)
            {
                if (ct.IsCancellationRequested || !_pending.TryGetValue(c.Id, out var current) || current != attempt)
                {
                    ConnectionPools.Clear(connection);
                    throw new OperationCanceledException("The connection was closed or changed while it was opening.");
                }

                TrySaveCache(c.Id, schema);   // the cache only feeds the search: a write failure must not fail the connect
                var session = new Session(attempt.Latest, link, schema, protector);
                owned = true;
                _open[c.Id] = session;
                return session;
            }
        }
        finally
        {
            if (!owned) link.Dispose();
        }
    }
}
