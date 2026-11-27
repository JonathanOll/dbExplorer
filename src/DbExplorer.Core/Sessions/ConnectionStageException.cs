using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbExplorer.Core.Sessions;

/// <summary>The steps of opening a connection, in order.</summary>
public enum ConnectionStage
{
    /// <summary>Reaching the SSH server.</summary>
    SshConnect,

    /// <summary>Checking the identity of the SSH server.</summary>
    SshHostKey,

    /// <summary>Signing in to the SSH server (key file included).</summary>
    SshSignIn,

    /// <summary>The SSH server reaching the database server.</summary>
    SshForward,

    /// <summary>Setting up TLS with the database server, certificates included.</summary>
    Tls,

    /// <summary>Signing in to the database.</summary>
    DatabaseSignIn,

    /// <summary>Anything else while connecting to the database.</summary>
    Database,
}

/// <summary>A connection failure that says which step failed. The message never holds a secret.</summary>
public class ConnectionStageException(ConnectionStage stage, string detail, Exception? inner = null)
    : Exception($"{Label(stage)}: {detail}", inner)
{
    public ConnectionStage Stage { get; } = stage;

    public static string Label(ConnectionStage stage) => stage switch
    {
        ConnectionStage.SshConnect => "SSH tunnel, cannot reach the SSH server",
        ConnectionStage.SshHostKey => "SSH tunnel, host key check failed",
        ConnectionStage.SshSignIn => "SSH tunnel, sign-in failed",
        ConnectionStage.SshForward => "SSH tunnel, the SSH server cannot reach the database",
        ConnectionStage.Tls => "TLS, the secure connection failed",
        ConnectionStage.DatabaseSignIn => "Database sign-in failed",
        _ => "Database connection failed",
    };

    /// <summary>Wraps a provider error, telling a refused sign-in from other failures.</summary>
    public static ConnectionStageException Database(Exception e)
    {
        var stage = IsSignIn(e) ? ConnectionStage.DatabaseSignIn : ConnectionStage.Database;
        var root = e.GetBaseException().Message.Trim();
        var text = e.Message.Trim();
        if (!text.Contains(root, StringComparison.OrdinalIgnoreCase)) text += ": " + root;
        return new ConnectionStageException(stage, text, e);
    }

    private static bool IsSignIn(Exception e) => e switch
    {
        PostgresException pg => pg.SqlState.StartsWith("28", StringComparison.Ordinal),
        NpgsqlException npg => npg.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                               || npg.Message.Contains("authenticat", StringComparison.OrdinalIgnoreCase),
        SqlException sql => sql.Number is 18452 or 18456 or 18470 or 18486 or 18487 or 18488
                            || sql.Message.Contains("Active Directory", StringComparison.OrdinalIgnoreCase)
                            || sql.Message.Contains("SSPI", StringComparison.Ordinal),
        // MySqlConnector, matched by name so that other engines never load it.
        System.Data.Common.DbException db when db.GetType().Namespace == "MySqlConnector" => IsMySqlSignIn(db),
        _ => false,
    };

    private static bool IsMySqlSignIn(System.Data.Common.DbException e) =>
        e.SqlState?.StartsWith("28", StringComparison.Ordinal) == true
        || e.Message.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
        || e.Message.Contains("authenticat", StringComparison.OrdinalIgnoreCase);
}
