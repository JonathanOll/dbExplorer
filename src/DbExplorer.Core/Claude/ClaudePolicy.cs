using DbExplorer.Core.Model;

namespace DbExplorer.Core.Claude;

/// <summary>What Claude Code may do on a connection right now, or why it may not.</summary>
/// <param name="Refusal">Null when <see cref="Access"/> is not <see cref="ClaudeAccess.None"/>.</param>
public readonly record struct ClaudeGrant(ClaudeAccess Access, string? Refusal)
{
    public bool Allowed => Access != ClaudeAccess.None;
    public bool CanReadData => Access == ClaudeAccess.SchemaAndData;
}

/// <summary>
/// The one place that decides access, used by the MCP server and by the running app. Deny by default: the integration is
/// off, no connection is listed, and a production connection is refused whatever is stored for it.
/// </summary>
public static class ClaudePolicy
{
    public const string Disabled = "The Claude Code integration is turned off in dbExplorer (Settings, Claude Code).";

    public static ClaudeGrant For(AppConfig config, ConnectionConfig connection)
    {
        if (!config.Claude.Enabled) return Refuse(Disabled);
        if (connection.IsProduction) return Refuse("Production connections are never available to Claude Code.");
        var access = config.Claude.Of(connection.Id);
        return access is ClaudeAccess.Schema or ClaudeAccess.SchemaAndData
            ? new ClaudeGrant(access, null)
            : Refuse("This connection is not allowed for Claude Code (dbExplorer, Settings, Claude Code).");
    }

    /// <summary>
    /// Why an MCP call may not open the connection: signing in would show a window, which only the user may trigger.
    /// SSH servers need no entry here: without a prompt an unknown host key is refused.
    /// </summary>
    public static string? CannotConnectUnattended(ConnectionConfig connection) =>
        connection.Auth == AuthMode.EntraInteractive
            ? "This connection signs in interactively (Microsoft Entra with MFA): Claude Code cannot open it."
            : null;

    /// <summary>The connections Claude Code may see, in the order of the config.</summary>
    public static IEnumerable<(ConnectionConfig Connection, ClaudeAccess Access)> Allowed(AppConfig config) =>
        from c in config.Connections
        let grant = For(config, c)
        where grant.Allowed
        select (c, grant.Access);

    private static ClaudeGrant Refuse(string why) => new(ClaudeAccess.None, why);
}
