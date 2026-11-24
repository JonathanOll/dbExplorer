using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace DbExplorer.Core.Scripting;

/// <summary>
/// A connection that reports the server's messages as they arrive. Implemented by the test doubles; SQL Server
/// connections are handled directly through <see cref="SqlConnection.InfoMessage"/>.
/// </summary>
public interface IServerMessageSource
{
    event Action<string>? MessageReceived;
}

/// <summary>
/// Collects the messages the server sends on one connection (PRINT, warnings, STATISTICS output) between <see cref="Start"/> and
/// <see cref="Dispose"/>. The handler is removed on dispose: the script connection is reused by later runs, so a handler left
/// behind would duplicate every message. <c>FireInfoMessageEventOnUserErrors</c> is deliberately NOT set: errors keep failing the
/// statement as before.
/// </summary>
internal sealed class ServerMessageCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly List<ServerMessage> _buffer = [];
    private Action? _detach;

    /// <summary>A capture on the connection, or null when its provider has no message events (then there is nothing to capture).</summary>
    public static ServerMessageCapture? Start(DbConnection connection)
    {
        var capture = new ServerMessageCapture();
        switch (connection)
        {
            case IServerMessageSource source:
                Action<string> onText = capture.Add;
                source.MessageReceived += onText;
                capture._detach = () => source.MessageReceived -= onText;
                return capture;
            case SqlConnection sql:
                SqlInfoMessageEventHandler onInfo = (_, e) =>
                {
                    // One event can carry several errors; Message would join them. Severity above 10 never comes here (it throws).
                    if (e.Errors.Count == 0) capture.Add(e.Message);
                    else foreach (SqlError error in e.Errors) capture.Add(error.Message);
                };
                sql.InfoMessage += onInfo;
                capture._detach = () => sql.InfoMessage -= onInfo;
                return capture;
            default:
                return null;
        }
    }

    /// <summary>
    /// For a <see cref="SqlException"/> that carries both info messages (class 10 or less) and errors: the messages, and the text of
    /// the errors alone. Null for anything else, or when there is no info message in it (nothing to separate).
    /// </summary>
    public static (IReadOnlyList<ServerMessage> Messages, string Error)? SplitSqlException(Exception ex)
    {
        if (ex is not SqlException sql) return null;
        var errors = sql.Errors.Cast<SqlError>().ToList();
        var infos = errors.Where(e => e.Class <= 10).ToList();
        var real = errors.Where(e => e.Class > 10).ToList();
        if (infos.Count == 0 || real.Count == 0) return null;

        var now = DateTimeOffset.Now;
        return (infos.Select(e => new ServerMessage(now, e.Message)).ToList(),
                string.Join(Environment.NewLine, real.Select(e => e.Message)));
    }

    private void Add(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate) _buffer.Add(new ServerMessage(DateTimeOffset.Now, text));
    }

    /// <summary>Takes what arrived since the last call (the messages of the statement that just ended).</summary>
    public IReadOnlyList<ServerMessage> Drain()
    {
        lock (_gate)
        {
            var taken = _buffer.ToArray();
            _buffer.Clear();
            return taken;
        }
    }

    public void Dispose()
    {
        var detach = Interlocked.Exchange(ref _detach, null);
        detach?.Invoke();
    }
}

/// <summary>
/// The session settings of a run with statistics: <c>SET STATISTICS IO / TIME ON</c> before the user's statements and OFF after
/// them. Sent as commands on the connection, never as script text, so the read-only guard and the statement splitting do not see
/// them. SET options are session-level: they survive a rollback, and a pooled connection is reset by the provider anyway, but
/// the script connection is dedicated and reused, so it is switched off again explicitly.
/// </summary>
internal static class StatisticsSession
{
    public const string On = "SET STATISTICS IO ON; SET STATISTICS TIME ON;";
    public const string Off = "SET STATISTICS IO OFF; SET STATISTICS TIME OFF;";

    public static async Task EnableAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = On;
        cmd.CommandTimeout = 30;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Switches the options off; false when that failed (the connection is then suspect and gets probed or dropped).</summary>
    public static async Task<bool> DisableAsync(DbConnection connection)
    {
        if (connection.State != ConnectionState.Open) return true;   // closed: nothing is left on it
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = Off;
            cmd.CommandTimeout = 10;
            await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
