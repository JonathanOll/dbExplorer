using DbExplorer.App.ViewModels;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.Tests;

public class MainViewModelTests : IDisposable
{
    private readonly TreeFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private MainViewModel NewMain() => new(_fx.CreateTree(AppConfig.Empty()), _fx.Status);

    private static Session SessionFor(string name) =>
        new(TreeFixture.Conn(name), DriverFactory.For(DbKind.Sqlite), [], null);

    [Fact]
    public async Task OpenTable_AddsTabAndMakesItActive()
    {
        var main = NewMain();
        var s = SessionFor("Lite");

        await main.OpenTable(s, new TableRef(null, "users"));

        var tab = Assert.Single(main.Tabs);
        Assert.Same(tab, main.ActiveTab);
        Assert.Equal("Lite · users", tab.Title);
        Assert.Equal(s.Config, tab.Connection);
        Assert.Matches("^#[0-9A-F]{6}$", tab.ChipColor);
    }

    [Fact]
    public async Task OpenTable_UsesTableTabFactory()
    {
        var main = NewMain();
        main.TableTabFactory = (s, t) => new PlaceholderTabViewModel(s.Config, t with { Name = "custom" });

        await main.OpenTable(SessionFor("Lite"), new TableRef(null, "users"));

        Assert.Equal("Lite · custom", main.Tabs.Single().Title);
    }

    [Fact]
    public async Task TableOpenedFromTree_GoesThroughMainViewModel()
    {
        var path = _fx.CreateSqliteFile();
        var c = TreeFixture.Conn("Lite", database: path);
        var tree = _fx.CreateTree(new AppConfig([], [c], new()));
        var main = new MainViewModel(tree, _fx.Status);
        var node = tree.Nodes.OfType<ConnectionNodeViewModel>().Single();
        await tree.ConnectAsync(node);

        await tree.OpenTableCommand.ExecuteAsync(node.Children.OfType<TableNodeViewModel>().First());

        Assert.Equal("Lite · users", Assert.Single(main.Tabs).Title);
    }

    [Fact]
    public async Task CloseTab_ActivatesNeighbour_AndClearsActiveWhenLast()
    {
        var main = NewMain();
        await main.OpenTable(SessionFor("A"), new TableRef(null, "t1"));
        await main.OpenTable(SessionFor("B"), new TableRef(null, "t2"));
        await main.OpenTable(SessionFor("C"), new TableRef(null, "t3"));
        var (first, second, third) = (main.Tabs[0], main.Tabs[1], main.Tabs[2]);

        main.CloseTabCommand.Execute(third);
        Assert.Same(second, main.ActiveTab);

        main.ActiveTab = first;
        main.CloseTabCommand.Execute(second); // not active: selection stays
        Assert.Same(first, main.ActiveTab);

        main.CloseTabCommand.Execute(first);
        Assert.Null(main.ActiveTab);
        Assert.Empty(main.Tabs);
    }
}
