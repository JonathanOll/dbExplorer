using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;
using static DbExplorer.App.Tests.TreeFixture;

namespace DbExplorer.App.Tests;

/// <summary>Groups in the navigator, the group editor and the schema-difference check (view models only, no UI).</summary>
public class GroupTreeTests : IDisposable
{
    private readonly TreeFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static AppConfig Config(IEnumerable<ConnectionConfig> connections, params ConnectionGroup[] groups) =>
        new(new(), connections.ToList(), new()) { Groups = groups.ToList() };

    private static ConnectionGroup Group(string name, params ConnectionConfig[] members) =>
        new(Guid.NewGuid(), name, members.Select(m => m.Id).ToList());

    private static GroupsSectionNodeViewModel Section(ConnectionTreeViewModel tree) =>
        tree.Nodes.OfType<GroupsSectionNodeViewModel>().Single();

    // ---- tree ----

    [Fact]
    public void Tree_ShowsGroupsSection_WithMemberNodes()
    {
        var a = Conn("Resto");
        var b = Conn("Resto2");
        var other = Conn("Other");
        var tree = _fx.CreateTree(Config([a, b, other], Group("Resto pair", a, b)));

        var section = Section(tree);
        var group = Assert.IsType<GroupNodeViewModel>(Assert.Single(section.Children));
        Assert.Equal("Resto pair", group.Title);
        Assert.Equal(["Resto", "Resto2"], group.Children.Select(n => n.Title));
        var members = group.Children.Cast<GroupMemberNodeViewModel>().ToList();
        Assert.All(members, m => Assert.Same(group.Group, m.Group));
        // A member is a shortcut to the real connection: same node, so connected state is shared.
        Assert.Contains(tree.Nodes, n => n is ConnectionNodeViewModel c && ReferenceEquals(c, members[0].Target));
        Assert.Equal(DbKind.Sqlite, group.Kind);
        Assert.Equal(3, tree.Nodes.OfType<ConnectionNodeViewModel>().Count());
    }

    [Fact]
    public void Tree_NoGroups_ShowsNoSection()
    {
        var tree = _fx.CreateTree(Config([Conn("A"), Conn("B")]));

        Assert.DoesNotContain(tree.Nodes, n => n is GroupsSectionNodeViewModel);
    }

    [Fact]
    public void Tree_RebuildKeepsGroupExpansion_AndUnknownMembersAreSkipped()
    {
        var a = Conn("A");
        var group = new ConnectionGroup(Guid.NewGuid(), "G", [a.Id, Guid.NewGuid()]);
        var tree = _fx.CreateTree(Config([a], group));
        var node = Section(tree).Children.OfType<GroupNodeViewModel>().Single();
        node.IsExpanded = true;

        tree.Rebuild();

        var again = Section(tree).Children.OfType<GroupNodeViewModel>().Single();
        Assert.Same(node, again);
        Assert.True(again.IsExpanded);
        Assert.Single(again.Children);
    }

    [Fact]
    public void Tree_GroupWithFewerThanTwoMembers_IsFlagged()
    {
        var a = Conn("A");
        var b = Conn("B");
        var tree = _fx.CreateTree(Config([a, b], Group("Solo", a), Group("Pair", a, b)));

        var groups = Section(tree).Children.Cast<GroupNodeViewModel>().ToDictionary(g => g.Title);
        Assert.True(groups["Solo"].IsUnderPopulated);
        Assert.False(groups["Pair"].IsUnderPopulated);
    }

    [Fact]
    public void SelectedConnection_OfAGroupMemberNode_IsTheRealConnection()
    {
        var a = Conn("A");
        var tree = _fx.CreateTree(Config([a], Group("G", a)));
        var member = Section(tree).Children.OfType<GroupNodeViewModel>().Single().Children.Single();

        tree.SelectedNode = member;

        Assert.Equal(a, tree.SelectedConnection);
    }

    [Fact]
    public async Task OpenGroupMember_ConnectsOnlyThatRealConnection()
    {
        var a = Conn("A", database: _fx.CreateSqliteFile("a.db"));
        var b = Conn("B", database: _fx.CreateSqliteFile("b.db"));
        var tree = _fx.CreateTree(Config([a, b], Group("G", a, b)));
        var member = (GroupMemberNodeViewModel)Section(tree).Children.OfType<GroupNodeViewModel>().Single().Children[1];

        await tree.OpenGroupMemberCommand.ExecuteAsync(member);

        Assert.Equal(ConnectionState.Connected, member.Target.State);
        Assert.NotNull(tree.FindSession(b.Id));
        Assert.Equal(ConnectionState.Disconnected, ((GroupMemberNodeViewModel)Section(tree).Children.OfType<GroupNodeViewModel>().Single().Children[0]).Target.State);
    }

    // ---- group editor ----

    [Fact]
    public void GroupEditor_FirstMemberRestrictsCandidatesToItsEngine()
    {
        var pg1 = Conn("PG one", kind: DbKind.Postgres, database: "one");
        var pg2 = Conn("PG two", kind: DbKind.Postgres, database: "two");
        var lite = Conn("Lite");
        var vm = new GroupEditorViewModel(Config([pg1, pg2, lite]), null);

        Assert.Equal(3, vm.Candidates.Count);

        vm.Add(pg1);

        Assert.Equal([pg1], vm.Selected);
        Assert.Equal([pg2], vm.Candidates);   // same engine only, the chosen one is not offered twice

        vm.Remove(pg1);

        Assert.Empty(vm.Selected);
        Assert.Equal(3, vm.Candidates.Count);
    }

    [Fact]
    public void GroupEditor_KeepsSelectionOrder_AndCanReorder()
    {
        var a = Conn("A");
        var b = Conn("B");
        var c = Conn("C");
        var vm = new GroupEditorViewModel(Config([a, b, c]), null);

        vm.Add(c);
        vm.Add(a);
        vm.Add(b);
        Assert.Equal([c, a, b], vm.Selected);

        vm.MoveUp(b);
        Assert.Equal([c, b, a], vm.Selected);
        vm.MoveDown(c);
        Assert.Equal([b, c, a], vm.Selected);
        vm.MoveUp(b);   // already first: nothing happens
        Assert.Equal([b, c, a], vm.Selected);
    }

    [Fact]
    public void GroupEditor_Valid_BuildsResult_KeepingTheIdOfAnExistingGroup()
    {
        var a = Conn("A");
        var b = Conn("B");
        var existing = Group("Old", a);
        var vm = new GroupEditorViewModel(Config([a, b], existing), existing);

        Assert.Equal("Old", vm.Name);
        Assert.Equal([a], vm.Selected);
        vm.Name = "  New name ";
        vm.Add(b);

        Assert.True(vm.TryConfirm());

        Assert.Null(vm.Error);
        Assert.Equal(existing.Id, vm.Result!.Id);
        Assert.Equal("New name", vm.Result.Name);
        Assert.Equal([a.Id, b.Id], vm.Result.ConnectionIds);
    }

    [Fact]
    public void GroupEditor_InvalidNameOrMixedKinds_SetsError_AndNoResult()
    {
        var lite = Conn("Lite");
        var pg = Conn("PG", kind: DbKind.Postgres, database: "x");
        var taken = Group("Taken", lite);
        var vm = new GroupEditorViewModel(Config([lite, pg], taken), null);

        // empty name
        Assert.False(vm.TryConfirm());
        Assert.False(string.IsNullOrWhiteSpace(vm.Error));
        Assert.Null(vm.Result);

        // duplicate name
        vm.Name = "taken";
        Assert.False(vm.TryConfirm());
        Assert.Contains("already exists", vm.Error);
        Assert.Null(vm.Result);

        // mixed engines (not reachable through the lists, but the rule is still enforced)
        vm.Name = "Mixed";
        vm.Add(lite);
        vm.Add(pg);
        Assert.False(vm.TryConfirm());
        Assert.Contains("same database kind", vm.Error);
        Assert.Null(vm.Result);

        // fixing the problem clears the error
        vm.Remove(pg);
        Assert.True(vm.TryConfirm());
        Assert.Null(vm.Error);
        Assert.NotNull(vm.Result);
    }

    [Fact]
    public void GroupEditor_FewerThanTwoMembers_IsAllowedButWarns()
    {
        var a = Conn("A");
        var b = Conn("B");
        var vm = new GroupEditorViewModel(Config([a, b]), null) { Name = "G" };

        vm.Add(a);
        Assert.NotNull(vm.Warning);
        Assert.True(vm.TryConfirm());   // allowed

        vm.Add(b);
        Assert.Null(vm.Warning);
    }

    // ---- group commands ----

    [Fact]
    public async Task NewGroup_Persists_AndAppearsInTree()
    {
        var a = Conn("Resto");
        var b = Conn("Resto2");
        var tree = _fx.CreateTree(Config([a, b]));
        _fx.Dialogs.OnGroupEditor = vm =>
        {
            vm.Name = "Resto pair";
            vm.Add(a);
            vm.Add(b);
        };

        await tree.NewGroupCommand.ExecuteAsync(null);

        var saved = Assert.Single(_fx.Store.Load().Groups);
        Assert.Equal("Resto pair", saved.Name);
        Assert.Equal([a.Id, b.Id], saved.ConnectionIds);
        var group = Assert.IsType<GroupNodeViewModel>(Assert.Single(Section(tree).Children));
        Assert.Equal(["Resto", "Resto2"], group.Children.Select(n => n.Title));
    }

    [Fact]
    public async Task NewGroup_Cancelled_ChangesNothing()
    {
        var tree = _fx.CreateTree(Config([Conn("A")]));
        _fx.Dialogs.GroupEditorResult = false;
        _fx.Dialogs.OnGroupEditor = vm => vm.Name = "Never";

        await tree.NewGroupCommand.ExecuteAsync(null);

        Assert.Empty(tree.Config.Groups);
        Assert.False(File.Exists(_fx.ConfigPath));
    }

    [Fact]
    public async Task EditGroup_ReplacesTheGroupKeepingItsId()
    {
        var a = Conn("A");
        var b = Conn("B");
        var group = Group("G", a);
        var tree = _fx.CreateTree(Config([a, b], group));
        _fx.Dialogs.OnGroupEditor = vm =>
        {
            Assert.Equal("G", vm.Name);
            vm.Name = "G2";
            vm.Add(b);
        };

        await tree.EditGroupCommand.ExecuteAsync(group);

        var saved = Assert.Single(_fx.Store.Load().Groups);
        Assert.Equal(group.Id, saved.Id);
        Assert.Equal("G2", saved.Name);
        Assert.Equal([a.Id, b.Id], saved.ConnectionIds);
        Assert.Equal("G2", Section(tree).Children.Single().Title);
    }

    [Fact]
    public async Task RenameGroup_Persists_AndRejectsEmptyOrDuplicateNames()
    {
        var a = Conn("A");
        var g1 = Group("One", a);
        var g2 = Group("Two", a);
        var tree = _fx.CreateTree(Config([a], g1, g2));

        _fx.Dialogs.PromptResult = "two";
        await tree.RenameGroupCommand.ExecuteAsync(g1);
        Assert.True(_fx.Status.IsError);
        Assert.Equal(["One", "Two"], tree.Config.Groups.Select(g => g.Name));
        Assert.False(File.Exists(_fx.ConfigPath));   // refused: nothing written

        _fx.Dialogs.PromptResult = " Uno ";
        await tree.RenameGroupCommand.ExecuteAsync(g1);

        Assert.Equal(["Uno", "Two"], _fx.Store.Load().Groups.Select(g => g.Name));
        Assert.Equal(["Uno", "Two"], tree.Config.Groups.Select(g => g.Name));
    }

    [Fact]
    public async Task DeleteGroup_Persists_AfterConfirmation()
    {
        var a = Conn("A");
        var g1 = Group("One", a);
        var g2 = Group("Two", a);
        var tree = _fx.CreateTree(Config([a], g1, g2));

        _fx.Dialogs.ConfirmResult = false;
        await tree.DeleteGroupCommand.ExecuteAsync(g1);
        Assert.Equal(2, tree.Config.Groups.Count);

        _fx.Dialogs.ConfirmResult = true;
        await tree.DeleteGroupCommand.ExecuteAsync(g1);

        Assert.Equal(["Two"], _fx.Store.Load().Groups.Select(g => g.Name));
        Assert.Equal(["Two"], Section(tree).Children.Select(n => n.Title));
        Assert.Single(tree.Config.Connections);   // members are untouched

        tree.DeleteGroup(g2);
        Assert.DoesNotContain(tree.Nodes, n => n is GroupsSectionNodeViewModel);
        Assert.Empty(_fx.Store.Load().Groups);
    }

    [Fact]
    public void DeleteConnection_RemovesItFromGroups_AndPersists()
    {
        var a = Conn("A");
        var b = Conn("B");
        var c = Conn("C");
        var tree = _fx.CreateTree(Config([a, b, c], Group("G1", a, b), Group("G2", b, c)));

        tree.DeleteConnection(b);

        var saved = _fx.Store.Load();
        Assert.Equal([a.Id], saved.Groups[0].ConnectionIds);
        Assert.Equal([c.Id], saved.Groups[1].ConnectionIds);
        var members = Section(tree).Children.Cast<GroupNodeViewModel>().Select(g => g.Children.Select(m => m.Title).ToList()).ToList();
        Assert.Equal([["A"], ["C"]], members);
    }

    [Fact]
    public void AddToGroup_AddsOnce_AndRefusesAnotherEngine()
    {
        var lite1 = Conn("L1");
        var lite2 = Conn("L2");
        var pg = Conn("PG", kind: DbKind.Postgres, database: "x");
        var group = Group("G", lite1);
        var tree = _fx.CreateTree(Config([lite1, lite2, pg], group));

        Assert.False(tree.AddToGroup(pg, group));
        Assert.True(_fx.Status.IsError);
        Assert.Equal([lite1.Id], tree.Config.Groups.Single().ConnectionIds);

        Assert.True(tree.AddToGroup(lite2, group));
        Assert.Equal([lite1.Id, lite2.Id], _fx.Store.Load().Groups.Single().ConnectionIds);

        Assert.False(tree.AddToGroup(lite2, tree.Config.Groups.Single()));   // already a member: nothing added twice
        Assert.Equal(2, tree.Config.Groups.Single().ConnectionIds.Count);
    }

    [Fact]
    public void RemoveFromGroup_Persists_AndKeepsTheGroup()
    {
        var a = Conn("A");
        var b = Conn("B");
        var group = Group("G", a, b);
        var tree = _fx.CreateTree(Config([a, b], group));

        tree.RemoveFromGroup(a, group);

        Assert.Equal([b.Id], _fx.Store.Load().Groups.Single().ConnectionIds);
        Assert.Equal(["B"], Section(tree).Children.Single().Children.Select(n => n.Title));
        Assert.Equal(2, tree.Config.Connections.Count);
    }

    [Fact]
    public async Task AddToGroupCommand_WithoutCompatibleGroup_OpensTheEditorSeededWithTheConnection()
    {
        var a = Conn("A");
        var tree = _fx.CreateTree(Config([a, Conn("B")]));
        var node = tree.Nodes.OfType<ConnectionNodeViewModel>().Single(n => n.Title == "A");
        _fx.Dialogs.OnGroupEditor = vm =>
        {
            Assert.Equal([a], vm.Selected);
            vm.Name = "Fresh";
        };

        await tree.AddToGroupCommand.ExecuteAsync(node);

        Assert.Equal(["Fresh"], tree.Config.Groups.Select(g => g.Name));
        Assert.Equal([a.Id], tree.Config.Groups.Single().ConnectionIds);
    }

    [Fact]
    public async Task AddToGroupCommand_ChoosesAmongCompatibleGroups()
    {
        var a = Conn("A");
        var b = Conn("B");
        var pg = Conn("PG", kind: DbKind.Postgres, database: "x");
        var liteGroup = Group("Lite group", b);
        var pgGroup = Group("Pg group", pg);
        var tree = _fx.CreateTree(Config([a, b, pg], liteGroup, pgGroup));
        var node = tree.Nodes.OfType<ConnectionNodeViewModel>().Single(n => n.Title == "A");
        _fx.Dialogs.ChooseResult = 0;

        await tree.AddToGroupCommand.ExecuteAsync(node);

        Assert.Equal(["Lite group", "New group…"], _fx.Dialogs.ChooseOptions.Single());   // the Postgres group is not offered
        Assert.Equal([b.Id, a.Id], _fx.Store.Load().Groups[0].ConnectionIds);
    }

    [Fact]
    public async Task PersistenceDisabled_GroupChangesApplyForTheSessionOnly_WithNotice()
    {
        var a = Conn("A");
        var b = Conn("B");
        var tree = new ConnectionTreeViewModel(_fx.Store, Config([a, b]), _fx.Sessions, _fx.Status, _fx.Dialogs, _fx.DBeaverDir, persistenceEnabled: false);
        _fx.Dialogs.OnGroupEditor = vm =>
        {
            vm.Name = "Temp";
            vm.Add(a);
            vm.Add(b);
        };

        await tree.NewGroupCommand.ExecuteAsync(null);

        Assert.False(File.Exists(_fx.ConfigPath));
        Assert.Equal("Temp", Assert.Single(tree.Config.Groups).Name);   // usable in memory
        Assert.Equal("Temp", Section(tree).Children.Single().Title);
        Assert.True(_fx.Status.IsError);
        Assert.Contains("NOT saved", _fx.Status.Message);
    }

    // ---- DBeaver import must not drop the groups ----

    [Fact]
    public async Task Import_KeepsTheGroups()
    {
        Directory.CreateDirectory(_fx.DBeaverDir);
        File.WriteAllText(Path.Combine(_fx.DBeaverDir, "data-sources.json"),
            """{ "connections": { "a": { "provider": "sqlite", "name": "Imported", "configuration": { "database": "i.db" } } } }""");
        var a = Conn("A");
        var b = Conn("B");
        var group = Group("Pair", a, b);
        var tree = _fx.CreateTree(Config([a, b], group));

        await tree.ImportFromDBeaverAsync();

        Assert.Equal(3, tree.Config.Connections.Count);
        var kept = Assert.Single(tree.Config.Groups);
        Assert.Equal("Pair", kept.Name);
        Assert.Equal([a.Id, b.Id], kept.ConnectionIds);
        var saved = _fx.Store.Load();
        Assert.Equal(3, saved.Connections.Count);
        Assert.Equal([a.Id, b.Id], Assert.Single(saved.Groups).ConnectionIds);
        Assert.Single(Section(tree).Children);
    }

    // ---- schema differences ----

    private ConnectionConfig Db(string name, string sql) => Conn(name, database: _fx.CreateSqliteFileWith(name + ".db", sql));

    [Fact]
    public async Task CheckSchemaDifferences_ShowsReportForLoadedSchemas()
    {
        var a = Db("A", "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT); CREATE TABLE logs (id INTEGER PRIMARY KEY);");
        var b = Db("B", "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, email TEXT); CREATE TABLE logs (id INTEGER PRIMARY KEY);");
        var c = Db("C", "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT);");
        var group = Group("Trio", a, b, c);
        var tree = _fx.CreateTree(Config([a, b, c], group));

        await tree.CheckSchemaDifferencesCommand.ExecuteAsync(group);

        var report = Assert.Single(_fx.Dialogs.SchemaReports);
        Assert.Equal("Trio", report.GroupName);
        Assert.Empty(report.Unreachable);
        Assert.True(report.HasDifferences);
        var byName = report.Members.ToDictionary(m => m.Database);
        Assert.Equal(["A", "B", "C"], report.Members.Select(m => m.Database));
        Assert.Contains(byName["C"].Findings, f => f.Label == "Missing table" && f.Subject == "logs");
        Assert.Contains(byName["A"].Findings, f => f.Label == "Missing column" && f.Subject == "users.email");
        Assert.Contains(byName["B"].Findings, f => f.Label == "Extra column" && f.Subject == "users.email");
        Assert.False(_fx.Status.IsBusy);
        Assert.False(_fx.Status.IsError);
        // the members were connected through the tree: the same sessions serve switching and runs later
        Assert.NotNull(tree.FindSession(a.Id));
        Assert.All(tree.Nodes.OfType<ConnectionNodeViewModel>(), n => Assert.Equal(ConnectionState.Connected, n.State));
    }

    [Fact]
    public async Task CheckSchemaDifferences_IdenticalSchemas_ReportsNoDifferences()
    {
        const string sql = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT);";
        var a = Db("A", sql);
        var b = Db("B", sql);
        var group = Group("Same", a, b);
        var tree = _fx.CreateTree(Config([a, b], group));

        await tree.CheckSchemaDifferencesCommand.ExecuteAsync(group);

        var report = Assert.Single(_fx.Dialogs.SchemaReports);
        Assert.False(report.HasDifferences);
        Assert.All(report.Members, m => Assert.True(m.IsClean));
    }

    [Fact]
    public async Task CheckSchemaDifferences_UsesTheCurrentSchemaOfAnAlreadyConnectedMember()
    {
        var a = Db("A", "CREATE TABLE users (id INTEGER PRIMARY KEY);");
        var b = Db("B", "CREATE TABLE users (id INTEGER PRIMARY KEY);");
        var group = Group("G", a, b);
        var tree = _fx.CreateTree(Config([a, b], group));
        await tree.ConnectAsync(tree.Nodes.OfType<ConnectionNodeViewModel>().Single(n => n.Title == "A"));
        _fx.CreateSqliteFileWith("A.db", "CREATE TABLE added_later (id INTEGER PRIMARY KEY);");   // changed behind the open session's back

        await tree.CheckSchemaDifferencesCommand.ExecuteAsync(group);

        var report = Assert.Single(_fx.Dialogs.SchemaReports);
        Assert.Contains(report.Members.Single(m => m.Database == "B").Findings, f => f.Label == "Missing table" && f.Subject == "added_later");
    }

    [Fact]
    public async Task CheckSchemaDifferences_UnreachableMemberIsReported_NotFatal()
    {
        var a = Db("A", "CREATE TABLE users (id INTEGER PRIMARY KEY);");
        var b = Db("B", "CREATE TABLE users (id INTEGER PRIMARY KEY, extra TEXT);");
        var gone = Conn("Gone", database: Path.Combine(_fx.Dir, "missing.db"));
        var group = Group("G", a, gone, b);
        var tree = _fx.CreateTree(Config([a, gone, b], group));

        await tree.CheckSchemaDifferencesCommand.ExecuteAsync(group);

        var report = Assert.Single(_fx.Dialogs.SchemaReports);
        var unreachable = Assert.Single(report.Unreachable);
        Assert.Equal("Gone", unreachable.Database);
        Assert.False(string.IsNullOrWhiteSpace(unreachable.Error));
        Assert.Equal(["A", "B"], report.Members.Select(m => m.Database));   // the others are still compared
        Assert.True(report.HasDifferences);
        Assert.False(_fx.Status.IsBusy);
    }

    [Fact]
    public async Task CheckSchemaDifferences_NothingToCompare_StillShowsWhyInTheReport()
    {
        var a = Db("A", "CREATE TABLE users (id INTEGER PRIMARY KEY);");
        var gone1 = Conn("Gone1", database: Path.Combine(_fx.Dir, "m1.db"));
        var gone2 = Conn("Gone2", database: Path.Combine(_fx.Dir, "m2.db"));
        var group = Group("G", a, gone1, gone2);
        var tree = _fx.CreateTree(Config([a, gone1, gone2], group));

        await tree.CheckSchemaDifferencesCommand.ExecuteAsync(group);

        var report = Assert.Single(_fx.Dialogs.SchemaReports);
        Assert.False(report.CanCompare);
        Assert.Equal(2, report.Unreachable.Count);
    }

    [Fact]
    public async Task CheckSchemaDifferences_GroupWithFewerThanTwoMembers_SaysSo_WithoutConnecting()
    {
        var a = Db("A", "CREATE TABLE users (id INTEGER PRIMARY KEY);");
        var group = Group("Solo", a);
        var tree = _fx.CreateTree(Config([a], group));

        await tree.CheckSchemaDifferencesCommand.ExecuteAsync(group);

        Assert.Empty(_fx.Dialogs.SchemaReports);
        Assert.Contains("at least 2", _fx.Status.Message);
        Assert.Null(tree.FindSession(a.Id));
    }

    [Fact]
    public async Task CheckSchemaDifferences_Cancelled_ShowsNoReport_AndIsNotAnError()
    {
        var a = Db("A", "CREATE TABLE users (id INTEGER PRIMARY KEY);");
        var b = Db("B", "CREATE TABLE users (id INTEGER PRIMARY KEY);");
        var group = Group("G", a, b);
        var tree = _fx.CreateTree(Config([a, b], group));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await tree.CheckSchemaDifferencesAsync(group, cts.Token);

        Assert.Empty(_fx.Dialogs.SchemaReports);
        Assert.False(_fx.Status.IsError);
        Assert.False(_fx.Status.IsBusy);
        Assert.Contains("cancel", _fx.Status.Message, StringComparison.OrdinalIgnoreCase);
    }
}
