using Avalonia.Controls;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;

namespace UiShots;

internal static partial class Scenes
{
    /// <summary>The shop database with a few more relations: a self reference, a one-to-one and a cycle.</summary>
    private static async Task<DiagramTabViewModel> Diagram(Demo d, DiagramRequest request, string? extra = null)
    {
        Demo.Exec(d.Shop.Database, """
            CREATE TABLE employees (id INTEGER PRIMARY KEY, name TEXT NOT NULL, manager_id INTEGER REFERENCES employees(id), team_id INTEGER REFERENCES teams(id));
            CREATE TABLE teams (id INTEGER PRIMARY KEY, name TEXT NOT NULL, lead_id INTEGER REFERENCES employees(id));
            CREATE TABLE shipments (order_id INTEGER PRIMARY KEY REFERENCES orders(id), packed_by INTEGER REFERENCES employees(id), shipped_at DATETIME);
            CREATE TABLE addresses (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES customers(id), street TEXT, city TEXT, postal_code TEXT);
            """);
        if (extra is not null) Demo.Exec(d.Shop.Database, extra);
        await Open(d);
        d.AllConnectionNodes().First(n => n.Config == d.Shop).IsExpanded = false;
        var tab = d.Main.OpenDiagram(d.Shop, request);
        await tab.Pending;
        await Settle();
        return tab;
    }

    private static async Task<Window?> ErDiagram(Demo d)
    {
        await Diagram(d, new DiagramRequest());
        return null;
    }

    private static async Task<Window?> ErDiagramFocus(Demo d)
    {
        var tab = await Diagram(d, new DiagramRequest(Table: new TableRef(null, "orders")));
        tab.Depth = 2;
        await tab.Pending;
        tab.IsKeyColumns = true;
        await tab.Pending;
        await Settle();
        return null;
    }

    // The pointer over a table: its relations stand out, the unrelated tables fade.
    private static async Task<Window?> ErDiagramHover(Demo d)
    {
        var tab = await Diagram(d, new DiagramRequest());
        var surface = d.Window.GetVisualDescendants().OfType<DiagramSurface>().First();
        var rect = tab.Scene!.Rect(tab.Scene.Find("customers")[0]);
        var at = Avalonia.VisualExtensions.TranslatePoint(surface, new Avalonia.Point(rect.CenterX, rect.Y + 10), d.Window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(d.Window, at);
        await Settle();
        return null;
    }

    // 300 linked tables, fitted: the level of detail of a large database.
    private static async Task<Window?> ErDiagramLarge(Demo d)
    {
        var sql = string.Concat(Enumerable.Range(0, 300).Select(i =>
            $"CREATE TABLE ref_{i:000} (id INTEGER PRIMARY KEY, label TEXT" +
            (i is > 0 and < 220 ? $", parent_id INTEGER REFERENCES ref_{(i - 1) / 6:000}(id)" : "") + ");"));
        var tab = await Diagram(d, new DiagramRequest(), sql);
        var view = d.Window.GetVisualDescendants().OfType<DiagramView>().First();
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Scroller");
        scroller.Focus();
        Avalonia.Headless.HeadlessWindowExtensions.KeyPressQwerty(
            d.Window, Avalonia.Input.PhysicalKey.Digit0, Avalonia.Input.RawInputModifiers.Control);
        await Settle();
        Console.WriteLine($"  {tab.Summary}, {tab.Scene!.Width} x {tab.Scene.Height}, zoom {tab.ZoomText}");
        return null;
    }

    // The PNG export itself, next to the window shots.
    private static async Task<Window?> ErDiagramExport(Demo d)
    {
        var tab = await Diagram(d, new DiagramRequest());
        var surface = d.Window.GetVisualDescendants().OfType<DiagramSurface>().First();
        using var stream = File.Create(Path.Combine(ExportDir, $"er-diagram-export-file-{Theme}.png"));
        surface.Painter.SavePng(tab.Scene!, stream);
        return null;
    }
}
