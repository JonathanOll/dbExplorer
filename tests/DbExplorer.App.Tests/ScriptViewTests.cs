using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App.Tests;

/// <summary>The script tab view in a headless window: the flyouts open, the handlers wired in templates fire, the editor keys work.</summary>
public class ScriptViewTests : IDisposable
{
    private readonly ScriptTabFixture _t = new();

    public void Dispose() => _t.Dispose();

    private async Task<(Window Window, ScriptTabView View, ScriptTabViewModel Tab)> Open(string text)
    {
        var tab = await _t.NewTab(text);
        var window = new Window { Content = new ContentControl { Content = tab }, Width = 1100, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, window.GetVisualDescendants().OfType<ScriptTabView>().Single(), tab);
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public async Task TheRowLimitFlyout_Opens_AndAPresetSetsTheLimit()
    {
        var (window, view, tab) = await Open("SELECT 1");
        var button = view.FindControl<DropDownButton>("RowLimitButton")!;
        Assert.Equal("1,000 rows", button.Content);

        button.Flyout!.ShowAt(button);
        Pump();

        var content = (Control)((Flyout)button.Flyout).Content!;
        var presets = content.GetVisualDescendants().OfType<Button>().Where(b => b.Tag is int).ToList();
        Assert.Equal(ScriptTabViewModel.RowLimitPresets, presets.Select(b => (int)b.Tag!).ToArray());

        presets.Single(b => (int)b.Tag! == 100).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();

        Assert.Equal(100, tab.RowLimit);
        Assert.Equal("100 rows", button.Content);
        Assert.False(((Flyout)button.Flyout).IsOpen);   // choosing closes it
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheCustomRowLimit_AcceptsTypedNumbers_ClampedToTheRange()
    {
        var (window, view, tab) = await Open("SELECT 1");
        var button = view.FindControl<DropDownButton>("RowLimitButton")!;
        button.Flyout!.ShowAt(button);
        Pump();
        var box = ((Control)((Flyout)button.Flyout).Content!).GetVisualDescendants().OfType<TextBox>().Single();

        box.Text = "2,500";
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Pump();
        Assert.Equal(2500, tab.RowLimit);

        button.Flyout.ShowAt(button);
        Pump();
        box.Text = "9999999";
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(ScriptTabViewModel.MaxRowLimit, tab.RowLimit);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheShortcutsFlyout_ListsTheChords()
    {
        var (window, view, _) = await Open("SELECT 1");
        var button = view.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.GetValue(Avalonia.Automation.AutomationProperties.NameProperty), "Script shortcuts"));

        button.Flyout!.ShowAt(button);
        Pump();

        var texts = ((Control)((Flyout)button.Flyout).Content!).GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Ctrl+Enter", texts);
        Assert.Contains("Comment or uncomment the selected lines", texts);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RaiseLimit_InTheResultLine_RunsAgainWithTenTimesTheRows()
    {
        var (window, view, tab) = await Open("SELECT id FROM big");
        tab.RowLimit = 5;
        await tab.RunScriptAsync();
        Pump();

        var raise = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Raise limit");
        raise.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (tab.RowLimit != 50 || tab.IsRunning)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the limit was not raised");
            await Task.Delay(10);
        }
        Pump();

        Assert.Equal(50, Assert.Single(tab.Results).Sets[0].Rows.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task GoToStatement_InTheErrorBanner_SelectsTheStatementInTheEditor()
    {
        var (window, view, tab) = await Open("SELECT 1;\nSELECT * FROM nope;");
        await tab.RunScriptAsync();
        Pump();

        var go = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Go to statement" && b.IsVisible);
        go.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();

        var editor = view.FindControl<TextEditor>("Editor")!;
        Assert.Equal("SELECT * FROM nope", editor.SelectedText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheSegmentedControl_CannotBeSwitchedOffByClickingItAgain()
    {
        var (window, view, _) = await Open("SELECT 1");
        var allRows = new ToggleButton { IsChecked = true };
        view.GetType().GetMethod("OnSegmentClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(view, [allRows, new RoutedEventArgs()]);
        Assert.True(allRows.IsChecked);

        allRows.IsChecked = false;   // what a click on the checked button does before Click is raised
        view.GetType().GetMethod("OnSegmentClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(view, [allRows, new RoutedEventArgs()]);
        Assert.True(allRows.IsChecked);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CtrlSlash_CommentsAndUncommentsTheSelectedLines()
    {
        var (window, view, tab) = await Open("SELECT 1\n  FROM t\n\nWHERE 1 = 1");
        var editor = view.FindControl<TextEditor>("Editor")!;
        editor.TextArea.Focus();
        Pump();
        editor.SelectAll();

        window.KeyPress(Key.OemQuestion, RawInputModifiers.Control, PhysicalKey.Slash, "/");
        Pump();
        Assert.Equal("-- SELECT 1\n  -- FROM t\n\n-- WHERE 1 = 1", editor.Text);
        Assert.Equal(editor.Text, tab.Text);   // the view-model follows

        window.KeyPress(Key.OemQuestion, RawInputModifiers.Control, PhysicalKey.Slash, "/");
        Pump();
        Assert.Equal("SELECT 1\n  FROM t\n\nWHERE 1 = 1", editor.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Escape_StopsARunningScript_ButOnlyWhileOneRuns()
    {
        var (window, view, tab) = await Open(ScriptTabFixture.Endless);
        var editor = view.FindControl<TextEditor>("Editor")!;
        editor.TextArea.Focus();
        Pump();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);   // nothing runs: nothing happens
        Assert.False(tab.IsBusy);

        var run = tab.RunStatementAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!tab.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(tab.IsRunning);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(tab.IsRunning);
        Assert.True(tab.RunSummaryIsCancelled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CtrlDigit_SelectsTheResultTab()
    {
        var (window, view, tab) = await Open("SELECT 1; SELECT 2; SELECT 3");
        var editor = view.FindControl<TextEditor>("Editor")!;
        editor.TextArea.Focus();
        await tab.RunScriptAsync();
        Pump();
        Assert.Same(tab.ResultTabs[2], tab.SelectedResultTab);

        window.KeyPress(Key.D1, RawInputModifiers.Control, PhysicalKey.Digit1, "1");
        Pump();

        Assert.Same(tab.ResultTabs[0], tab.SelectedResultTab);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheMarks_FollowTheRun_AndGoAsSoonAsTheTextIsEdited()
    {
        var (window, view, tab) = await Open("SELECT 1;\nSELECT * FROM nope;");
        await tab.RunScriptAsync();
        Pump();
        var editor = view.FindControl<TextEditor>("Editor")!;
        var renderer = editor.TextArea.TextView.BackgroundRenderers.OfType<StatementMarkRenderer>().Single();
        Assert.True(renderer.HasMarks);

        editor.Document.Insert(0, "-- edited\n");
        Pump();

        Assert.False(renderer.HasMarks);
        window.Close();
    }

    // TappedEventArgs cannot be built without a real pointer: the handler only needs an instance to mark as handled.
    private static void DoubleTap(ScriptTabView view, Control splitter) =>
        view.GetType().GetMethod("OnSplitterDoubleTapped", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(view, [splitter, System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TappedEventArgs))]);

    [AvaloniaFact]
    public async Task DoubleClickOnTheSplitter_MaximizesTheResults_AndRestores()
    {
        var (window, view, _) = await Open("SELECT 1");
        var grid = view.FindControl<Grid>("SplitGrid")!;
        var splitter = view.FindControl<GridSplitter>("PaneSplitter")!;
        var before = grid.RowDefinitions[0].Height;

        DoubleTap(view, splitter);
        Pump();
        Assert.Equal(0, grid.RowDefinitions[0].Height.Value);
        Assert.Equal(0, grid.RowDefinitions[0].MinHeight);

        DoubleTap(view, splitter);
        Pump();
        Assert.Equal(before, grid.RowDefinitions[0].Height);
        Assert.Equal(80, grid.RowDefinitions[0].MinHeight);
        window.Close();
    }
}
