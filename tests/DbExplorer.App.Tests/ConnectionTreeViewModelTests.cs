using DbExplorer.App.ViewModels;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using static DbExplorer.App.Tests.TreeFixture;

namespace DbExplorer.App.Tests;

public class ConnectionTreeViewModelTests : IDisposable
{
    private readonly TreeFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static AppConfig Config(string[] folders, params ConnectionConfig[] connections) =>
        new(folders.ToList(), connections.ToList(), new());

    private static ConnectionNodeViewModel NodeOf(ConnectionTreeViewModel tree, string name) =>
        tree.Nodes.OfType<ConnectionNodeViewModel>()
            .Concat(tree.Nodes.OfType<FolderNodeViewModel>().SelectMany(f => f.Children.OfType<ConnectionNodeViewModel>()))
            .Single(n => n.Config.Name == name);

    [Fact]
    public void Tree_GroupsConnectionsByFolder_AndPutsUnfolderedAtRoot()
    {
        var config = Config(["Admin", "Company", "Empty"],
            Conn("Admin PG", "Admin"), Conn("Company local", "Company"), Conn("Company dev", "Company"), Conn("Loose"));

        var tree = _fx.CreateTree(config);

        // Folders first in config order, then unfoldered connections.
        Assert.Equal(["Admin", "Company", "Empty", "Loose"], tree.Nodes.Select(n => n.Title));
        var folders = tree.Nodes.OfType<FolderNodeViewModel>().ToList();
        Assert.Equal(["Admin PG"], folders[0].Children.Select(n => n.Title));
        Assert.Equal(["Company dev", "Company local"], folders[1].Children.Select(n => n.Title)); // sorted by name
        Assert.Empty(folders[2].Children);
        Assert.IsType<ConnectionNodeViewModel>(tree.Nodes[3]);
    }

    [Fact]
    public void Tree_ConnectionWithUnknownFolder_StillAppearsInThatFolder()
    {
        var tree = _fx.CreateTree(Config([], Conn("Orphan", "Ghost")));

        var folder = Assert.IsType<FolderNodeViewModel>(Assert.Single(tree.Nodes));
        Assert.Equal("Ghost", folder.Name);
        Assert.Equal("Orphan", Assert.Single(folder.Children).Title);
    }

    [Fact]
    public void MoveToFolder_Persists()
    {
        var c = Conn("Local");
        var tree = _fx.CreateTree(Config(["Company"], c));

        tree.MoveToFolder(c, "Company");

        Assert.Equal("Company", Assert.Single(_fx.Store.Load().Connections).Folder);
        var folder = Assert.IsType<FolderNodeViewModel>(Assert.Single(tree.Nodes));
        Assert.Equal("Local", Assert.Single(folder.Children).Title);

        tree.MoveToFolder(c, null);

        Assert.Null(Assert.Single(_fx.Store.Load().Connections).Folder);
        Assert.IsType<ConnectionNodeViewModel>(tree.Nodes.Last());
    }

    [Fact]
    public void MoveToFolder_UnknownFolder_IsRejectedWithStatusError()
    {
        var c = Conn("Local");
        var tree = _fx.CreateTree(Config(["Company"], c));

        tree.MoveToFolder(c, "Nope");

        Assert.True(_fx.Status.IsError);
        Assert.Null(tree.Config.Connections.Single().Folder);
    }

    [Fact]
    public void DeleteFolder_MovesConnectionsToRoot()
    {
        var tree = _fx.CreateTree(Config(["Company", "Other"], Conn("A", "Company"), Conn("B", "Company"), Conn("C", "Other")));

        tree.DeleteFolder("Company");

        var saved = _fx.Store.Load();
        Assert.Equal(["Other"], saved.Folders);
        Assert.All(saved.Connections.Where(c => c.Name is "A" or "B"), c => Assert.Null(c.Folder));
        Assert.Equal("Other", saved.Connections.Single(c => c.Name == "C").Folder);
        Assert.Equal(3, tree.Config.Connections.Count);
        Assert.Contains(tree.Nodes, n => n.Title == "A" && n is ConnectionNodeViewModel);
        Assert.DoesNotContain(tree.Nodes, n => n is FolderNodeViewModel { Name: "Company" });
    }

    [Fact]
    public void AddFolder_RejectsEmptyAndDuplicateNames()
    {
        var tree = _fx.CreateTree(Config(["Company"]));

        Assert.False(tree.AddFolder("  "));
        Assert.False(tree.AddFolder("company"));
        Assert.True(tree.AddFolder(" Keeple "));

        Assert.Equal(["Company", "Keeple"], _fx.Store.Load().Folders);
    }

    [Fact]
    public void RenameFolder_UpdatesFolderAndItsConnections()
    {
        var tree = _fx.CreateTree(Config(["Company"], Conn("A", "Company")));

        Assert.True(tree.RenameFolder("Company", "Work"));

        var saved = _fx.Store.Load();
        Assert.Equal(["Work"], saved.Folders);
        Assert.Equal("Work", saved.Connections.Single().Folder);
        Assert.Equal("Work", Assert.Single(tree.Nodes).Title);
    }

    [Fact]
    public async Task Import_AddsConnectionsAndRefreshesTree()
    {
        Directory.CreateDirectory(_fx.DBeaverDir);
        File.WriteAllText(Path.Combine(_fx.DBeaverDir, "data-sources.json"), """
            {
              "folders": { "Admin": {} },
              "connections": {
                "pg-1": { "provider": "postgresql", "name": "Admin PG", "folder": "Admin",
                          "configuration": { "host": "db", "port": "5432", "database": "admin" } },
                "lite-1": { "provider": "sqlite", "name": "Local lite",
                            "configuration": { "database": "C:\\data\\x.db" } }
              }
            }
            """);
        var tree = _fx.CreateTree(Config([], Conn("Existing")));

        await tree.ImportFromDBeaverAsync();

        Assert.Equal(["Admin PG", "Existing", "Local lite"], tree.Config.Connections.Select(c => c.Name).Order());
        var folder = Assert.IsType<FolderNodeViewModel>(tree.Nodes[0]);
        Assert.Equal("Admin", folder.Name);
        Assert.Equal("Admin PG", Assert.Single(folder.Children).Title);
        Assert.Contains(tree.Nodes, n => n.Title == "Local lite");
        Assert.Equal(3, _fx.Store.Load().Connections.Count); // persisted
        Assert.False(_fx.Status.IsError);
        Assert.Contains("2", _fx.Status.Message);
        // credentials-config.json is absent: the importer warning is surfaced.
        Assert.Contains("credentials-config.json", _fx.Status.Message);
    }

    [Fact]
    public async Task Import_SecondRun_SkipsExistingConnections()
    {
        Directory.CreateDirectory(_fx.DBeaverDir);
        File.WriteAllText(Path.Combine(_fx.DBeaverDir, "data-sources.json"),
            """{ "connections": { "a": { "provider": "sqlite", "name": "A", "configuration": { "database": "a.db" } } } }""");
        var tree = _fx.CreateTree(Config([]));

        await tree.ImportFromDBeaverAsync();
        await tree.ImportFromDBeaverAsync();

        Assert.Single(tree.Config.Connections);
        Assert.Contains("skipped", _fx.Status.Message);
    }

    [Fact]
    public async Task Import_MalformedFile_ShowsErrorWithoutThrowingAndLeavesConfigUntouched()
    {
        Directory.CreateDirectory(_fx.DBeaverDir);
        File.WriteAllText(Path.Combine(_fx.DBeaverDir, "data-sources.json"), "{ not json");
        var existing = Conn("Existing");
        var tree = _fx.CreateTree(Config([], existing));

        var ex = Record.Exception(() => tree.ImportFromDBeaverAsync().GetAwaiter().GetResult());

        Assert.Null(ex);
        Assert.True(_fx.Status.IsError);
        Assert.Contains("import", _fx.Status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([existing], tree.Config.Connections);
    }

    [Fact]
    public async Task Import_MissingDirectory_ShowsWarning()
    {
        var tree = _fx.CreateTree(Config([]));

        await tree.ImportFromDBeaverAsync();

        Assert.Contains("data-sources.json not found", _fx.Status.Message);
        Assert.Empty(tree.Config.Connections);
    }

    [Fact]
    public async Task Connect_Failure_SetsErrorStateWithoutThrowing()
    {
        var c = Conn("Broken", database: Path.Combine(_fx.Dir, "missing.db"));
        var tree = _fx.CreateTree(Config([], c));
        var node = NodeOf(tree, "Broken");

        await tree.ConnectAsync(node);

        Assert.Equal(ConnectionState.Failed, node.State);
        Assert.False(string.IsNullOrWhiteSpace(node.ErrorMessage));
        Assert.True(_fx.Status.IsError);
        Assert.Contains("Broken", _fx.Status.Message);
        Assert.Empty(node.Children);
        Assert.False(_fx.Status.IsBusy);
    }

    [Fact]
    public async Task Connect_Sqlite_ShowsTablesAndViewsDirectlyUnderConnection()
    {
        var c = Conn("Lite", database: _fx.CreateSqliteFile());
        var tree = _fx.CreateTree(Config([], c));
        var node = NodeOf(tree, "Lite");

        await tree.ConnectAsync(node);

        Assert.Equal(ConnectionState.Connected, node.State);
        Assert.True(node.IsExpanded);
        Assert.Equal(["Views", "users"], node.Children.Select(n => n.Title));
        Assert.False(Assert.IsType<TableNodeViewModel>(node.Children[1]).IsView);
        node.Children[0].IsExpanded = true;
        Assert.True(Assert.IsType<TableNodeViewModel>(Assert.Single(node.Children[0].Children)).IsView);
        Assert.False(_fx.Status.IsBusy);
    }

    [Fact]
    public async Task Connect_Cancelled_ReturnsToDisconnectedWithoutError()
    {
        var c = Conn("Lite", database: _fx.CreateSqliteFile());
        var tree = _fx.CreateTree(Config([], c));
        var node = NodeOf(tree, "Lite");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await tree.ConnectAsync(node, cts.Token);

        Assert.Equal(ConnectionState.Disconnected, node.State);
        Assert.False(_fx.Status.IsError);
    }

    [Fact]
    public void ApplySchema_Postgres_GroupsTablesUnderSchemaNodes_CreatedOnFirstExpand()
    {
        var schema = new[]
        {
            Table("public", "users", false), Table("public", "v_users", true), Table("audit", "log", false),
        };
        var cfg = Conn("PG", database: "db", kind: DbKind.Postgres);
        var session = new Session(cfg, DriverFactory.For(DbKind.Postgres), schema, null);
        var node = new ConnectionNodeViewModel(cfg);

        ConnectionTreeViewModel.ApplySchema(node, session);

        Assert.Equal(["audit", "public"], node.Children.Select(n => n.Title));
        var pub = Assert.IsType<SchemaNodeViewModel>(node.Children[1]);
        Assert.IsType<PlaceholderNodeViewModel>(Assert.Single(pub.Children)); // not created yet
        pub.IsExpanded = true;
        Assert.Equal(["Views", "Procedures", "Functions", "users"], pub.Children.Select(n => n.Title));
    }

    [Fact]
    public void ApplySchema_3000Tables_CreatesNoTableNodesUntilExpanded_AndIsFast()
    {
        var schema = Enumerable.Range(0, 3000).Select(i => Table("big", $"t{i:0000}", false))
            .Concat(Enumerable.Range(0, 5).Select(i => Table("small", $"s{i}", false))).ToList();
        var cfg = Conn("PG", database: "db", kind: DbKind.Postgres);
        var session = new Session(cfg, DriverFactory.For(DbKind.Postgres), schema, null);
        var node = new ConnectionNodeViewModel(cfg);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        ConnectionTreeViewModel.ApplySchema(node, session);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500, $"took {sw.ElapsedMilliseconds} ms");
        var big = Assert.IsType<SchemaNodeViewModel>(node.Children[0]);
        Assert.Equal(3000, big.TableCount);
        Assert.True(big.HasUnrealizedChildren);
        Assert.IsType<PlaceholderNodeViewModel>(Assert.Single(big.Children));
        Assert.False(big.IsExpanded); // never auto-expanded
        big.IsExpanded = true;
        Assert.Equal(3000, big.Children.OfType<TableNodeViewModel>().Count());
        Assert.False(big.HasUnrealizedChildren);
    }

    [Fact]
    public void ApplySchema_SqliteWithManyTables_StaysCollapsedUntilTheUserExpands()
    {
        var schema = Enumerable.Range(0, ConnectionTreeViewModel.AutoExpandLimit + 1).Select(i => Table(null, $"t{i}", false)).ToList();
        var cfg = Conn("Lite");
        var node = new ConnectionNodeViewModel(cfg);

        ConnectionTreeViewModel.ApplySchema(node, new Session(cfg, DriverFactory.For(DbKind.Sqlite), schema, null));

        Assert.False(node.IsExpanded);
        Assert.IsType<PlaceholderNodeViewModel>(Assert.Single(node.Children));
        node.IsExpanded = true;
        Assert.Equal(schema.Count, node.Children.OfType<TableNodeViewModel>().Count());
    }

    [Fact]
    public void ApplySchema_SqliteWithFewTables_ExpandsImmediately()
    {
        var cfg = Conn("Lite");
        var node = new ConnectionNodeViewModel(cfg);

        ConnectionTreeViewModel.ApplySchema(node, new Session(cfg, DriverFactory.For(DbKind.Sqlite), [Table(null, "a", false)], null));

        Assert.True(node.IsExpanded);
        Assert.IsType<TableNodeViewModel>(Assert.Single(node.Children));
    }

    [Fact]
    public async Task OpenTable_WhenTableOpenerThrows_ReportsToStatusBarWithoutThrowing()
    {
        var c = Conn("Lite", database: _fx.CreateSqliteFile());
        var tree = _fx.CreateTree(Config([], c));
        tree.TableOpener = (_, _) => throw new InvalidOperationException("tab failed");
        var node = NodeOf(tree, "Lite");
        await tree.ConnectAsync(node);

        await tree.OpenTableAsync(node.Children.OfType<TableNodeViewModel>().First());

        Assert.True(_fx.Status.IsError);
        Assert.Contains("tab failed", _fx.Status.Message);
        Assert.Contains("users", _fx.Status.Message); // names the table
    }

    [Fact]
    public void ReadOnlyConfigMode_MutationsAreNotWritten_AndStatusSaysSo()
    {
        var tree = new ConnectionTreeViewModel(_fx.Store, Config([]), _fx.Sessions, _fx.Status, _fx.Dialogs, _fx.DBeaverDir, persistenceEnabled: false);

        Assert.True(tree.AddFolder("Company"));

        Assert.False(File.Exists(_fx.ConfigPath));
        Assert.Equal(["Company"], tree.Config.Folders); // still usable in memory
        Assert.True(_fx.Status.IsError);
        Assert.Contains("NOT saved", _fx.Status.Message);
    }
    private static TableInfo Table(string? schema, string name, bool isView) =>
        new(new TableRef(schema, name), isView, [], [], []);

    [Fact]
    public async Task ToggleReadOnly_PersistsAndUpdatesOpenSession()
    {
        var c = Conn("Lite", database: _fx.CreateSqliteFile());
        var tree = _fx.CreateTree(Config([], c));
        var node = NodeOf(tree, "Lite");
        await tree.ConnectAsync(node);
        var session = _fx.Sessions.Find(c.Id)!;

        tree.ToggleReadOnly(node);

        Assert.True(_fx.Store.Load().Connections.Single().ReadOnly);
        Assert.True(node.IsReadOnly);
        Assert.True(session.Config.ReadOnly);
        Assert.Equal(ConnectionState.Connected, node.State);

        tree.ToggleReadOnly(node);
        Assert.False(_fx.Store.Load().Connections.Single().ReadOnly);
    }

    [Fact]
    public async Task UpdateConnection_ChangingTarget_ClosesSession()
    {
        var c = Conn("Lite", database: _fx.CreateSqliteFile());
        var tree = _fx.CreateTree(Config([], c));
        var node = NodeOf(tree, "Lite");
        await tree.ConnectAsync(node);

        tree.UpdateConnection(c with { Database = _fx.CreateSqliteFile("other.db") });

        Assert.Empty(_fx.Sessions.Open);
        Assert.Equal(ConnectionState.Disconnected, node.State);
        Assert.Empty(node.Children);
    }

    [Fact]
    public async Task DeleteConnection_RemovesSessionCacheAndConfigEntry()
    {
        var c = Conn("Lite", database: _fx.CreateSqliteFile());
        var tree = _fx.CreateTree(Config([], c));
        await tree.ConnectAsync(NodeOf(tree, "Lite"));
        Assert.NotEmpty(_fx.Cache.Load(c.Id));

        tree.DeleteConnection(c);

        Assert.Empty(tree.Nodes);
        Assert.Empty(_fx.Store.Load().Connections);
        Assert.Empty(_fx.Sessions.Open);
        Assert.Empty(_fx.Cache.Load(c.Id));
    }

    [Fact]
    public async Task AddConnectionCommand_UsesDialogResult_AndPersists()
    {
        var tree = _fx.CreateTree(Config(["Company"]));
        _fx.Dialogs.OnConnectionDialog = vm =>
        {
            vm.Name = "New one";
            vm.Kind = DbKind.Sqlite;
            vm.Database = "new.db";
            vm.Folder = "Company";
        };

        await tree.AddConnectionCommand.ExecuteAsync(null);

        var saved = Assert.Single(_fx.Store.Load().Connections);
        Assert.Equal("New one", saved.Name);
        Assert.Equal("Company", saved.Folder);
        var folder = Assert.IsType<FolderNodeViewModel>(Assert.Single(tree.Nodes));
        Assert.Equal("New one", Assert.Single(folder.Children).Title);
    }

    [Fact]
    public async Task AddConnectionCommand_DialogCancelled_ChangesNothing()
    {
        var tree = _fx.CreateTree(Config([]));
        _fx.Dialogs.ConnectionDialogResult = false;

        await tree.AddConnectionCommand.ExecuteAsync(null);

        Assert.Empty(tree.Config.Connections);
        Assert.False(File.Exists(_fx.ConfigPath));
    }

    [Fact]
    public async Task DeleteFolderCommand_AsksConfirmation()
    {
        var tree = _fx.CreateTree(Config(["Company"], Conn("A", "Company")));
        _fx.Dialogs.ConfirmResult = false;

        await tree.DeleteFolderCommand.ExecuteAsync(tree.Nodes.OfType<FolderNodeViewModel>().Single());

        Assert.Equal(["Company"], tree.Config.Folders);
    }

    [Fact]
    public async Task OpenTableCommand_CallsTableOpener()
    {
        var c = Conn("Lite", database: _fx.CreateSqliteFile());
        var tree = _fx.CreateTree(Config([], c));
        var opened = new List<(Session, TableRef)>();
        tree.TableOpener = (s, t) =>
        {
            opened.Add((s, t));
            return Task.CompletedTask;
        };
        var node = NodeOf(tree, "Lite");
        await tree.ConnectAsync(node);

        await tree.OpenTableCommand.ExecuteAsync(node.Children.OfType<TableNodeViewModel>().First());

        var (session, table) = Assert.Single(opened);
        Assert.Equal(c.Id, session.Config.Id);
        Assert.Equal(new TableRef(null, "users"), table);
    }
}



