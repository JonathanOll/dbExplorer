using System.Data.Common;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Tunnels;

namespace DbExplorer.Core.Sessions;

/// <summary>
/// The one path every database connection is opened through: the driver, plus the SSH tunnel when the connection has one.
/// Owns the tunnel (opened on first use, reopened after a drop, closed with the link).
/// </summary>
internal sealed class ConnectionLink(IDbDriver driver, ISshTunnel? tunnel) : IDisposable
{
    // How long a failed open waits for the tunnel to say why: the forwarder reports just after it closes the local socket.
    private static readonly TimeSpan ForwardErrorDelay = TimeSpan.FromMilliseconds(150);

    private int _port;

    public IDbDriver Driver { get; } = driver;

    /// <summary>False only for a tunnel that was open and is not any more.</summary>
    public bool IsAlive => tunnel is null || _port == 0 || tunnel.IsAlive;

    public static ConnectionLink For(ConnectionConfig config, IDbDriver driver, SshTunnelFactory tunnels)
    {
        if (config.Ssh is null || config.Kind == DbKind.Sqlite) return new ConnectionLink(driver, null);
        var (host, port) = driver.NetworkTarget(config);
        return new ConnectionLink(driver, tunnels(config.Ssh, host, port));
    }

    /// <summary>Opens a new connection; the caller disposes it.</summary>
    public async Task<DbConnection> OpenAsync(ConnectionConfig config, string? plainPassword, CancellationToken ct)
    {
        if (tunnel is not null) await EnsureTunnelAsync(config, plainPassword, ct).ConfigureAwait(false);
        var connection = Build(config, plainPassword) ?? throw new InvalidOperationException("The SSH tunnel is not open.");
        try
        {
            tunnel?.TakeForwardError();   // an older failure must not explain this one
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch (Exception e)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            if (tunnel is not null && e is not OperationCanceledException)
            {
                await Task.Delay(ForwardErrorDelay, CancellationToken.None).ConfigureAwait(false);
                if (tunnel.TakeForwardError() is { } reason)
                    throw new ConnectionStageException(ConnectionStage.SshForward, reason, e);
            }
            throw;
        }
    }

    /// <summary>A connection that is not opened, or null when the config needs a tunnel that never opened.</summary>
    public DbConnection? Build(ConnectionConfig config, string? plainPassword)
    {
        if (config.Ssh is null || config.Kind == DbKind.Sqlite) return Driver.CreateConnection(config, plainPassword);
        // Never fall back to a direct connection: the user asked for the tunnel.
        if (tunnel is null) throw new InvalidOperationException("This connection needs an SSH tunnel, which was not set up.");
        return _port == 0 ? null : Driver.CreateTunneledConnection(config, plainPassword, _port);
    }

    private async Task EnsureTunnelAsync(ConnectionConfig config, string? plainPassword, CancellationToken ct)
    {
        var dropped = _port != 0 && !tunnel!.IsAlive;
        var port = await tunnel!.EnsureOpenAsync(ct).ConfigureAwait(false);
        var previous = Interlocked.Exchange(ref _port, port);
        if (previous == 0 || (!dropped && previous == port)) return;
        // The pooled connections went through the old tunnel: they are dead.
        using var stale = Driver.CreateTunneledConnection(config, plainPassword, previous);
        ConnectionPools.Clear(stale);
    }

    public void Dispose() => tunnel?.Dispose();
}
