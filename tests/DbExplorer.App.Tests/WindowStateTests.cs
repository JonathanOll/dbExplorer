using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DbExplorer.App.Views;

namespace DbExplorer.App.Tests;

/// <summary>Remembering the main window's size, position and maximised state across launches.</summary>
public class WindowStateTests : ShellTestBase
{
    private static readonly ScreenArea Primary = new(0, 0, 1920, 1040, 1.0);
    private static readonly ScreenArea Left = new(-1280, 100, 1280, 960, 1.0);

    private string StateFile => Path.Combine(T.Fx.Dir, "window.json");

    private WindowPlacement? Roundtrip(WindowPlacement p)
    {
        WindowStateStore.TrySave(StateFile, p);
        return WindowStateStore.TryLoad(StateFile);
    }

    // ---- the file ----

    [Fact]
    public void WindowState_RoundTrips()
    {
        var saved = new WindowPlacement(-1200, 40, 1033.5, 702.5, true);

        Assert.Equal(saved, Roundtrip(saved));
        Assert.True(new FileInfo(StateFile).Length < 200);   // a tiny file
        var noPosition = saved with { X = null, Y = null };
        Assert.Equal(noPosition, Roundtrip(noPosition));   // no position: left to the OS
    }

    [Fact]
    public void WindowState_MissingFile_IsNoPlacement() => Assert.Null(WindowStateStore.TryLoad(Path.Combine(T.Fx.Dir, "nope.json")));

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"x\":1,\"y\":2,\"width\":\"wide\",\"height\":500}")]
    [InlineData("{\"x\":1,\"y\":2,\"width\":-5,\"height\":500}")]
    [InlineData("{\"x\":1,\"y\":2,\"width\":0,\"height\":500}")]
    [InlineData("{\"x\":1,\"y\":2,\"height\":500}")]
    [InlineData("{\"x\":1.5,\"y\":2,\"width\":800,\"height\":500}")]
    [InlineData("{\"x\":99999999999,\"y\":2,\"width\":800,\"height\":500}")]
    [InlineData("{\"x\":1,\"y\":2,\"width\":800,\"height\":500,\"maximized\":\"yes\"}")]
    [InlineData("{\"x\":1,\"y\":2,\"width\":1e300,\"height\":500}")]
    public void WindowState_CorruptFile_IsNoPlacement_AndNeverThrows(string content)
    {
        File.WriteAllText(StateFile, content);
        Assert.Null(WindowStateStore.TryLoad(StateFile));
    }

    [Fact]
    public void WindowState_UnknownFieldsAreIgnored_AndLoneCoordinateDropsThePosition()
    {
        File.WriteAllText(StateFile, "{\"extra\":{\"a\":[1,2]},\"x\":10,\"width\":900,\"height\":600}");

        Assert.Equal(new WindowPlacement(null, null, 900, 600, false), WindowStateStore.TryLoad(StateFile));
    }

    [Fact]
    public void WindowState_SaveIntoAMissingFolder_CreatesIt_AndAnUnwritablePathDoesNotThrow()
    {
        var nested = Path.Combine(T.Fx.Dir, "a", "b", "window.json");
        WindowStateStore.TrySave(nested, new WindowPlacement(1, 2, 800, 600, false));
        Assert.NotNull(WindowStateStore.TryLoad(nested));

        WindowStateStore.TrySave(Path.Combine(T.Fx.Dir, "a", "b"), new WindowPlacement(1, 2, 800, 600, false));   // a folder, not a file
    }

    [Fact]
    public void WindowStateFile_LivesUnderTheDataDir()
    {
        Assert.Equal(@"C:\tmp\x\window.json", AppPaths.Resolve(["--data-dir", @"C:\tmp\x"], _ => null).WindowStateFile);
    }

    // ---- fitting to the screens ----

    [Fact]
    public void Fit_KeepsAPlacementThatIsOnAScreen()
    {
        var fitted = WindowStateStore.Fit(new WindowPlacement(100, 50, 1200, 750, false), [Primary], 720, 420);
        Assert.Equal(new WindowPlacement(100, 50, 1200, 750, false), fitted);
    }

    [Fact]
    public void Fit_ADisconnectedMonitor_FallsBackToTheOsPlacement_KeepingTheSize()
    {
        var onGoneMonitor = new WindowPlacement(3000, 200, 1000, 700, true);

        var fitted = WindowStateStore.Fit(onGoneMonitor, [Primary], 720, 420);

        Assert.Null(fitted.X);
        Assert.Null(fitted.Y);
        Assert.Equal((1000, 700, true), (fitted.Width, fitted.Height, fitted.Maximized));
    }

    [Fact]
    public void Fit_AMonitorToTheLeftWithNegativeCoordinates_IsRestoredThere()
    {
        var fitted = WindowStateStore.Fit(new WindowPlacement(-1100, 150, 1000, 700, false), [Primary, Left], 720, 420);
        Assert.Equal((-1100, 150), (fitted.X, fitted.Y));
    }

    [Fact]
    public void Fit_PartlyOffScreen_IsPushedFullyOntoTheScreen()
    {
        var fitted = WindowStateStore.Fit(new WindowPlacement(1500, 900, 1000, 700, false), [Primary], 720, 420);
        Assert.Equal((920, 340), (fitted.X, fitted.Y));   // right edge at 1920, bottom edge at 1040

        var above = WindowStateStore.Fit(new WindowPlacement(-300, -100, 1000, 700, false), [Primary], 720, 420);
        Assert.Equal((0, 0), (above.X, above.Y));
    }

    [Fact]
    public void Fit_BiggerThanTheScreen_IsShrunkToIt_ButNeverBelowTheMinimum()
    {
        var small = new ScreenArea(0, 0, 1024, 728, 1.0);
        var fitted = WindowStateStore.Fit(new WindowPlacement(0, 0, 3000, 2000, false), [small], 720, 420);
        Assert.Equal((0, 0, 1024.0, 728.0), (fitted.X, fitted.Y, fitted.Width, fitted.Height));

        var tiny = WindowStateStore.Fit(new WindowPlacement(10, 10, 100, 100, false), [Primary], 720, 420);
        Assert.Equal((720.0, 420.0), (tiny.Width, tiny.Height));
    }

    [Fact]
    public void Fit_HighDpiMonitor_ComparesPhysicalPixels()
    {
        var hiDpi = new ScreenArea(0, 0, 3000, 1900, 2.0);   // 1500 x 950 device-independent
        var fitted = WindowStateStore.Fit(new WindowPlacement(2000, 1200, 1200, 750, false), [hiDpi], 720, 420);
        Assert.Equal((600, 400), (fitted.X, fitted.Y));   // 1200 x 750 dip = 2400 x 1500 px
        Assert.Equal((1200.0, 750.0), (fitted.Width, fitted.Height));
    }

    [Fact]
    public void Fit_WithoutScreensOrPosition_StillClampsTheSize()
    {
        Assert.Equal(new WindowPlacement(null, null, 720, 420, false),
            WindowStateStore.Fit(new WindowPlacement(5, 5, 10, 10, false), [], 720, 420));
        Assert.Equal(new WindowPlacement(null, null, 900, 600, true),
            WindowStateStore.Fit(new WindowPlacement(null, null, 900, 600, true), [Primary], 720, 420));
    }

    // ---- the normal-bounds tracker ----

    [Fact]
    public void Tracker_RemembersTheNormalBounds_AndIgnoresThoseSeenWhileMaximized()
    {
        var tracker = new NormalBoundsTracker(new WindowPlacement(100, 100, 1200, 750, false));
        tracker.PositionChanged(200, 120, isNormal: true);
        tracker.SizeChanged(1000, 600, isNormal: true);
        tracker.PositionChanged(-8, -8, isNormal: false);   // events of a maximised window are not "normal"
        tracker.SizeChanged(1936, 1048, isNormal: false);

        Assert.Equal(new WindowPlacement(200, 120, 1000, 600, true), tracker.Placement(maximized: true));
    }

    [Fact]
    public void Tracker_WhenTheMaximisingResizeArrivesBeforeTheStateChange_PutsTheEarlierBoundsBack()
    {
        var tracker = new NormalBoundsTracker();
        tracker.PositionChanged(200, 120, true);
        tracker.SizeChanged(1000, 600, true);
        // maximise: move and resize events come while the window still reports Normal, then the state changes
        tracker.PositionChanged(-8, -8, true);
        tracker.SizeChanged(1936, 1048, true);
        tracker.BecameMaximized(-8, -8, 1936, 1048);

        Assert.Equal(new WindowPlacement(200, 120, 1000, 600, true), tracker.Placement(true));
    }

    [Fact]
    public void Tracker_WhenOnlyTheStateChangeArrives_KeepsTheBounds()
    {
        var tracker = new NormalBoundsTracker();
        tracker.PositionChanged(200, 120, true);
        tracker.SizeChanged(1000, 600, true);
        tracker.BecameMaximized(-8, -8, 1936, 1048);   // different from what was recorded: nothing to undo

        Assert.Equal(new WindowPlacement(200, 120, 1000, 600, false), tracker.Placement(false));
    }

    [Fact]
    public void Tracker_IgnoresTheMinimisedPosition()
    {
        var tracker = new NormalBoundsTracker(new WindowPlacement(200, 120, 1000, 600, false));
        // Win32 moves a minimised window to (-32000, -32000); the event can arrive before the state says Minimized
        tracker.PositionChanged(-32000, -32000, isNormal: true);
        tracker.PositionChanged(-32000, 5, isNormal: true);
        tracker.PositionChanged(5, -32000, isNormal: true);

        Assert.Equal(new WindowPlacement(200, 120, 1000, 600, false), tracker.Placement(false));

        tracker.PositionChanged(-1500, 40, isNormal: true);   // a real window on a monitor to the left is still tracked
        Assert.Equal(-1500, tracker.Placement(false)!.X);
    }

    [Fact]
    public void Tracker_WithNothingKnown_SavesNothing()
    {
        Assert.Null(new NormalBoundsTracker().Placement(false));
        Assert.Equal(new WindowPlacement(null, null, 900, 600, false),
            new NormalBoundsTracker(new WindowPlacement(null, null, 900, 600, false)).Placement(false));
    }

    // ---- the window ----

    [AvaloniaFact]
    public async Task MainWindow_SavesTheTrackedPositionAndSizeOnClose()
    {
        var window = new MainWindow { DataContext = NewMain(), StateFile = StateFile };
        window.ApplyPlacement(new WindowPlacement(120, 90, 1000, 640, false));
        window.Show();
        await Pump();

        window.Position = new PixelPoint(210, 160);   // the user moves and resizes it
        window.Width = 900;
        window.Height = 600;
        await Pump();
        window.Close();

        var saved = WindowStateStore.TryLoad(StateFile);
        Assert.NotNull(saved);
        Assert.Equal((210, 160, 900.0, 600.0, false), (saved!.X, saved.Y, saved.Width, saved.Height, saved.Maximized));
    }

    [AvaloniaFact]
    public async Task MainWindow_ClosedMaximised_SavesTheNormalBoundsAndTheMaximisedFlag()
    {
        var window = new MainWindow { DataContext = NewMain(), StateFile = StateFile };
        window.ApplyPlacement(new WindowPlacement(120, 90, 1000, 640, false));
        window.Show();
        await Pump();

        window.WindowState = WindowState.Maximized;
        await Pump();
        window.Close();

        var saved = WindowStateStore.TryLoad(StateFile);
        Assert.NotNull(saved);
        Assert.True(saved!.Maximized);
        Assert.Equal((120, 90, 1000.0, 640.0), (saved.X, saved.Y, saved.Width, saved.Height));   // not the maximised bounds
    }

    [AvaloniaFact]
    public async Task MainWindow_RestorePlacement_AppliesTheSavedFile_AndWritesBackToIt()
    {
        WindowStateStore.TrySave(StateFile, new WindowPlacement(140, 100, 1100, 680, false));
        var window = new MainWindow { DataContext = NewMain() };

        window.RestorePlacement(StateFile);
        Assert.Equal((1100.0, 680.0), (window.Width, window.Height));
        Assert.Equal(new PixelPoint(140, 100), window.Position);
        window.Show();
        await Pump();
        window.Close();

        Assert.Equal(1100, WindowStateStore.TryLoad(StateFile)!.Width);

        var fresh = new MainWindow();
        fresh.RestorePlacement(Path.Combine(T.Fx.Dir, "missing.json"));   // no file: defaults stay, nothing throws
        Assert.Equal(1280.0, fresh.Width);   // the default width of the window (MainWindow.axaml)
    }

    private static async Task Pump()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
    }
}
