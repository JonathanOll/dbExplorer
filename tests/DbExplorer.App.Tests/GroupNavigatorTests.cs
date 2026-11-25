using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;
using static DbExplorer.App.Tests.TreeFixture;

namespace DbExplorer.App.Tests;

/// <summary>The navigator's groups: connect a whole group, browse the databases of a group, hide folders and groups.</summary>
public class GroupNavigatorTests : IDisposable
{
    private readonly TreeFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static ConnectionNodeViewModel NodeOf(ConnectionTreeViewModel tree, string name) =>
        tree.Nodes.SelectMany(Flatten).OfType<ConnectionNodeViewModel>().First(n => n.Title == name);

    private static IEnumerable<TreeNodeViewModel> Flatten(TreeNodeViewModel n) => new[] { n }.Concat(n.Children.SelectMany(Flatten));

    private (ConnectionTreeViewModel Tree, GroupNodeViewModel Group, ConnectionConfig A, ConnectionConfig B) TwoDatabases(bool inFolder = false)
    {
        var a = Conn("Alpha", inFolder ? "Prod" : null, _fx.CreateSqliteFile("a.db"));
        var b = Conn("Beta", inFolder ? "Prod" : null, _fx.CreateSqliteFile("b.db"));
        var group = new ConnectionGroup(Guid.NewGuid(), "Pair", [a.Id, b.Id]);
        var config = new AppConfig(inFolder ? ["Prod"] : [], [a, b], new()) { Groups = [group] };
        var tree = _fx.CreateTree(config);
        return (tree, tree.Nodes.OfType<GroupsSectionNodeViewModel>().Single().Children.OfType<GroupNodeViewModel>().Single(), a, b);
    }

    [Fact]
    public async Task ConnectGroup_ConnectsEveryMember_AndOpensTheGroup()
    {
        var (tree, group, _, _) = TwoDatabases();

        await tree.ConnectGroupAsync(group);

        Assert.True(group.IsExpanded);
        Assert.All(group.Children.Cast<GroupMemberNodeViewModel>(), m => Assert.True(m.Target.IsConnected));
        Assert.Contains("2 of 2", _fx.Status.Message);
    }

    [Fact]
    public async Task ConnectGroup_AMemberThatFails_DoesNotStopTheOthers()
    {
        var good = Conn("Good", null, _fx.CreateSqliteFile("good.db"));
        var bad = Conn("Bad", null, Path.Combine(_fx.Dir, "missing.db"));
        var group = new ConnectionGroup(Guid.NewGuid(), "Mixed", [bad.Id, good.Id]);
        var tree = _fx.CreateTree(new AppConfig([], [good, bad], new()) { Groups = [group] });
        var node = tree.Nodes.OfType<GroupsSectionNodeViewModel>().Single().Children.OfType<GroupNodeViewModel>().Single();

        await tree.ConnectGroupAsync(node);

        Assert.True(NodeOf(tree, "Good").IsConnected);
        Assert.True(NodeOf(tree, "Bad").HasError);
        Assert.Contains("1 of 2", _fx.Status.Message);
    }

    [Fact]
    public async Task AMember_ShowsTheTablesOfItsConnection()
    {
        var (tree, group, _, _) = TwoDatabases();
        var member = group.Children.Cast<GroupMemberNodeViewModel>().First();

        // before the connection: a "Loading…" child gives the expander
        Assert.IsType<PlaceholderNodeViewModel>(Assert.Single(member.Children));

        await tree.ConnectAsync(member.Target);

        Assert.NotEmpty(member.Children);
        Assert.DoesNotContain(member.Children, c => c is PlaceholderNodeViewModel);
        Assert.Same(member.Target.Children, member.Children);   // the very same nodes: one table, one node
        Assert.All(member.Children.OfType<TableNodeViewModel>(), t => Assert.Same(t.Session, tree.FindSession(member.Target.Config.Id)));
    }

    [Fact]
    public async Task ExpandingADisconnectedMember_ConnectsIt()
    {
        var (tree, group, _, _) = TwoDatabases();
        var member = group.Children.Cast<GroupMemberNodeViewModel>().First();

        member.IsExpanded = true;
        for (var i = 0; i < 200 && !member.Target.IsConnected; i++) await Task.Delay(20);

        Assert.True(member.Target.IsConnected);
        Assert.NotEmpty(member.Children.OfType<TableNodeViewModel>());
    }

    [Fact]
    public async Task AMember_FollowsADisconnect_AndARebuildDoesNotLeakHandlers()
    {
        var (tree, group, _, _) = TwoDatabases();
        await tree.ConnectGroupAsync(group);

        tree.Rebuild();
        tree.Rebuild();
        var member = tree.Nodes.OfType<GroupsSectionNodeViewModel>().Single().Children.OfType<GroupNodeViewModel>().Single()
            .Children.Cast<GroupMemberNodeViewModel>().First();
        Assert.NotEmpty(member.Children.OfType<TableNodeViewModel>());   // a new member picks up the open connection's schema

        tree.Disconnect(member.Target.Config.Id);

        Assert.IsType<PlaceholderNodeViewModel>(Assert.Single(member.Children));
    }

    [Fact]
    public void HidingFolders_ListsEveryConnectionFlat()
    {
        var (tree, _, _, _) = TwoDatabases(inFolder: true);
        Assert.Contains(tree.Nodes, n => n is FolderNodeViewModel);
        Assert.DoesNotContain(tree.Nodes, n => n is ConnectionNodeViewModel);

        tree.ToggleFoldersCommand.Execute(null);

        Assert.False(tree.ShowFolders);
        Assert.DoesNotContain(tree.Nodes, n => n is FolderNodeViewModel);
        Assert.Equal(["Alpha", "Beta"], tree.Nodes.OfType<ConnectionNodeViewModel>().Select(n => n.Title));

        tree.ToggleFoldersCommand.Execute(null);
        Assert.Contains(tree.Nodes, n => n is FolderNodeViewModel);
    }

    [Fact]
    public void HidingGroups_RemovesTheSection_AndTheChoiceIsSaved()
    {
        var (tree, _, _, _) = TwoDatabases();

        tree.ToggleGroupsCommand.Execute(null);

        Assert.False(tree.ShowGroups);
        Assert.DoesNotContain(tree.Nodes, n => n is GroupsSectionNodeViewModel);
        var saved = _fx.Store.Load();
        Assert.False(saved.ShowGroups);
        Assert.True(saved.ShowFolders);

        tree.ToggleGroupsCommand.Execute(null);
        Assert.Contains(tree.Nodes, n => n is GroupsSectionNodeViewModel);
    }

    [Fact]
    public async Task Filter_DoesNotHideTablesOfAConnection_BecauseOfItsGroupShortcut()
    {
        var (tree, group, _, _) = TwoDatabases();
        await tree.ConnectGroupAsync(group);
        var table = NodeOf(tree, "Alpha").Children.OfType<TableNodeViewModel>().First();

        tree.FilterText = "Alpha";

        Assert.True(table.IsShown);   // under a matching connection everything stays visible, whichever path reaches it
    }
}
