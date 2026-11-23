using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;

namespace UiShots;

/// <summary>Scrolls a 200 row x 20 column table by script (vertically, then horizontally), rendering every step; prints ms per frame.</summary>
internal static class ScrollBench
{
    public static async Task Run(int rounds)
    {
        using var demo = new Demo();
        demo.Window.Show();
        await demo.ConnectAsync();
        var session = demo.Sessions.Find(demo.Shop.Id)!;
        await demo.Main.OpenTable(session, new TableRef(null, "wide"));
        var tab = (TableTabViewModel)demo.Main.ActiveTab!;
        for (var i = 0; i < 100 && (tab.IsLoading || tab.Rows.Count == 0); i++) await Task.Delay(20);
        for (var i = 0; i < 10; i++) { await Task.Delay(30); Dispatcher.UIThread.RunJobs(); }

        var grid = demo.Window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Columns.Count > 0);
        var items = tab.Rows.ToList();
        var columns = grid.Columns.ToList();
        var times = new List<double>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Step(int row, int col)
        {
            sw.Restart();
            grid.ScrollIntoView(items[row], columns[col]);
            demo.Window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            demo.Window.CaptureRenderedFrame();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        for (var r = 0; r < rounds; r++)
        {
            for (var row = 0; row < items.Count; row += 3) Step(row, 0);
            for (var row = items.Count - 1; row >= 0; row -= 3) Step(row, 0);
            for (var col = 0; col < columns.Count; col++) Step(items.Count / 2, col);
        }
        times.Sort();
        Console.WriteLine($"frames={times.Count} mean={times.Average():F1} ms median={times[times.Count / 2]:F1} p95={times[(int)(times.Count * 0.95)]:F1} max={times[^1]:F1}");
    }
}