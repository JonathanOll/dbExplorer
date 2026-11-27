using System.Net.Sockets;
using DbExplorer.Core.Config;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DbExplorer.Core.Tunnels;

/// <summary>A local port forward over SSH.NET, listening on 127.0.0.1 only. Secrets are decrypted for each connect.</summary>
internal sealed class SshTunnel(
    SshTunnelConfig config, string targetHost, int targetPort, ISecretProtector protector, HostKeyVerifier hostKeys) : ISshTunnel
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _state = new();
    private Link? _link;
    private uint _lastPort;
    private string? _forwardError;
    private bool _disposed;

    private sealed class Link(SshClient client) : IDisposable
    {
        public SshClient Client { get; } = client;
        public ForwardedPortLocal? Port { get; set; }
        public volatile bool Broken;

        public bool IsAlive => !Broken && Port is { IsStarted: true } && Client.IsConnected;

        public void Dispose()
        {
            try { Port?.Dispose(); }
            catch (Exception) { /* the connection is already gone */ }
            try { Client.Dispose(); }
            catch (Exception) { /* same */ }
        }
    }

    public bool IsAlive
    {
        get
        {
            lock (_state)
            {
                try { return _link?.IsAlive == true; }
                catch (ObjectDisposedException) { return false; }
            }
        }
    }

    public string? TakeForwardError() => Interlocked.Exchange(ref _forwardError, null);

    public async Task<int> EnsureOpenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_state) ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsAlive) return (int)_lastPort;
            Replace(null);
            var link = await ConnectAsync(ct).ConfigureAwait(false);
            try
            {
                Forward(link);
            }
            catch (Exception e)
            {
                link.Dispose();
                throw new ConnectionStageException(ConnectionStage.SshForward, e.Message, e);
            }

            lock (_state)
            {
                if (!_disposed)
                {
                    _link = link;
                    return (int)_lastPort;
                }
            }
            link.Dispose();
            throw new ObjectDisposedException(nameof(SshTunnel));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Link> ConnectAsync(CancellationToken ct)
    {
        var port = config.Port > 0 ? config.Port : SshTunnelConfig.DefaultPort;
        // Twice at most: a first handshake with an unknown server stops, the user is asked, the second one goes through.
        for (var asked = false; ; asked = true)
        {
            var client = CreateClient(port);
            HostKeyInfo? refused = null;
            Exception? storeError = null;
            client.HostKeyReceived += (_, e) =>
            {
                e.CanTrust = false;   // SSH.NET trusts by default: only a stored key goes through
                var key = new HostKeyInfo(config.Host, port, e.HostKeyName, "SHA256:" + e.FingerPrintSHA256);
                try
                {
                    if (hostKeys.IsTrusted(key)) e.CanTrust = true;
                    else refused = key;
                }
                catch (Exception ex)
                {
                    storeError = ex;
                }
            };

            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
                return new Link(client);
            }
            catch (Exception e)
            {
                client.Dispose();
                ct.ThrowIfCancellationRequested();
                if (storeError is not null)
                    throw new ConnectionStageException(ConnectionStage.SshHostKey, storeError.Message, storeError);
                if (refused is null)
                    throw new ConnectionStageException(
                        e is SshAuthenticationException ? ConnectionStage.SshSignIn : ConnectionStage.SshConnect,
                        $"{config.User}@{config.Host}:{port}: {e.Message}", e);
                if (asked)
                    throw new ConnectionStageException(ConnectionStage.SshHostKey, "the server presented another key on the second attempt.", e);
            }

            // The handshake is closed while the user reads the fingerprint: no server timeout runs against them.
            await hostKeys.ApproveAsync(refused, ct).ConfigureAwait(false);
        }
    }

    private SshClient CreateClient(int port)
    {
        try
        {
            var info = new ConnectionInfo(config.Host, port, config.User, AuthenticationMethods()) { Timeout = ConnectTimeout };
            return new SshClient(info) { KeepAliveInterval = KeepAlive };
        }
        catch (Exception e)
        {
            // Settings the library rejects before any network traffic: unreadable key file, wrong passphrase, empty user.
            throw new ConnectionStageException(ConnectionStage.SshSignIn,
                config.Auth == SshAuthKind.PrivateKey ? $"private key '{config.KeyFile}': {e.Message}" : e.Message, e);
        }
    }

    private AuthenticationMethod[] AuthenticationMethods()
    {
        if (config.Auth == SshAuthKind.PrivateKey)
        {
            var passphrase = Plain(config.ProtectedPassphrase);
            var path = Environment.ExpandEnvironmentVariables(config.KeyFile ?? "");
            var key = passphrase is null ? new PrivateKeyFile(path) : new PrivateKeyFile(path, passphrase);
            return [new PrivateKeyAuthenticationMethod(config.User, key)];
        }

        var password = Plain(config.ProtectedPassword) ?? "";
        // Many servers ask for the password through "keyboard-interactive" instead of "password".
        var interactive = new KeyboardInteractiveAuthenticationMethod(config.User);
        interactive.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
                if (!prompt.IsEchoed) prompt.Response = password;
        };
        return [new PasswordAuthenticationMethod(config.User, password), interactive];
    }

    private string? Plain(string? protectedValue) =>
        string.IsNullOrEmpty(protectedValue) ? null : protector.Unprotect(protectedValue);

    private void Forward(Link link)
    {
        link.Client.ErrorOccurred += (_, _) => link.Broken = true;
        // The same local port as before when it is still free, so connection strings (and their pools) stay valid.
        try
        {
            Start(link, _lastPort);
        }
        catch (SocketException) when (_lastPort != 0)
        {
            Start(link, 0);
        }
    }

    private void Start(Link link, uint localPort)
    {
        var port = new ForwardedPortLocal("127.0.0.1", localPort, targetHost, (uint)targetPort);
        port.Exception += (_, e) => _forwardError = $"{targetHost}:{targetPort}: {e.Exception.Message}";
        link.Client.AddForwardedPort(port);
        try
        {
            port.Start();
        }
        catch
        {
            link.Client.RemoveForwardedPort(port);
            port.Dispose();
            throw;
        }
        link.Port = port;
        _lastPort = port.BoundPort;
    }

    private void Replace(Link? next)
    {
        Link? old;
        lock (_state)
        {
            old = _link;
            _link = next;
        }
        // Closing a dead connection can wait on the network: never on the caller's thread (a tree command runs on the UI thread).
        if (old is not null) _ = Task.Run(old.Dispose);
    }

    public void Dispose()
    {
        lock (_state) _disposed = true;
        Replace(null);
    }
}
