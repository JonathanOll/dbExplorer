using DbExplorer.Core.Model;
using DbExplorer.Core.Upgrade;

namespace DbExplorer.Core.Tests.Upgrade;

public class UpgradeRulesTests
{
    private static ConnectionConfig Conn(string name, DbKind kind = DbKind.SqlServer, bool readOnly = false) =>
        new(Guid.NewGuid(), name, kind, null, "h", null, "d", null, null, false, false, readOnly);

    [Fact]
    public void Refusal_IsNullForPlainSqlServerTargets()
    {
        Assert.Null(UpgradeRules.Refusal([Conn("a"), Conn("b")], requireSqlServer: true));
    }

    [Fact]
    public void Refusal_NoTarget()
    {
        Assert.Equal("There is no database to upgrade.", UpgradeRules.Refusal([], requireSqlServer: true));
        Assert.Equal("There is no database to upgrade.", UpgradeRules.Refusal([], requireSqlServer: false));
    }

    [Fact]
    public void Refusal_ReadOnly_NamesEveryReadOnlyConnection()
    {
        var message = UpgradeRules.Refusal([Conn("A", readOnly: true), Conn("ok"), Conn("B", readOnly: true)], requireSqlServer: true);

        Assert.Equal(
            "Read-only connections cannot be upgraded: A, B. Untick Read-only in the connection's menu first; nothing was run.",
            message);
    }

    [Fact]
    public void Refusal_ReadOnly_WinsOverTheEngineChecks()
    {
        var message = UpgradeRules.Refusal([Conn("A", DbKind.Postgres, readOnly: true), Conn("B")], requireSqlServer: true);

        Assert.StartsWith("Read-only connections cannot be upgraded: A.", message);
    }

    [Fact]
    public void Refusal_MixedEngines()
    {
        var message = UpgradeRules.Refusal([Conn("a"), Conn("b", DbKind.Postgres)], requireSqlServer: false);

        Assert.NotNull(message);
        Assert.Contains("same", message);
    }

    [Fact]
    public void Refusal_NonSqlServer_OnlyWhenRequired()
    {
        var pg = Conn("X", DbKind.Postgres);

        Assert.Null(UpgradeRules.Refusal([pg], requireSqlServer: false));
        Assert.Equal(
            "Upgrade scripts are T-SQL: only SQL Server databases can be upgraded (not: X (Postgres)).",
            UpgradeRules.Refusal([pg], requireSqlServer: true));
    }

    [Fact]
    public void Refusal_NonSqlServer_ListsEveryOffender()
    {
        var message = UpgradeRules.Refusal([Conn("X", DbKind.Postgres), Conn("Y", DbKind.Postgres)], requireSqlServer: true);

        Assert.NotNull(message);
        Assert.Contains("not: X (Postgres), Y (Postgres)", message);
    }

    [Fact]
    public void TargetsFor_ConnectionInNoGroup_IsOneTarget()
    {
        var current = Conn("cur");
        var cfg = AppConfig.Empty();
        cfg.Connections.Add(current);

        var targets = UpgradeRules.TargetsFor(cfg, current);

        var only = Assert.Single(targets);
        Assert.Equal("This database (cur)", only.Label);
        Assert.Equal([current], only.Members);
        Assert.Null(only.Group);
    }

    [Fact]
    public void TargetsFor_OneMemberGroup_IsStillOneTarget()
    {
        var current = Conn("cur");
        var cfg = AppConfig.Empty();
        cfg.Connections.Add(current);
        cfg.Groups.Add(new ConnectionGroup(Guid.NewGuid(), "solo", [current.Id]));

        Assert.Single(UpgradeRules.TargetsFor(cfg, current));
    }

    [Fact]
    public void TargetsFor_TwoGroups_AreListedInConfigOrder_WithTheirMembers()
    {
        var current = Conn("cur");
        var b = Conn("b");
        var c = Conn("c");
        var other = Conn("other");
        var cfg = AppConfig.Empty();
        cfg.Connections.AddRange([current, b, c, other]);
        var g1 = new ConnectionGroup(Guid.NewGuid(), "prod", [current.Id, b.Id]);
        var g2 = new ConnectionGroup(Guid.NewGuid(), "all", [c.Id, current.Id, b.Id]);
        var g3 = new ConnectionGroup(Guid.NewGuid(), "not mine", [other.Id, c.Id]);
        cfg.Groups.AddRange([g1, g2, g3]);

        var targets = UpgradeRules.TargetsFor(cfg, current);

        Assert.Equal(3, targets.Count);
        Assert.Equal("This database (cur)", targets[0].Label);
        Assert.Null(targets[0].Group);
        Assert.Equal("Group prod (2 databases)", targets[1].Label);
        Assert.Same(g1, targets[1].Group);
        Assert.Equal([current, b], targets[1].Members);
        Assert.Equal("Group all (3 databases)", targets[2].Label);
        Assert.Same(g2, targets[2].Group);
        Assert.Equal([c, current, b], targets[2].Members);
    }
}
