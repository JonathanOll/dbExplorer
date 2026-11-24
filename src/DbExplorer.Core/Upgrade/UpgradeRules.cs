using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Upgrade;

/// <summary>Where an upgrade can be played: its label, its databases and the group they come from (null for the current database).</summary>
public sealed record UpgradeTarget(string Label, IReadOnlyList<ConnectionConfig> Members, ConnectionGroup? Group);

/// <summary>Pure rules of the upgrade feature; no I/O. The upgrade is the one write path of the app besides the table editor.</summary>
public static class UpgradeRules
{
    /// <summary>
    /// Null when the targets can be upgraded, otherwise a user-facing message. A read-only connection is refused first (it is
    /// named), then mixed engines, then, when <paramref name="requireSqlServer"/>, any engine other than SQL Server (the scripts are T-SQL).
    /// </summary>
    public static string? Refusal(IReadOnlyList<ConnectionConfig> targets, bool requireSqlServer)
    {
        if (targets.Count == 0)
            return "There is no database to upgrade.";

        var readOnly = targets.Where(t => t.ReadOnly).Select(t => t.Name).ToList();
        if (readOnly.Count > 0)
            return $"Read-only connections cannot be upgraded: {string.Join(", ", readOnly)}. Untick Read-only in the connection's menu first; nothing was run.";

        var kinds = targets.Select(t => t.Kind).Distinct().ToList();
        if (kinds.Count > 1)
            return $"All databases of an upgrade must use the same database kind (found {string.Join(", ", kinds)}); nothing was run.";

        if (requireSqlServer)
        {
            var others = targets.Where(t => t.Kind != DbKind.SqlServer).Select(t => $"{t.Name} ({t.Kind})").ToList();
            if (others.Count > 0)
                return $"Upgrade scripts are T-SQL: only SQL Server databases can be upgraded (not: {string.Join(", ", others)}).";
        }

        return null;
    }

    /// <summary>The current database first, then each group of it (config order) that holds at least 2 databases.</summary>
    public static IReadOnlyList<UpgradeTarget> TargetsFor(AppConfig cfg, ConnectionConfig current)
    {
        var targets = new List<UpgradeTarget> { new($"This database ({current.Name})", [current], null) };
        foreach (var group in GroupRules.GroupsOf(cfg, current.Id))
        {
            var members = GroupRules.MembersOf(cfg, group);
            if (members.Count >= 2)
                targets.Add(new UpgradeTarget($"Group {group.Name} ({members.Count} databases)", members, group));
        }
        return targets;
    }
}
