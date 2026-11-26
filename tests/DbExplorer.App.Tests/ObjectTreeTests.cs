using System.Data.Common;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using static DbExplorer.App.Tests.TreeFixture;

namespace DbExplorer.App.Tests;

/// <summary>A server engine with routines, over a SQLite connection: canned routines and definitions.</summary>
internal sealed class RoutineDriver : IDbDriver
{
    private readonly IDbDriver _inner = new SqliteDriver();

    public List<TableInfo> Tables { get; } = [];
    public List<RoutineInfo> Routines { get; } = [];
    public int RoutineReads { get; private set; }
    public Exception? RoutineFailure { get; set; }
    public Func<RoutineInfo, CancellationToken, Task<string>> Definition { get; set; } =
        (r, _) => Task.FromResult($"CREATE {r.Kind} {r.Ref} ({r.Arguments})");

    public DbKind Kind => DbKind.Postgres;
    public bool HasRoutines => true;
    public string ParamPrefix => _inner.ParamPrefix;
    public string Quote(string identifier) => _inner.Quote(identifier);
    public string QuoteTable(TableRef t) => _inner.QuoteTable(t);
    public string CastToText(string quotedColumn) => _inner.CastToText(quotedColumn);
    public string EscapeLike(string text) => _inner.EscapeLike(text);
    public string PageSql(string selectSql, string orderBy, int offset, int limit) => _inner.PageSql(selectSql, orderBy, offset, limit);

    public DbConnection CreateConnection(ConnectionConfig cfg, string? plainPassword) =>
        new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");

    public Task<IReadOnlyList<TableInfo>> LoadSchemaAsync(DbConnection c, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TableInfo>>(Tables);

    public Task<IReadOnlyList<RoutineInfo>> LoadRoutinesAsync(DbConnection c, CancellationToken ct)
    {
        RoutineReads++;
        return RoutineFailure is { } e
            ? Task.FromException<IReadOnlyList<RoutineInfo>>(e)
            : Task.FromResult<IReadOnlyList<RoutineInfo>>(Routines.ToList());
    }

    public Task<string> LoadViewDefinitionAsync(DbConnection c, TableRef view, CancellationToken ct) =>
        Task.FromResult($"CREATE VIEW {view}");

    public Task<string> LoadRoutineDefinitionAsync(DbConnection c, RoutineInfo routine, CancellationToken ct) =>
        Definition(routine, ct);
}

public class ObjectTreeTests : IDisposable
{
    private readonly TreeFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static TableInfo Info(string? schema, string name, bool view) => new(new TableRef(schema, name), view, [], [], []);

    private static RoutineDriver Driver()
    {
        var driver = new RoutineDriver();
        driver.Tables.AddRange([Info("public", "users", false), Info("public", "v_users", true)]);
        driver.Routines.AddRange([
            new(new TableRef("public", "add_user"), RoutineKind.Procedure, "text", 1),
            new(new TableRef("public", "total"), RoutineKind.Function, "integer", 2),
            new(new TableRef("public", "total"), RoutineKind.Function, "text", 3),
            new(new TableRef("api", "ping"), RoutineKind.Function, "", 4),
        ]);
        return driver;
    }

    private static (ConnectionNodeViewModel Node, Session Session) Filled(RoutineDriver driver)
    {
        var config = Conn("PG", database: "db", kind: DbKind.Postgres);
        var session = new Session(config, driver, driver.Tables, null);
        var node = new ConnectionNodeViewModel(config);
        ConnectionTreeViewModel.ApplySchema(node, session);
        return (node, session);
    }

    private static T Group<T>(TreeNodeViewModel parent, ObjectGroupKind kind) where T : ObjectGroupNodeViewModel =>
        parent.Children.OfType<T>().Single(g => g.Kind == kind);

    [Fact]
    public void Schema_ShowsGroupsThenTables_AndReadsNoRoutine()
    {
        var driver = Driver();
        var (node, _) = Filled(driver);
        var schema = Assert.IsType<SchemaNodeViewModel>(Assert.Single(node.Children));

        schema.IsExpanded = true;

        Assert.Equal(["Views", "Procedures", "Functions", "users"], schema.Children.Select(c => c.Title));
        Assert.Equal(1, schema.TableCount);
        var views = Group<ViewGroupNodeViewModel>(schema, ObjectGroupKind.Views);
        Assert.True(views.HasUnrealizedChildren);
        views.IsExpanded = true;
        Assert.Equal("v_users", Assert.IsType<TableNodeViewModel>(Assert.Single(views.Children)).Title);
        Assert.Equal(0, driver.RoutineReads);
    }

    [Fact]
    public async Task RoutineGroups_LoadOnExpand_WithOneReadForAll_AndListOverloads()
    {
        var driver = Driver();
        var (node, _) = Filled(driver);
        var schema = node.Children[0];
        schema.IsExpanded = true;
        var functions = Group<RoutineGroupNodeViewModel>(schema, ObjectGroupKind.Functions);
        var procedures = Group<RoutineGroupNodeViewModel>(schema, ObjectGroupKind.Procedures);

        functions.IsExpanded = true;
        await functions.LoadAsync();
        await procedures.LoadAsync();

        Assert.Equal(["(integer)", "(text)"], functions.Children.Cast<RoutineNodeViewModel>().Select(r => r.Signature));
        Assert.Equal("(2)", functions.CountText);
        Assert.Equal("add_user", Assert.Single(procedures.Children).Title);
        Assert.Equal(1, driver.RoutineReads);
    }

    [Fact]
    public async Task RoutineGroup_Failure_ShowsAMessage_AndRetries()
    {
        var driver = Driver();
        driver.RoutineFailure = new InvalidOperationException("no right");
        var (node, _) = Filled(driver);
        node.Children[0].IsExpanded = true;
        var functions = Group<RoutineGroupNodeViewModel>(node.Children[0], ObjectGroupKind.Functions);

        await functions.LoadAsync();
        Assert.Contains("no right", Assert.IsType<MessageNodeViewModel>(Assert.Single(functions.Children)).Title);

        driver.RoutineFailure = null;
        await functions.LoadAsync();
        Assert.Equal(2, functions.Children.OfType<RoutineNodeViewModel>().Count());
    }

    [Fact]
    public async Task RoutineGroup_WithNothing_SaysSo()
    {
        var driver = Driver();
        driver.Routines.Clear();
        var (node, _) = Filled(driver);
        node.Children[0].IsExpanded = true;
        var procedures = Group<RoutineGroupNodeViewModel>(node.Children[0], ObjectGroupKind.Procedures);

        await procedures.LoadAsync();

        Assert.Equal("No procedures", Assert.Single(procedures.Children).Title);
    }

    [Fact]
    public async Task Background_AddsSchemasThatHoldOnlyRoutines_InOrder()
    {
        var driver = Driver();
        var config = Conn("PG", database: "db", kind: DbKind.Postgres);
        var sessions = new SessionManager(_fx.Store, _fx.Cache, _fx.Protector, _ => driver);
        var tree = new ConnectionTreeViewModel(_fx.Store, new AppConfig([], [config], new()), sessions, _fx.Status, _fx.Dialogs, _fx.DBeaverDir);
        var node = tree.NodeOf(config.Id)!;

        await tree.ConnectAsync(node);
        await tree.LoadRoutinesInBackgroundAsync(node, sessions.Find(config.Id)!);

        Assert.Equal(["api", "public"], node.Children.Select(c => c.Title));
        Assert.Equal(4, _fx.Cache.LoadRoutines(config.Id).Count);
        node.Children[0].IsExpanded = true;
        Assert.Equal(["Procedures", "Functions"], node.Children[0].Children.Select(c => c.Title));
    }

    [Fact]
    public async Task Filter_FindsViews_AndLoadedRoutines_ButNotGroupTitles()
    {
        var driver = Driver();
        var config = Conn("PG", database: "db", kind: DbKind.Postgres);
        var sessions = new SessionManager(_fx.Store, _fx.Cache, _fx.Protector, _ => driver);
        var tree = new ConnectionTreeViewModel(_fx.Store, new AppConfig([], [config], new()), sessions, _fx.Status, _fx.Dialogs, _fx.DBeaverDir);
        var node = tree.NodeOf(config.Id)!;
        await tree.ConnectAsync(node);
        var schema = node.Children.Single(c => c.Title == "public");
        schema.IsExpanded = true;
        await Group<RoutineGroupNodeViewModel>(schema, ObjectGroupKind.Procedures).LoadAsync();

        tree.FilterText = "v_us";
        var views = Group<ViewGroupNodeViewModel>(schema, ObjectGroupKind.Views);
        Assert.True(views is { IsShown: true, IsExpanded: true });
        Assert.True(Assert.Single(views.Children).IsShown);

        tree.FilterText = "add_u";
        Assert.True(Group<RoutineGroupNodeViewModel>(schema, ObjectGroupKind.Procedures) is { IsShown: true, IsExpanded: true });

        tree.FilterText = "proc";
        Assert.True(tree.NoMatch);
    }

    [Fact]
    public async Task OpenDefinition_OpensAScriptTab_NotDirty_NotKeptInTheSession()
    {
        var driver = Driver();
        var tree = _fx.CreateTree(new AppConfig([], [Conn("PG", database: "db", kind: DbKind.Postgres)], new()));
        var main = new MainViewModel(tree, _fx.Status, _fx.Cache);
        var (node, _) = Filled(driver);
        node.Children[0].IsExpanded = true;
        var functions = Group<RoutineGroupNodeViewModel>(node.Children[0], ObjectGroupKind.Functions);
        await functions.LoadAsync();

        await tree.OpenDefinitionAsync(functions.Children[0]);
        await tree.OpenDefinitionAsync(functions.Children[0]);   // the same text: its tab comes back

        var tab = Assert.IsType<ScriptTabViewModel>(Assert.Single(main.Tabs));
        Assert.Equal("CREATE Function public.total (integer)", tab.Text);
        Assert.Equal("public.total · PG", tab.Title);
        Assert.False(tab.HasUnsavedWork);
        Assert.True(tab.IsUnmodifiedDefinition);
        Assert.False(tab.NeedsCloseConfirmation);

        tab.Text += " -- edited";
        Assert.True(tab.HasUnsavedWork);
        Assert.False(tab.IsUnmodifiedDefinition);
    }

    [Fact]
    public async Task OpenDefinition_Unavailable_ReportsTheReason_AndOpensNothing()
    {
        var driver = Driver();
        driver.Definition = (r, _) => throw new DefinitionUnavailableException($"{r.Ref} is encrypted.");
        var tree = _fx.CreateTree(new AppConfig([], [], new()));
        var opened = 0;
        tree.DefinitionOpener = (_, _, _) => opened++;
        var (_, session) = Filled(driver);

        await tree.OpenRoutineDefinitionAsync(session, driver.Routines[0]);

        Assert.Equal(0, opened);
        Assert.True(_fx.Status.IsError);
        Assert.Equal("public.add_user is encrypted.", _fx.Status.Message);
        Assert.False(_fx.Status.IsBusy);
    }

    [Fact]
    public async Task OpenDefinition_SessionClosedMeanwhile_IsCancelledWithoutError()
    {
        var driver = Driver();
        var reading = new TaskCompletionSource();
        driver.Definition = async (_, ct) =>
        {
            reading.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        };
        var tree = _fx.CreateTree(new AppConfig([], [], new()));
        tree.DefinitionOpener = (_, _, _) => { };
        var (_, session) = Filled(driver);

        var opening = tree.OpenRoutineDefinitionAsync(session, driver.Routines[0]);
        await reading.Task;
        session.Dispose();
        await opening;

        Assert.False(_fx.Status.IsError);
        Assert.Contains("cancelled", _fx.Status.Message);
    }

    [Fact]
    public async Task Sqlite_ViewDefinition_OpensFromTheTree_AndTheViewStillOpensAsData()
    {
        var config = Conn("Lite", database: _fx.CreateSqliteFile());
        var tree = _fx.CreateTree(new AppConfig([], [config], new()));
        string? sql = null;
        tree.DefinitionOpener = (_, _, text) => sql = text;
        TableRef? opened = null;
        tree.TableOpener = (_, table) =>
        {
            opened = table;
            return Task.CompletedTask;
        };
        var node = tree.NodeOf(config.Id)!;
        await tree.ConnectAsync(node);
        var views = Group<ViewGroupNodeViewModel>(node, ObjectGroupKind.Views);
        views.IsExpanded = true;
        var view = Assert.IsType<TableNodeViewModel>(Assert.Single(views.Children));

        await tree.OpenDefinitionAsync(view);
        await tree.OpenTableAsync(view);

        Assert.Equal("CREATE VIEW v_users AS SELECT * FROM users", sql);
        Assert.Equal(new TableRef(null, "v_users"), opened);
        Assert.DoesNotContain(node.Children, c => c is RoutineGroupNodeViewModel);
    }

    [Fact]
    public async Task RevealTab_FindsAViewInItsGroup()
    {
        var config = Conn("Lite", database: _fx.CreateSqliteFile());
        var tree = _fx.CreateTree(new AppConfig([], [config], new()));
        var main = new MainViewModel(tree, _fx.Status, _fx.Cache);
        main.UseTableTabs(_fx.Sessions);
        var node = tree.NodeOf(config.Id)!;
        await tree.ConnectAsync(node);
        await main.OpenTable(tree.FindSession(config.Id)!, new TableRef(null, "v_users"));

        Assert.True(tree.RevealTab(main.ActiveTab));

        Assert.Equal("v_users", Assert.IsType<TableNodeViewModel>(tree.SelectedNode).Title);
        Assert.True(Group<ViewGroupNodeViewModel>(node, ObjectGroupKind.Views).IsExpanded);
    }

    [Fact]
    public void MoveToFolder_FolderKnownOnlyThroughConnections_IsAccepted()
    {
        var loose = Conn("Loose");
        var tree = _fx.CreateTree(new AppConfig([], [Conn("Orphan", "Ghost"), loose], new()));

        tree.MoveToFolder(loose, "ghost");

        Assert.False(_fx.Status.IsError);
        Assert.Equal(["Ghost"], _fx.Store.Load().Folders);
        Assert.All(_fx.Store.Load().Connections, c => Assert.Equal("Ghost", c.Folder));
    }
}
