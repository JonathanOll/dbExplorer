using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DbExplorer.Core.Claude;

/// <summary>What the MCP server asks of the running app.</summary>
public sealed record ClaudeAppRequest(string Op, Guid? ConnectionId = null, string? Title = null, string? Sql = null)
{
    public const string OpenScript = "open_script";
    public const string ActiveTab = "get_active_tab";
}

/// <summary>The script tab in front of the user.</summary>
/// <param name="LastError">The error of the tab's last run; null when there is none or it may not be shared.</param>
/// <param name="Note">Why something is left out.</param>
public sealed record ClaudeActiveTab(Guid ConnectionId, string ConnectionName, string Title, string Sql, string? LastError, string? Note);

/// <summary>The app's answer; <see cref="Message"/> says why when <see cref="Ok"/> is false.</summary>
public sealed record ClaudeAppReply(bool Ok, string? Message = null, ClaudeActiveTab? Tab = null);

/// <summary>The running app as the MCP server sees it.</summary>
public interface IClaudeApp
{
    Task<ClaudeAppReply> SendAsync(ClaudeAppRequest request, CancellationToken ct);
}

/// <summary>
/// The local channel between the two processes: a named pipe of the current Windows user (never reachable from the
/// network), one JSON line each way per connection. There is one pipe per data folder.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ClaudeBridge
{
    public const int MaxMessageBytes = 1_000_000;
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    public const string NotRunning =
        "dbExplorer is not answering: start it (with the Claude Code integration turned on) and try again.";

    public static string PipeName(string dataDirectory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var key = identity.User!.Value + "|" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)).ToUpperInvariant();
        return "dbExplorer.claude." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
    }

    /// <summary>
    /// Creates the server end: only this user may open it, remote logons are denied, and it fails when the name is taken
    /// (another instance of the app, or a squatter).
    /// </summary>
    internal static NamedPipeServerStream CreateServer(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.SetOwner(identity.User!);   // the client checks it; an elevated process would default to Administrators
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }

    /// <summary>
    /// Serves requests one at a time until disposed. Returns null when the pipe cannot be created (another instance of the
    /// app already listens).
    /// </summary>
    public static IDisposable? Listen(string pipeName, Func<ClaudeAppRequest, CancellationToken, Task<ClaudeAppReply>> handle)
    {
        NamedPipeServerStream server;
        try { server = CreateServer(pipeName); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var listener = new Listener(server);
        _ = Task.Run(() => listener.RunAsync(handle));
        return listener;
    }

    private sealed class Listener(NamedPipeServerStream server) : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();

        public async Task RunAsync(Func<ClaudeAppRequest, CancellationToken, Task<ClaudeAppReply>> handle)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    try { await ServeAsync(handle).ConfigureAwait(false); }
                    catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or InvalidDataException)
                    {
                        // A client that hangs, leaves or sends garbage only loses its own request.
                    }
                    if (server.IsConnected || !_stop.IsCancellationRequested) server.Disconnect();
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested) { }
            catch (Exception) { /* the pipe broke for good: the app goes on without it */ }
            finally { await server.DisposeAsync().ConfigureAwait(false); }
        }

        private async Task ServeAsync(Func<ClaudeAppRequest, CancellationToken, Task<ClaudeAppReply>> handle)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(IoTimeout);
            var request = JsonSerializer.Deserialize<ClaudeAppRequest>(await ReadLineAsync(server, timeout.Token).ConfigureAwait(false));
            ClaudeAppReply reply;
            if (request?.Op is null) reply = new ClaudeAppReply(false, "Unreadable request.");
            else
            {
                try { reply = await handle(request, timeout.Token).ConfigureAwait(false); }
                catch (Exception e) when (e is not OperationCanceledException) { reply = new ClaudeAppReply(false, "dbExplorer could not do it."); }
            }
            await WriteLineAsync(server, JsonSerializer.Serialize(reply), timeout.Token).ConfigureAwait(false);
            // The reply must be read before the pipe is disconnected.
            await server.ReadAsync(new byte[1], timeout.Token).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _stop.Cancel();
            server.Dispose();   // at once: a new listener may need the name right away
        }
    }

    /// <summary>The client end, used by the MCP server.</summary>
    public sealed class Client(string pipeName) : IClaudeApp
    {
        public async Task<ClaudeAppReply> SendAsync(ClaudeAppRequest request, CancellationToken ct)
        {
            try
            {
                // CurrentUserOnly: refuses a pipe another account created under the same name.
                await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    TokenImpersonationLevel.Identification);
                await pipe.ConnectAsync(ConnectTimeout, ct).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(IoTimeout);
                await WriteLineAsync(pipe, JsonSerializer.Serialize(request), timeout.Token).ConfigureAwait(false);
                var line = await ReadLineAsync(pipe, timeout.Token).ConfigureAwait(false);
                return JsonSerializer.Deserialize<ClaudeAppReply>(line) ?? new ClaudeAppReply(false, NotRunning);
            }
            catch (Exception e) when (!ct.IsCancellationRequested
                && e is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException or JsonException or InvalidDataException)
            {
                return new ClaudeAppReply(false, NotRunning);
            }
        }
    }

    private static async Task WriteLineAsync(Stream stream, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        if (bytes.Length > MaxMessageBytes) throw new InvalidDataException("The message is too long.");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        using var message = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) throw new IOException("The pipe closed before the message ended.");
            var end = Array.IndexOf(buffer, (byte)'\n', 0, read);
            message.Write(buffer, 0, end < 0 ? read : end);
            if (message.Length > MaxMessageBytes) throw new InvalidDataException("The message is too long.");
            if (end >= 0) return Encoding.UTF8.GetString(message.ToArray());
        }
    }
}
