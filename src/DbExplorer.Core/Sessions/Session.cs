using System.Data.Common;
using DbExplorer.Core.Config;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Sessions;

/// <summary>
/// An opened connection target: its config, driver and loaded schema. It holds no live connection; every
/// operation gets its own pooled connection from <see cref="OpenConnectionAsync"/>. Disposing (done by
/// <see cref="SessionManager.Close"/>) closes the pooled connections and makes further opens fail.
/// </summary>
public class Session : IDisposable
{
    private readonly ISecretProtector? _protector;
    private volatile bool _disposed;

    /// <param name="protector">Used to decrypt <c>Config.ProtectedPassword</c> on each open, so no plain password is kept in memory.</param>
    public Session(ConnectionConfig config, IDbDriver driver, IReadOnlyList<TableInfo> schema, ISecretProtector? protector = null)
    {
        Config = config;
        Driver = driver;
        Schema = schema;
        _protector = protector;
    }

    /// <summary>Current config. Replaced (same Id) when cosmetic settings such as the read-only flag change.</summary>
    public ConnectionConfig Config { get; private set; }

    public IDbDriver Driver { get; }

    /// <summary>Last loaded schema; replaced as a whole by <see cref="SessionManager.RefreshSchemaAsync"/>.</summary>
    public IReadOnlyList<TableInfo> Schema { get; private set; }

    /// <summary>Opens a new connection; the caller disposes it.</summary>
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void UpdateConfig(ConnectionConfig config)
    {
        if (config.Id != Config.Id) throw new ArgumentException("Config id must not change.", nameof(config));
        var readOnlyChanged = config.ReadOnly != Config.ReadOnly;
        Config = config;

        // A dedicated script connection is opened for one read-only setting (SQLite opens read-only files in another mode):
        // the next run of each uses a new one.
        if (readOnlyChanged)
            foreach (var script in DedicatedConnections()) script.Invalidate();
    }

    internal void ReplaceSchema(IReadOnlyList<TableInfo> schema) => Schema = schema;

    // ---- dedicated script connections (see ScriptConnection) ----

    private readonly object _dedicatedGate = new();
    private readonly List<Scripting.ScriptConnection> _dedicated = [];

    internal void Register(Scripting.ScriptConnection script)
    {
        lock (_dedicatedGate) _dedicated.Add(script);
    }

    internal void Unregister(Scripting.ScriptConnection script)
    {
        lock (_dedicatedGate) _dedicated.Remove(script);
    }

    private List<Scripting.ScriptConnection> DedicatedConnections()
    {
        lock (_dedicatedGate) return [.. _dedicated];
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Held connections first: closing them returns them to the pool, which is cleared right after.
        foreach (var script in DedicatedConnections()) script.Dispose();
        try
        {
            using var connection = Build();
            ConnectionPools.Clear(connection);
        }
        catch (Exception)
        {
            // Releasing pooled connections is best effort (e.g. the password cannot be decrypted any more).
        }
        finally
        {
            _disposed = true;
        }
    }

    private DbConnection CreateConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Build();
    }

    private DbConnection Build()
    {
        var password = string.IsNullOrEmpty(Config.ProtectedPassword) || _protector is null
            ? null
            : _protector.Unprotect(Config.ProtectedPassword);
        return Driver.CreateConnection(Config, password);
    }
}

