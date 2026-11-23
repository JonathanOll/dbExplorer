using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App.Tests;

/// <summary>Ctrl+L column flash, middle-button pan of the grid, middle-click on a tab to close it.</summary>
public class MouseAndFlashTests : ShellTestBase
{
    private static async Task Pump(int rounds = 20)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    private static DataGrid InnerGrid(Control root) => root.GetVisualDescendants().OfType<DataGrid>().First(g => g.Columns.Count > 0);

    // The grid template holds several scroll bars (hidden ones included): the one that scrolls the rows is visible with a range.
    private static ScrollBar VerticalBar(DataGrid data) =>
        data.GetVisualDescendants().OfType<ScrollBar>().First(s => s.Orientation == Avalonia.Layout.Orientation.Vertical && s.IsVisible && s.Maximum > 0);

    private static Point Center(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    // ---- Ctrl+L flash ----

    [AvaloniaFact]
    public async Task ColumnJump_LightsTheColumnForAMoment_ThenClearsIt()
    {
        var tab = await T.OpenTab("orders");
        var grid = new ResultGrid { Columns = tab.GridColumns, ItemsSource = tab.Rows, Features = tab, Editing = tab };
        var window = new Window { Content = grid, Width = 1400, Height = 500 };
        window.Show();
        await Pump();

        var index = tab.FindColumn("note")!.Value;
        grid.ScrollToColumn(index);
        await Pump(3);

        var data = InnerGrid(grid);
        var lit = data.GetVisualDescendants().OfType<DataGridCell>().Where(c => c.Classes.Contains("flash")).ToList();
        Assert.NotEmpty(lit);
        Assert.All(lit, c => Assert.Equal(index, ((GridCell)c.Content!).Index));   // only that column
        Assert.Contains(grid.GetVisualDescendants().OfType<StackPanel>(), p => p.Classes.Contains("flashHeader"));

        await Pump(130);   // > 1.6 s
        Assert.DoesNotContain(data.GetVisualDescendants().OfType<DataGridCell>(), c => c.Classes.Contains("flash"));
        Assert.DoesNotContain(grid.GetVisualDescendants().OfType<StackPanel>(), p => p.Classes.Contains("flashHeader"));
        window.Close();
    }

    // ---- middle-button pan ----

    [AvaloniaFact]
    public async Task MiddleButtonDrag_PansTheGridVertically()
    {
        var tab = await T.OpenTab("big");
        var grid = new ResultGrid { Columns = tab.GridColumns, ItemsSource = tab.Rows, Features = tab, Editing = tab };
        var window = new Window { Content = grid, Width = 1000, Height = 400 };
        window.Show();
        await Pump();

        var data = InnerGrid(grid);
        var bar = VerticalBar(data);
        Assert.Equal(0, bar.Value);
        var firstBefore = data.GetVisualDescendants().OfType<DataGridRow>().Min(r => r.Index);

        var start = Center(window, data);
        window.MouseDown(start, MouseButton.Middle);
        window.MouseMove(start + new Point(0, -150), RawInputModifiers.MiddleMouseButton);   // drag up: content follows the pointer, so it moves up
        window.MouseUp(start + new Point(0, -150), MouseButton.Middle);
        await Pump();

        Assert.True(bar.Value > 100, $"vertical offset should follow the drag, was {bar.Value}");
        Assert.True(data.GetVisualDescendants().OfType<DataGridRow>().Min(r => r.Index) > firstBefore);

        var after = bar.Value;
        window.MouseMove(start);   // button released: moving does nothing
        await Pump(3);
        Assert.Equal(after, bar.Value);
        window.Close();
    }

    [AvaloniaFact]
    public async Task MiddleButtonDrag_PansTheGridHorizontally()
    {
        var tab = await T.OpenTab("orders");
        var grid = new ResultGrid { Columns = tab.GridColumns, ItemsSource = tab.Rows, Features = tab, Editing = tab };
        var window = new Window { Content = grid, Width = 400, Height = 400 };   // narrower than the columns
        window.Show();
        await Pump();

        var data = InnerGrid(grid);
        var bar = data.GetVisualDescendants().OfType<ScrollBar>().First(s => s.Orientation == Avalonia.Layout.Orientation.Horizontal && s.IsVisible && s.Maximum > 0);
        Assert.Equal(0, bar.Value);

        var start = Center(window, data);
        window.MouseDown(start, MouseButton.Middle);
        window.MouseMove(start + new Point(-150, 0), RawInputModifiers.MiddleMouseButton);   // drag left: content moves left
        window.MouseUp(start + new Point(-150, 0), MouseButton.Middle);
        await Pump();

        Assert.True(bar.Value > 100, $"horizontal offset should follow the drag, was {bar.Value}");
        window.Close();
    }

    [AvaloniaFact]
    public async Task MiddleButtonPan_InterruptedByDetach_DoesNotLeaveThePanCursor()
    {
        var tab = await T.OpenTab("big");
        var grid = new ResultGrid { Columns = tab.GridColumns, ItemsSource = tab.Rows, Features = tab, Editing = tab };
        var window = new Window { Content = grid, Width = 1000, Height = 400 };
        window.Show();
        await Pump();

        var data = InnerGrid(grid);
        window.MouseDown(Center(window, data), MouseButton.Middle);
        Assert.NotNull(data.Cursor);   // the pan cursor while the button is down

        window.Content = null;   // the view goes away mid-pan: no release will ever reach it
        await Pump(3);

        Assert.Null(data.Cursor);
        window.Close();
    }

    [AvaloniaFact]
    public async Task LeftButtonDrag_DoesNotPan()
    {
        var tab = await T.OpenTab("big");
        var grid = new ResultGrid { Columns = tab.GridColumns, ItemsSource = tab.Rows, Features = tab, Editing = tab };
        var window = new Window { Content = grid, Width = 1000, Height = 400 };
        window.Show();
        await Pump();

        var data = InnerGrid(grid);
        var bar = VerticalBar(data);
        var start = Center(window, data);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(0, 5), RawInputModifiers.LeftMouseButton);
        window.MouseUp(start + new Point(0, 5), MouseButton.Left);
        await Pump(3);
        Assert.Equal(0, bar.Value);
        window.Close();
    }

    // ---- middle-click closes a tab ----

    private static TabItem HeaderOf(MainWindow window, object tab) =>
        window.FindControl<TabControl>("DocTabs")!.GetVisualDescendants().OfType<TabItem>().First(i => ReferenceEquals(i.DataContext, tab));

    [AvaloniaFact]
    public async Task MiddleClickOnATab_ClosesIt()
    {
        var main = NewMain();
        var orders = await AddTable(main, "orders", activate: false);
        var customers = await AddTable(main, "customers");
        var window = new MainWindow { DataContext = main, Width = 1200, Height = 700 };
        window.Show();
        await Pump();

        var header = HeaderOf(window, orders);
        var p = Center(window, header);
        window.MouseDown(p, MouseButton.Middle);
        window.MouseUp(p, MouseButton.Middle);
        await Pump();

        Assert.Equal([(TabViewModel)customers], main.Tabs.ToArray());
        window.Close();
    }

    [AvaloniaFact]
    public async Task MiddleClickOnAnInactiveTab_ClosesItWithoutActivatingIt()
    {
        var main = NewMain();
        var orders = await AddTable(main, "orders", activate: false);
        var big = await AddTable(main, "big", activate: false);
        var customers = await AddTable(main, "customers");   // active
        var window = new MainWindow { DataContext = main, Width = 1200, Height = 700 };
        window.Show();
        await Pump();

        var p = Center(window, HeaderOf(window, orders));
        window.MouseDown(p, MouseButton.Middle);
        await Pump(3);
        Assert.Same(customers, main.ActiveTab);   // the press alone does not switch tabs
        window.MouseUp(p, MouseButton.Middle);
        await Pump();

        Assert.Equal([(TabViewModel)big, customers], main.Tabs.ToArray());
        Assert.Same(customers, main.ActiveTab);
        window.Close();
    }

    [AvaloniaFact]
    public async Task MiddleClickOnATab_WithPendingEdits_AsksFirst()
    {
        var main = NewMain();
        var tab = await AddTable(main, "customers");
        tab.SetCell(0, tab.FindColumn("name")!.Value, "Changed");
        Dialogs.Choice = SaveChoice.Cancel;
        var window = new MainWindow { DataContext = main, Width = 1200, Height = 700 };
        window.Show();
        await Pump();

        var p = Center(window, HeaderOf(window, tab));
        window.MouseDown(p, MouseButton.Middle);
        window.MouseUp(p, MouseButton.Middle);
        await Pump();

        Assert.Equal(1, Dialogs.AskCount);
        Assert.Single(main.Tabs);   // user answered Cancel
        window.Close();
    }

    [AvaloniaFact]
    public async Task MiddleClickOnTheSubTabs_DoesNotCloseTheTab()
    {
        var main = NewMain();
        var tab = await AddTable(main, "orders");
        var window = new MainWindow { DataContext = main, Width = 1200, Height = 700 };
        window.Show();
        await Pump();

        var sub = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "SubTabs");
        var structure = sub.GetVisualDescendants().OfType<TabItem>().Last();
        var p = Center(window, structure);
        window.MouseDown(p, MouseButton.Middle);
        window.MouseUp(p, MouseButton.Middle);
        await Pump();

        Assert.Single(main.Tabs);
        window.Close();
    }

    [AvaloniaFact]
    public async Task MiddleClickInsideTheGrid_DoesNotCloseTheTab()
    {
        var main = NewMain();
        await AddTable(main, "big");
        var window = new MainWindow { DataContext = main, Width = 1200, Height = 700 };
        window.Show();
        await Pump();

        var p = Center(window, InnerGrid(window));
        window.MouseDown(p, MouseButton.Middle);
        window.MouseUp(p, MouseButton.Middle);
        await Pump();

        Assert.Single(main.Tabs);
        window.Close();
    }
}
