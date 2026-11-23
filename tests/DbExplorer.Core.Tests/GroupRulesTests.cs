using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Tests;

public class GroupRulesTests
{
    private static ConnectionConfig Conn(string name, DbKind kind) =>
        new(Guid.NewGuid(), name, kind, null, "h", null, "db", null, null, false, false, false);

    private static AppConfig Cfg(params ConnectionConfig[] conns) =>
        new(new(), conns.ToList(), new());

    [Fact]
    public void Validate_AcceptsSameKindMembers()
    {
        var a = Conn("a", DbKind.Postgres);
        var b = Conn("b", DbKind.Postgres);
        var cfg = Cfg(a, b);
        var g = new ConnectionGroup(Guid.NewGuid(), "Tenants", new() { a.Id, b.Id });

        Assert.Null(GroupRules.Validate(cfg, g));
    }

    [Fact]
    public void Validate_RejectsMixedKinds()
    {
        var a = Conn("a", DbKind.Postgres);
        var b = Conn("b", DbKind.SqlServer);
        var cfg = Cfg(a, b);
        var g = new ConnectionGroup(Guid.NewGuid(), "Mixed", new() { a.Id, b.Id });

        var msg = GroupRules.Validate(cfg, g);

        Assert.NotNull(msg);
        Assert.Contains("Postgres", msg);
        Assert.Contains("SqlServer", msg);
    }

    [Fact]
    public void Validate_RejectsDuplicateNameCaseInsensitive()
    {
        var a = Conn("a", DbKind.Postgres);
        var cfg = Cfg(a);
        cfg.Groups.Add(new ConnectionGroup(Guid.NewGuid(), "Tenants", new() { a.Id }));
        var other = new ConnectionGroup(Guid.NewGuid(), "tENANTS", new() { a.Id });

        Assert.NotNull(GroupRules.Validate(cfg, other));
    }

    [Fact]
    public void Validate_SameGroupAlreadyInConfig_IsNotADuplicateOfItself()
    {
        var a = Conn("a", DbKind.Postgres);
        var cfg = Cfg(a);
        var g = new ConnectionGroup(Guid.NewGuid(), "Tenants", new() { a.Id });
        cfg.Groups.Add(g);

        Assert.Null(GroupRules.Validate(cfg, g));
    }

    [Fact]
    public void Validate_RejectsUnknownAndDuplicateMembers()
    {
        var a = Conn("a", DbKind.Postgres);
        var cfg = Cfg(a);

        var unknown = new ConnectionGroup(Guid.NewGuid(), "G1", new() { a.Id, Guid.NewGuid() });
        var dup = new ConnectionGroup(Guid.NewGuid(), "G2", new() { a.Id, a.Id });

        Assert.NotNull(GroupRules.Validate(cfg, unknown));
        Assert.NotNull(GroupRules.Validate(cfg, dup));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsEmptyName(string name)
    {
        var a = Conn("a", DbKind.Postgres);
        var g = new ConnectionGroup(Guid.NewGuid(), name, new() { a.Id });

        Assert.NotNull(GroupRules.Validate(Cfg(a), g));
    }

    [Fact]
    public void GroupsOf_ReturnsEveryGroupContainingTheConnection()
    {
        var a = Conn("a", DbKind.Postgres);
        var b = Conn("b", DbKind.Postgres);
        var cfg = Cfg(a, b);
        var g1 = new ConnectionGroup(Guid.NewGuid(), "G1", new() { a.Id, b.Id });
        var g2 = new ConnectionGroup(Guid.NewGuid(), "G2", new() { a.Id });
        var g3 = new ConnectionGroup(Guid.NewGuid(), "G3", new() { b.Id });
        cfg.Groups.AddRange(new[] { g1, g2, g3 });

        Assert.Equal(new[] { g1, g2 }, GroupRules.GroupsOf(cfg, a.Id));
        Assert.Equal(new[] { g1, g3 }, GroupRules.GroupsOf(cfg, b.Id));
        Assert.Empty(GroupRules.GroupsOf(cfg, Guid.NewGuid()));
    }

    [Fact]
    public void MembersOf_KeepsOrder_AndSkipsUnknown()
    {
        var a = Conn("a", DbKind.Postgres);
        var b = Conn("b", DbKind.Postgres);
        var c = Conn("c", DbKind.Postgres);
        var cfg = Cfg(a, b, c);
        var g = new ConnectionGroup(Guid.NewGuid(), "G", new() { c.Id, Guid.NewGuid(), a.Id, b.Id });

        Assert.Equal(new[] { c, a, b }, GroupRules.MembersOf(cfg, g));
    }

    [Fact]
    public void MembersOf_ToleratesDuplicateConnectionIds_FirstWins()
    {
        // A hand-edited config can repeat a connection id: it must not crash the tree rebuild at startup.
        var a = Conn("a", DbKind.Postgres);
        var dup = a with { Name = "a (duplicate id)" };
        var cfg = Cfg(a, dup);
        var g = new ConnectionGroup(Guid.NewGuid(), "G", new() { a.Id });

        Assert.Equal(new[] { a }, GroupRules.MembersOf(cfg, g));
    }

    [Fact]
    public void MembersOf_ARepeatedIdInTheGroup_CountsOnce_AtItsFirstPlace()
    {
        var a = Conn("a", DbKind.Postgres);
        var b = Conn("b", DbKind.Postgres);
        var cfg = Cfg(a, b);
        var g = new ConnectionGroup(Guid.NewGuid(), "G", new() { b.Id, a.Id, b.Id });

        Assert.Equal(new[] { b, a }, GroupRules.MembersOf(cfg, g));
    }

    [Fact]
    public void RemoveConnection_RemovesFromAllGroups_KeepsGroups()
    {
        var a = Conn("a", DbKind.Postgres);
        var b = Conn("b", DbKind.Postgres);
        var cfg = Cfg(a, b);
        var g1 = new ConnectionGroup(Guid.NewGuid(), "G1", new() { a.Id, b.Id });
        var g2 = new ConnectionGroup(Guid.NewGuid(), "G2", new() { a.Id });
        cfg.Groups.AddRange(new[] { g1, g2 });

        GroupRules.RemoveConnection(cfg, a.Id);

        Assert.Equal(2, cfg.Groups.Count);
        Assert.Equal(new[] { b.Id }, cfg.Groups[0].ConnectionIds);
        Assert.Empty(cfg.Groups[1].ConnectionIds);
    }
}
