using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App.Tests;

public class FreeCanvasMathTests
{
    [Theory]
    [InlineData(800, 2000)]   // bigger than the viewport
    [InlineData(800, 300)]    // smaller
    [InlineData(800, 40)]     // smaller than the strip that is kept
    [InlineData(100, 2000)]   // tiny viewport
    public void BothEndsOfAPan_KeepAStripOfThePlanOnScreen(double view, double content)
    {
        var pad = FreeCanvasMath.Pad(view, content);
        var strip = Math.Min(Math.Min(FreeCanvasMath.Keep, view / 2), content);

        var low = FreeCanvasMath.Clamp(double.MinValue, view, content);
        var high = FreeCanvasMath.Clamp(double.MaxValue, view, content);

        Assert.Equal(strip, view - (pad - low), 6);          // pushed to the far end: its start is still visible
        Assert.Equal(strip, pad + content - high, 6);        // pushed to the near end: its end is still visible
    }

    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    public void AnyPointOfThePlan_CanBeBroughtToTheCentre(double point)
    {
        const double view = 800, content = 300;
        var wanted = FreeCanvasMath.Pad(view, content) + point - view / 2;

        Assert.Equal(wanted, FreeCanvasMath.Clamp(wanted, view, content), 6);
    }

    [Theory]
    [InlineData(1.0, 1.5)]
    [InlineData(1.5, 0.5)]
    public void Zoom_KeepsThePointUnderTheCursor(double from, double to)
    {
        const double view = 800, content = 2000, anchor = 200;
        var offset = FreeCanvasMath.Pad(view, content * from) + 300;
        var before = (offset + anchor - FreeCanvasMath.Pad(view, content * from)) / from;

        var next = FreeCanvasMath.ZoomOffset(offset, anchor, view, content, from, to);

        Assert.Equal(before, (next + anchor - FreeCanvasMath.Pad(view, content * to)) / to, 6);
    }

    [Fact]
    public void Reveal_MovesOnlyWhenNeeded()
    {
        Assert.Equal(100, FreeCanvasMath.Reveal(100, 500, 200, 100));
        Assert.Equal(26, FreeCanvasMath.Reveal(100, 500, 50, 100));
        Assert.Equal(324, FreeCanvasMath.Reveal(100, 500, 700, 100));
    }

    [Fact]
    public void WheelStep_IsATenthPerNotch_AndNeverNothing()
    {
        Assert.Equal(0.1, FreeCanvasMath.WheelStep(1), 6);
        Assert.Equal(-0.1, FreeCanvasMath.WheelStep(-3), 6);
        Assert.Equal(0.01, FreeCanvasMath.WheelStep(0.02), 6);
        Assert.Equal(0, FreeCanvasMath.WheelStep(0));
    }
}

public class PlanCanvasViewTests
{
    private static void Pump()
    {
        for (var i = 0; i < 6; i++) Dispatcher.UIThread.RunJobs();
    }

    private static (Window Window, ScrollViewer Scroller, PlanResultTab Tab) Show()
    {
        var tab = PlanSamples.Tab();
        var view = new PlanView { DataContext = tab };
        var window = new Window { Content = view, Width = 1100, Height = 650 };
        window.Show();
        Pump();
        return (window, view.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "Scroller"), tab);
    }

    private static Point CardAt(Window window, int id) => window.GetVisualDescendants().OfType<Border>()
        .Single(b => b.Classes.Contains("plannode") && ((PlanNodeViewModel)b.DataContext!).Node.Id == id)
        .TranslatePoint(default, window)!.Value;

    [AvaloniaFact]
    public void ASmallPlan_CanBeDraggedPastItsEdges_AndFitBringsItBack()
    {
        var (window, scroller, _) = Show();
        scroller.Focus();
        window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.Control);   // fitted: where Fit must bring it back
        Pump();
        var home = CardAt(window, 0);
        var empty = scroller.TranslatePoint(new Point(scroller.Bounds.Width - 20, scroller.Bounds.Height - 20), window)!.Value;

        // down and to the right: the direction that used to stop at the top left corner
        window.MouseDown(empty - new Vector(300, 200), MouseButton.Middle);
        window.MouseMove(empty);
        window.MouseUp(empty, MouseButton.Middle);
        Pump();
        var moved = CardAt(window, 0);
        Assert.Equal(home.X + 300, moved.X, 1);
        Assert.Equal(home.Y + 200, moved.Y, 1);

        window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.Control);
        Pump();
        Assert.Equal(home.X, CardAt(window, 0).X, 1);
        Assert.Equal(home.Y, CardAt(window, 0).Y, 1);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(RawInputModifiers.None)]
    [InlineData(RawInputModifiers.Control)]
    public void TheWheel_ZoomsAroundTheCursor(RawInputModifiers modifiers)
    {
        var (window, _, tab) = Show();
        tab.Zoom = 1;
        Pump();
        var cursor = CardAt(window, 1);

        window.MouseWheel(cursor, new Vector(0, 1), modifiers);
        Pump();

        Assert.Equal(1.1, tab.Zoom, 6);
        Assert.Equal(cursor.X, CardAt(window, 1).X, 0);
        Assert.Equal(cursor.Y, CardAt(window, 1).Y, 0);
        window.Close();
    }

    [AvaloniaFact]
    public void SelectingAnOperatorPannedAway_BringsItBack()
    {
        var (window, scroller, tab) = Show();
        scroller.Offset = default;   // the plan pushed to the bottom right corner
        Pump();
        var seek = tab.Selected!.Nodes.Single(n => n.Node.Id == 3);

        tab.Selected.SelectedNode = seek;
        Pump();

        var topLeft = CardAt(window, 3);
        var inScroller = window.TranslatePoint(topLeft, scroller)!.Value;
        Assert.True(new Rect(scroller.Bounds.Size).Contains(new Rect(inScroller, new Size(seek.Width, seek.Height))));
        window.Close();
    }
}
