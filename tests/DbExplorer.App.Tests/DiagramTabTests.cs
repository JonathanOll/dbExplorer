using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Diagrams;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using Xunit;

namespace DbExplorer.App.Tests;

public sealed class DiagramTabViewModelTests : IDisposable
{
    private readonly TreeFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private const string Schema = """
        CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT);
        CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER REFERENCES customers(id), total REAL);
        CREATE TABLE lines (id INTEGER PRIMARY KEY, order_id INTEGER REFERENCES orders(id));
        CREATE TABLE settings (key TEXT);
        """;

    private (MainViewModel Main, ConnectionTreeViewModel Tree, ConnectionConfig Config) Shell()
    {
        var config = TreeFixture.Conn("Shop", database: _fx.CreateSqliteFileWith("shop.db", Schema));
        var tree = _fx.CreateTree(new AppConfig([], [config], new()));
        return (new MainViewModel(tree, _fx.Status, _fx.Cache), tree, config);
    }

    private static string[] Names(DiagramTabViewModel tab) => tab.Scene!.Model.Tables.Select(t => t.Ref.Name).ToArray();

    [Fact]
    public async Task AConnectionNode_OpensTheWholeDatabase_ConnectingFirst_AndOnlyOnce()
    {
        var (main, tree, config) = Shell();

        tree.ShowDiagram(tree.NodeOf(config.Id));
        var tab = Assert.IsType<DiagramTabViewModel>(main.ActiveTab);
        await tab.Pending;

        Assert.Null(tab.Error);
        Assert.Equal(["customers", "lines", "orders", "settings"], Names(tab));
        Assert.Equal("4 tables · 2 relations", tab.Summary);
        Assert.Equal("ER · Shop", tab.Title);
        Assert.False(tab.HasSchemaFilter);
        Assert.True(tab.CanExport);

        tree.ShowDiagram(tree.NodeOf(config.Id));
        Assert.Single(main.Tabs);
    }

    [Fact]
    public async Task ATableNode_ShowsItsNeighbours_WithAnAdjustableDepth()
    {
        var (main, tree, config) = Shell();
        var node = tree.NodeOf(config.Id)!;
        await tree.ConnectAsync(node);
        node.IsExpanded = true;

        tree.ShowDiagram(node.Children.OfType<TableNodeViewModel>().Single(t => t.Title == "customers"));
        var tab = (DiagramTabViewModel)main.ActiveTab!;
        await tab.Pending;

        Assert.Equal(["customers", "orders"], Names(tab));
        Assert.True(tab.IsFocused);
        Assert.Equal("customers", tab.Scene!.Model.Tables[tab.SelectedTable].Ref.Name);

        tab.DeeperCommand.Execute(null);
        await tab.Pending;
        Assert.Equal(["customers", "lines", "orders"], Names(tab));

        tab.ShowAllCommand.Execute(null);
        await tab.Pending;
        Assert.Equal(4, Names(tab).Length);
        Assert.False(tab.IsFocused);

        tab.FocusOn(new TableRef(null, "settings"));
        await tab.Pending;
        Assert.Equal(["settings"], Names(tab));
    }

    [Fact]
    public async Task ColumnModeSearchAndOpening()
    {
        var (main, _, config) = Shell();
        var tab = main.OpenDiagram(config, new DiagramRequest());
        await tab.Pending;
        var centred = new List<string>();
        tab.CentreRequested += i => centred.Add(tab.Scene!.Model.Tables[i].Ref.Name);

        tab.IsKeyColumns = true;
        await tab.Pending;
        Assert.Equal(DiagramColumnMode.Keys, tab.Scene!.Mode);

        tab.SearchText = "s";
        Assert.Equal("1 of 4", tab.SearchStatus);
        tab.Search(1);
        tab.Search(-1);
        Assert.Equal(["settings", centred[1], "settings"], centred);
        tab.SearchText = "nothing";
        Assert.Equal("No table", tab.SearchStatus);

        await tab.OpenTableAsync(tab.Scene.Find("orders")[0]);
        Assert.Equal(2, main.Tabs.Count);
        Assert.IsNotType<DiagramTabViewModel>(main.ActiveTab);
        Assert.Contains("orders", main.ActiveTab!.Title);
    }

    [Fact]
    public async Task Reload_ReadsTheDatabaseAgain()
    {
        var (main, _, config) = Shell();
        var tab = main.OpenDiagram(config, new DiagramRequest());
        await tab.Pending;

        _fx.CreateSqliteFileWith("shop.db", "CREATE TABLE added (id INTEGER PRIMARY KEY);");
        await tab.ArrangeAsync();
        Assert.Equal(4, Names(tab).Length);

        await tab.ReloadAsync();
        Assert.Contains("added", Names(tab));
    }

    [Fact]
    public async Task SchemaFilter_StartsOnTheRequestedSchema_AndRedraws()
    {
        var id = new ColumnInfo("id", "int", typeof(int), false, true, 0);
        var tables = new[] { "sales", "stock" }
            .Select(s => new TableInfo(new TableRef(s, "t_" + s), false, [id], [], [])).ToList();
        var config = TreeFixture.Conn("Db");
        var session = new Session(config, new Core.Drivers.PostgresDriver(), tables);
        var tab = new DiagramTabViewModel(config, new DiagramRequest(Schema: "stock"),
            new DiagramHooks(_ => Task.FromResult<Session?>(session), (_, _) => Task.CompletedTask));

        await tab.LoadAsync();
        Assert.Equal(["t_stock"], Names(tab));
        Assert.True(tab.HasSchemaFilter);
        Assert.Equal("stock", tab.SchemaFilterText);

        tab.SchemaChoices[0].IsShown = true;
        await tab.Pending;
        Assert.Equal(2, Names(tab).Length);
        Assert.True(tab.Scene!.ShowSchema);

        foreach (var choice in tab.SchemaChoices) choice.IsShown = false;
        await tab.Pending;
        Assert.True(tab.IsEmpty);
        Assert.Equal("No schema is selected.", tab.EmptyText);
    }

    [Fact]
    public async Task AFailedConnection_IsShownInTheTab()
    {
        var config = TreeFixture.Conn("Gone");
        var tab = new DiagramTabViewModel(config, new DiagramRequest(),
            new DiagramHooks(_ => throw new InvalidOperationException("no server"), (_, _) => Task.CompletedTask));

        await tab.LoadAsync();

        Assert.Contains("no server", tab.Error);
        Assert.False(tab.IsBusy);
        Assert.False(tab.CanExport);
    }

    [Fact]
    public async Task Cancelling_StopsTheLoad()
    {
        var config = TreeFixture.Conn("Slow");
        var tab = new DiagramTabViewModel(config, new DiagramRequest(), new DiagramHooks(
            async ct => { await Task.Delay(Timeout.Infinite, ct); return null; }, (_, _) => Task.CompletedTask));
        var load = tab.LoadAsync();

        tab.CancelCommand.Execute(null);
        await load;

        Assert.False(tab.IsBusy);
        Assert.Equal("Reading the diagram was cancelled.", tab.Error);
    }

    [Fact]
    public void Zoom_IsClamped_AndStepsShrinkWhenFarOut()
    {
        var config = TreeFixture.Conn("Db");
        var tab = new DiagramTabViewModel(config, new DiagramRequest(),
            new DiagramHooks(_ => Task.FromResult<Session?>(null), (_, _) => Task.CompletedTask));

        tab.ZoomBy(0.1);
        Assert.Equal(1.1, tab.Zoom, 6);
        tab.SetZoom(0.1);
        tab.ZoomBy(0.1);
        Assert.Equal(0.12, tab.Zoom, 6);
        tab.SetZoom(100);
        Assert.Equal(DiagramTabViewModel.MaxZoom, tab.Zoom);
    }
}

public class DiagramViewTests
{
    private static void Pump()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    // A tab over canned metadata: the load completes synchronously apart from the layout, which is waited for.
    private static async Task<(Window Window, DiagramSurface Surface, DiagramTabViewModel Tab, List<TableRef> Opened)> Show(int count)
    {
        var id = new ColumnInfo("id", "integer", typeof(int), false, true, 0);
        var tables = Enumerable.Range(0, count)
            .Select(i => new TableInfo(new TableRef(null, $"t{i:000}"), false, [id], [], [])).ToList();
        var config = TreeFixture.Conn("Db");
        var session = new Session(config, new Core.Drivers.SqliteDriver(), tables);
        var opened = new List<TableRef>();
        var tab = new DiagramTabViewModel(config, new DiagramRequest(), new DiagramHooks(
            _ => Task.FromResult<Session?>(session), (_, t) => { opened.Add(t); return Task.CompletedTask; }));
        await tab.LoadAsync();
        var view = new DiagramView { DataContext = tab };
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        Pump();
        return (window, view.GetVisualDescendants().OfType<DiagramSurface>().Single(), tab, opened);
    }

    private static Point CentreOf(Window window, DiagramSurface surface, int table)
    {
        var r = surface.Scene!.Rect(table);
        return surface.TranslatePoint(new Point(r.CenterX, r.CenterY), window)!.Value;
    }

    [AvaloniaFact]
    public async Task OnlyTheTablesInViewAreDrawn()
    {
        var (window, surface, tab, _) = await Show(400);
        tab.SetZoom(1);
        Pump();
        // The test platform draws nothing: rendering into a bitmap runs the same drawing code.
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(900, 600));
        bitmap.Render(surface);

        Assert.InRange(surface.DrawnTables, 1, 100);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ATableIsDraggedAlone_AndADoubleClickOpensIt()
    {
        var (window, surface, tab, opened) = await Show(3);
        var before = surface.Scene!.Rect(1);
        var other = CentreOf(window, surface, 0);
        var at = CentreOf(window, surface, 1);

        window.MouseDown(at, MouseButton.Left);
        window.MouseMove(at + new Vector(40, 120), RawInputModifiers.LeftMouseButton);
        window.MouseUp(at + new Vector(40, 120), MouseButton.Left);
        Pump();

        Assert.Equal(before.X + 40 / tab.Zoom, surface.Scene.Rect(1).X, 0);
        Assert.Equal(before.Y + 120 / tab.Zoom, surface.Scene.Rect(1).Y, 0);
        Assert.Equal(other.X, CentreOf(window, surface, 0).X, 0);   // the canvas did not pan
        Assert.Equal(1, tab.SelectedTable);
        Assert.Empty(opened);

        var third = CentreOf(window, surface, 2);
        for (var click = 0; click < 2; click++)
        {
            window.MouseDown(third, MouseButton.Left);
            window.MouseUp(third, MouseButton.Left);
        }
        Pump();
        Assert.Equal("t002", Assert.Single(opened).Name);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheBackgroundPans_AndTheWheelZoomsAroundTheCursor()
    {
        var (window, surface, tab, _) = await Show(3);
        tab.SetZoom(1);
        Pump();
        var scroller = surface.FindAncestorOfType<ScrollViewer>()!;
        var empty = scroller.TranslatePoint(new Point(scroller.Bounds.Width - 20, scroller.Bounds.Height - 20), window)!.Value;
        var home = CentreOf(window, surface, 0);

        window.MouseDown(empty - new Vector(200, 100), MouseButton.Left);
        window.MouseMove(empty, RawInputModifiers.LeftMouseButton);
        window.MouseUp(empty, MouseButton.Left);
        Pump();
        var panned = CentreOf(window, surface, 0);
        Assert.Equal(home.X + 200, panned.X, 1);
        Assert.Equal(home.Y + 100, panned.Y, 1);

        window.MouseWheel(panned, new Vector(0, 1));
        Pump();
        Assert.Equal(1.1, tab.Zoom, 6);
        Assert.Equal(panned.X, CentreOf(window, surface, 0).X, 0);
        Assert.Equal(panned.Y, CentreOf(window, surface, 0).Y, 0);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ASearchedTable_IsBroughtToTheMiddle()
    {
        var (window, surface, tab, _) = await Show(200);

        tab.SearchText = "t150";
        Pump();

        var scroller = surface.FindAncestorOfType<ScrollViewer>()!;
        var middle = scroller.TranslatePoint(new Point(scroller.Bounds.Width / 2, scroller.Bounds.Height / 2), window)!.Value;
        var found = CentreOf(window, surface, tab.SelectedTable);
        Assert.InRange(found.X - middle.X, -1.5, 1.5);
        Assert.InRange(found.Y - middle.Y, -1.5, 1.5);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheExport_DrawsTheWholeDiagram_AndShrinksAHugeOne()
    {
        var (window, surface, _, _) = await Show(30);
        using var stream = new MemoryStream();

        surface.Painter.SavePng(surface.Scene!, stream);

        Assert.Equal(30, surface.Painter.DrawnTables);
        Assert.Equal(1.5, DiagramPainter.ExportScale(2000, 1000));
        var huge = DiagramPainter.ExportScale(40_000, 30_000);
        Assert.True(40_000 * huge <= 16_000 && 40_000 * huge * 30_000 * huge <= 48_000_001);
        window.Close();
    }
}
