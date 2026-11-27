using DbExplorer.Core.Config;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Tunnels;

/// <summary>A local port forwarded to a server behind an SSH server. Disposing closes it.</summary>
public interface ISshTunnel : IDisposable
{
    /// <summary>
    /// Opens the tunnel, or reopens it when it dropped, and returns the local port (on 127.0.0.1). Failures are
    /// <see cref="Sessions.ConnectionStageException"/>.
    /// </summary>
    Task<int> EnsureOpenAsync(CancellationToken ct);

    bool IsAlive { get; }

    /// <summary>Why the SSH server last failed to forward a connection, once; null when it did not.</summary>
    string? TakeForwardError();
}

/// <summary>Creates a closed tunnel to <paramref name="targetHost"/>:<paramref name="targetPort"/> (as seen from the SSH server).</summary>
public delegate ISshTunnel SshTunnelFactory(SshTunnelConfig ssh, string targetHost, int targetPort);

/// <summary>The real tunnels (SSH.NET, loaded only when the first one is opened).</summary>
public static class SshTunnels
{
    public static SshTunnelFactory Factory(KnownHostsStore knownHosts, ISecretProtector protector, IHostKeyPrompt? prompt)
    {
        // One verifier for all tunnels: several connections to a new server ask the question once.
        var verifier = new HostKeyVerifier(knownHosts, prompt);
        return (ssh, host, port) => new SshTunnel(ssh, host, port, protector, verifier);
    }
}
