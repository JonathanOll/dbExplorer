using DbExplorer.Core.Sessions;

namespace DbExplorer.Core.Tunnels;

/// <summary>Asks the user whether to trust an SSH server seen for the first time.</summary>
public interface IHostKeyPrompt
{
    /// <summary>True only when the user explicitly accepted the fingerprint.</summary>
    Task<bool> ConfirmNewHostAsync(HostKeyInfo key, CancellationToken ct);
}

/// <summary>
/// Trust on first use: a stored key must match, a new one needs the user's approval, a changed one is always refused.
/// Without a prompt, unknown servers are refused.
/// </summary>
public sealed class HostKeyVerifier(KnownHostsStore store, IHostKeyPrompt? prompt)
{
    private readonly SemaphoreSlim _asking = new(1, 1);

    /// <summary>Whether the handshake may go on with this key (no question asked: called while the server waits).</summary>
    public bool IsTrusted(HostKeyInfo key) => store.Check(key) == HostKeyStatus.Trusted;

    /// <summary>
    /// After a handshake stopped on <paramref name="key"/>: returns when the key is now trusted (connect again), otherwise
    /// throws a <see cref="ConnectionStageException"/> that says why.
    /// </summary>
    public async Task ApproveAsync(HostKeyInfo key, CancellationToken ct)
    {
        await _asking.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            switch (store.Check(key))
            {
                case HostKeyStatus.Trusted:
                    return;
                case HostKeyStatus.Changed:
                    throw Refused(
                        $"the host key of {key.Host}:{key.Port} CHANGED. Expected {store.Find(key.Host, key.Port)!.Fingerprint}, " +
                        $"the server presented {key.Fingerprint}. The connection is refused: someone may be intercepting it. " +
                        $"If the server was really reinstalled, remove its entry from {store.FilePath} and connect again.");
            }

            if (prompt is null || !await prompt.ConfirmNewHostAsync(key, ct).ConfigureAwait(false))
                throw Refused($"the host key of {key.Host}:{key.Port} ({key.Fingerprint}) was not accepted.");
            ct.ThrowIfCancellationRequested();
            store.Add(key);
        }
        finally
        {
            _asking.Release();
        }
    }

    private static ConnectionStageException Refused(string detail) => new(ConnectionStage.SshHostKey, detail);
}
