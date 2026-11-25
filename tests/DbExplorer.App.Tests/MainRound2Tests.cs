using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;
using Microsoft.Data.Sqlite;

namespace DbExplorer.App.Tests;

/// <summary>Navigator width and toggle, folder connected count, quick-open scope switch, recent tabs, dismissable banner.</summary>
public class MainRound2Tests : IDisposable
{
    private readonly TreeFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private string CreateDb(string name, string sql)
    {
        var file = Path.Combine(_fx.Dir, name);
        using var c = new SqliteConnection($"Data Source={file};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        return file;
    }

    private MainViewModel NewMain(params ConnectionConfig[] configs) =>
        new(_fx.CreateTree(new AppConfig([], configs.ToList(), new())), _fx.Status, _fx.Cache);

    // ---- window state ----

    [Fact]
    public void WindowState_NavWidth_RoundTrips_AndOldFilesStillLoad()
    {
        var file = Path.Combine(_fx.Dir, "window.json");
        WindowStateStore.TrySave(file, new WindowPlacement(1, 2, 900, 600, false, 333.5));
        Assert.Equal(333.5, WindowStateStore.TryLoad(file)!.NavWidth);

        File.WriteAllText(file, """{"width":900,"height":600,"maximized":false}""");   // a file written before the navigator width existed
        var old = WindowStateStore.TryLoad(file)!;
        Assert.Null(old.NavWidth);
        Assert.Equal(900, old.Width);

        File.WriteAllText(file, """{"width":900,"height":600,"maximized":false,"navWidth":-5}""");
        Assert.Null(WindowStateStore.TryLoad(file)!.NavWidth);   // nonsense is ignored, the window still restores

        Assert.Equal(250, WindowStateStore.Fit(new WindowPlacement(null, null, 900, 600, false, 250), [new ScreenArea(0, 0, 1920, 1040, 1.0)], 720, 420).NavWidth);
    }

    [Fact]
    public void Main_CtrlB_TogglesTheNavigator()
    {
        var main = NewMain();
        Assert.True(main.IsNavigatorVisible);

        Assert.True(Views.MainShortcuts.Handle(main, Avalonia.Input.Key.B, Avalonia.Input.KeyModifiers.Control));
        Assert.False(main.IsNavigatorVisible);
        main.ToggleNavigatorCommand.Execute(null);
        Assert.True(main.IsNavigatorVisible);
    }

    [Fact]
    public void Main_DismissBanner_ClearsIt()
    {
        var main = NewMain();
        main.Banner = "The config could not be read.";

        main.DismissBannerCommand.Execute(null);

        Assert.Null(main.Banner);
    }

    // ---- folder row ----

    [Fact]
    public async Task Folder_CountText_ShowsConnectedOnlyWhenCollapsed()
    {
        var shop = TreeFixture.Conn("Shop", "Prod", CreateDb("shop.db", "CREATE TABLE t (id INTEGER)"));
        var other = TreeFixture.Conn("Other", "Prod");
        var tree = _fx.CreateTree(new AppConfig(["Prod"], [shop, other], new()));
        var folder = tree.Nodes.OfType<FolderNodeViewModel>().Single();
        Assert.Equal("(2)", folder.CountText);

        await tree.ConnectAsync(tree.NodeOf(shop.Id)!);

        Assert.Equal(1, folder.ConnectedCount);
        folder.IsExpanded = false;
        Assert.Equal("(2 · 1 connected)", folder.CountText);
        folder.IsExpanded = true;
        Assert.Equal("(2)", folder.CountText);

        tree.Disconnect(shop.Id);
        folder.IsExpanded = false;
        Assert.Equal(0, folder.ConnectedCount);
        Assert.Equal("(2)", folder.CountText);
    }

    // ---- quick open scope ----

    [Fact]
    public async Task QuickOpen_Tab_SwapsBetweenThisConnectionAndAll_KeepingTheQuery()
    {
        var a = TreeFixture.Conn("Alpha", database: CreateDb("a.db", "CREATE TABLE orders (id INTEGER)"));
        var b = TreeFixture.Conn("Beta", database: CreateDb("b.db", "CREATE TABLE orders_b (id INTEGER); CREATE TABLE invoices (id INTEGER)"));
        var main = NewMain(a, b);
        var sa = await _fx.Sessions.GetOrOpenAsync(a, CancellationToken.None);
        var sb = await _fx.Sessions.GetOrOpenAsync(b, CancellationToken.None);
        _fx.Cache.Save(a.Id, sa.Schema);
        _fx.Cache.Save(b.Id, sb.Schema);
        await main.OpenTable(sa, new TableRef(null, "orders"));

        main.ShowTablePicker();
        var picker = main.QuickOpen!;
        picker.Query = "ord";
        Assert.StartsWith("Go to table in Alpha", picker.Title);
        Assert.Single(picker.Results);
        Assert.True(picker.HasScopes);
        Assert.Equal("Search all connections", picker.ScopeHint);

        picker.SwitchScope();

        Assert.Same(picker, main.QuickOpen);   // not closed
        Assert.Equal("ord", picker.Query);
        Assert.Equal("Go to table in any connection", picker.Title);
        Assert.Equal(2, picker.Results.Count);   // Alpha's orders and Beta's orders_b
        Assert.Equal("Search this connection only", picker.ScopeHint);

        picker.SwitchScope();
        Assert.StartsWith("Go to table in Alpha", picker.Title);
        Assert.Single(picker.Results);
    }

    [Fact]
    public void QuickOpen_WithoutAnotherScope_TabDoesNothing()
    {
        var vm = new QuickOpenViewModel("Go to column", [new QuickOpenItem("id", null, () => Task.CompletedTask)]);

        vm.SwitchScope();

        Assert.False(vm.HasScopes);
        Assert.Equal("Go to column", vm.Title);
        Assert.Single(vm.Results);
    }

    // ---- recent tabs ----

    [Fact]
    public async Task RecentTabs_ListTheClosedTabsNewestFirst_AndReopenTheChosenOne()
    {
        var shop = TreeFixture.Conn("Shop", database: CreateDb("shop.db", "CREATE TABLE a (id INTEGER); CREATE TABLE b (id INTEGER); CREATE TABLE c (id INTEGER)"));
        var main = NewMain(shop);
        main.UseTableTabs(_fx.Sessions);
        var session = await _fx.Sessions.GetOrOpenAsync(shop, CancellationToken.None);
        foreach (var name in new[] { "a", "b", "c" }) await main.OpenTable(session, new TableRef(null, name));
        Assert.False(main.HasRecentTabs);

        await main.CloseAllTabsCommand.ExecuteAsync(null);

        Assert.True(main.HasRecentTabs);
        Assert.Equal(3, main.RecentTabs.Count);
        Assert.Equal("Shop · c", main.RecentTabs[0].Title);   // closed last: first in the list
        Assert.Equal("Shop · a", main.RecentTabs[2].Title);

        await main.ReopenRecentCommand.ExecuteAsync(main.RecentTabs[2]);

        Assert.Equal("Shop · a", main.ActiveTab!.Title);
        Assert.Equal(2, main.RecentTabs.Count);
        Assert.DoesNotContain(main.RecentTabs, r => r.Title == "Shop · a");
    }

    [Fact]
    public async Task RecentTabs_ShowAtMostFive()
    {
        var shop = TreeFixture.Conn("Shop", database: CreateDb("shop.db", "CREATE TABLE t (id INTEGER)"));
        var main = NewMain(shop);
        main.UseTableTabs(_fx.Sessions);
        var session = await _fx.Sessions.GetOrOpenAsync(shop, CancellationToken.None);
        for (var i = 0; i < 8; i++) await main.OpenTable(session, new TableRef(null, "t"));

        await main.CloseAllTabsCommand.ExecuteAsync(null);

        Assert.Equal(MainViewModel.MaxRecentShown, main.RecentTabs.Count);
    }
}
