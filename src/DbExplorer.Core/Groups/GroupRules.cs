using DbExplorer.Core.Model;

namespace DbExplorer.Core.Groups;

/// <summary>Pure rules over <see cref="ConnectionGroup"/>s; no I/O.</summary>
public static class GroupRules
{
    /// <summary>Returns null when the group is valid, otherwise a user-facing message.</summary>
    public static string? Validate(AppConfig cfg, ConnectionGroup g)
    {
        if (string.IsNullOrWhiteSpace(g.Name))
            return "The group name cannot be empty.";

        var name = g.Name.Trim();
        if (cfg.Groups.Any(o => o.Id != g.Id && string.Equals(o.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            return $"A group named \"{name}\" already exists.";

        var seen = new HashSet<Guid>();
        var kinds = new List<DbKind>();
        foreach (var id in g.ConnectionIds)
        {
            if (!seen.Add(id))
                return "A connection appears more than once in the group.";
            var conn = cfg.Connections.FirstOrDefault(c => c.Id == id);
            if (conn is null)
                return "The group references a connection that no longer exists.";
            if (!kinds.Contains(conn.Kind)) kinds.Add(conn.Kind);
        }

        if (kinds.Count > 1)
            return $"All connections in a group must use the same database kind (found {string.Join(", ", kinds)}).";

        return null;
    }

    /// <summary>Every group containing the connection, in config order.</summary>
    public static IReadOnlyList<ConnectionGroup> GroupsOf(AppConfig cfg, Guid connectionId) =>
        cfg.Groups.Where(g => g.ConnectionIds.Contains(connectionId)).ToList();

    /// <summary>The group's connections in group order; unknown ids are skipped, a connection id repeated in the group (or in the connection list) counts once, at its first place.</summary>
    public static IReadOnlyList<ConnectionConfig> MembersOf(AppConfig cfg, ConnectionGroup g)
    {
        var byId = new Dictionary<Guid, ConnectionConfig>();
        foreach (var c in cfg.Connections) byId.TryAdd(c.Id, c);   // a hand-edited config may repeat an id: first wins
        var members = new List<ConnectionConfig>();
        var added = new HashSet<Guid>();
        foreach (var id in g.ConnectionIds)
            if (byId.TryGetValue(id, out var c) && added.Add(id)) members.Add(c);   // a repeated id counts once (first position)
        return members;
    }

    /// <summary>Removes the connection from every group; groups are kept even when left with 0 or 1 member.</summary>
    public static void RemoveConnection(AppConfig cfg, Guid connectionId)
    {
        foreach (var g in cfg.Groups) g.ConnectionIds.RemoveAll(id => id == connectionId);
    }
}
