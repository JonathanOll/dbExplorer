namespace DbExplorer.Core.Model;

/// <summary>What Claude Code may do on one connection. Persisted by name.</summary>
public enum ClaudeAccess
{
    /// <summary>The connection is not visible to Claude Code.</summary>
    None,

    /// <summary>Object names, columns, keys, indexes and view / routine definitions. No table data.</summary>
    Schema,

    /// <summary>As <see cref="Schema"/>, and read-only queries whose rows are sent to Claude.</summary>
    SchemaAndData,
}

/// <summary>
/// The Claude Code integration, off until the user turns it on and picks connections. A connection that is not listed is
/// not accessible. What is stored here is only the user's choice: <c>ClaudePolicy</c> decides what it allows.
/// </summary>
public sealed record ClaudeSettings
{
    public bool Enabled { get; set; }

    /// <summary>Connection id to access level.</summary>
    public Dictionary<Guid, ClaudeAccess> Connections { get; set; } = new();

    /// <summary>A copy safe to use: a null map (hand-edited file) is empty and unknown levels grant nothing.</summary>
    public ClaudeSettings Normalized() => Connections is { Count: 0 } ? this : this with
    {
        Connections = (Connections ?? new()).Where(p => Enum.IsDefined(p.Value) && p.Value != ClaudeAccess.None)
            .ToDictionary(p => p.Key, p => p.Value),
    };

    public ClaudeAccess Of(Guid connectionId) => Connections.GetValueOrDefault(connectionId);
}
