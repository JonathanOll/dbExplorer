using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.Tests.Upgrade;

/// <summary>The Upgrade dialog on a real window over a temp dbupdate tree and SQLite databases.</summary>
public class UpgradeDialogTests : IDisposable
{
    private readonly UpgradeFixture _f = new();
    private readonly TimeSpan _grace = UpgradeViewModel.CancelGrace;

    public void Dispose()
    {
        UpgradeViewModel.CancelGrace = _grace;
        _f.Dispose();
    }

    private static UpgradeDialog Show(UpgradeViewModel vm, out Func<bool> closed)
    {
        var window = new UpgradeDialog { DataContext = vm };
        var isClosed = false;
        window.Closed += (_, _) => isClosed = true;
        closed = () => isClosed;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static async Task PumpUntil(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static Button? ButtonNamed(Window window, string text) =>
        window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == text && b.IsEffectivelyVisible);

    private static bool ShowsText(Window window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);

    private static void Click(Button button)
    {
        Assert.True(button.IsEffectivelyEnabled, $"'{button.Content}' is disabled");
        // A click runs the button's command (the handlers of the Click event do not); a button without one has a Click handler.
        if (button.Command is { } command) command.Execute(null);
        else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task LoadSprint143(UpgradeDialog window, UpgradeViewModel vm)
    {
        window.FindControl<TextBox>("SprintBox")!.Text = "143";   // what typing does: the binding feeds the view model
        Dispatcher.UIThread.RunJobs();
        Click(ButtonNamed(window, "Load")!);
        await PumpUntil(() => vm.Phase == UpgradePhase.Preview);
    }

    [AvaloniaFact]
    public async Task TypingASprint_AndLoad_ShowsTheScripts_AndTheProductionBadge()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);
        var window = Show(vm, out _);
        Assert.False(ShowsText(window, "adj - 2 - x.sql"));

        await LoadSprint143(window, vm);

        Assert.Equal("143", vm.SprintText);
        Assert.True(ShowsText(window, "adj - 2 - x.sql"));
        Assert.True(ShowsText(window, "mch - 010 - Disable users - Production2.sql"));
        Assert.True(ShowsText(window, "Production2"));                       // the badge
        Assert.True(ShowsText(window, "1 warning: 1 index"));                // the summary banner
        Assert.Contains(window.GetVisualDescendants().OfType<CheckBox>(), c => c.IsChecked == false && c.IsEffectivelyVisible);
        Assert.NotNull(ButtonNamed(window, "Run..."));
        window.Close();
    }

    [AvaloniaFact]
    public async Task AnInvalidSprint_ShowsTheMessageBanner()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);
        var window = Show(vm, out _);
        Assert.False(ShowsText(window, "Enter a sprint number, for example 143."));

        window.FindControl<TextBox>("SprintBox")!.Text = "abc";
        Dispatcher.UIThread.RunJobs();
        Click(ButtonNamed(window, "Load")!);
        await PumpUntil(() => vm.Message is not null);

        Assert.True(ShowsText(window, "Enter a sprint number, for example 143."));
        Assert.Null(ButtonNamed(window, "Run..."));   // still in Setup
        window.Close();
    }

    [AvaloniaFact]
    public async Task GroupTarget_ShowsTheTypedConfirmation_RunStaysDisabledUntilTheTokenIsTyped()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A")), UpgradeFixture.Group("G", _f.Db("B"), _f.Db("C"))]);
        var window = Show(vm, out _);
        await LoadSprint143(window, vm);
        vm.SelectedTarget = vm.Targets[1];

        Click(ButtonNamed(window, "Run...")!);

        Assert.Equal(UpgradePhase.Confirm, vm.Phase);
        Assert.True(ShowsText(window, "Upgrade 9 scripts on:"));
        Assert.True(ShowsText(window, "B"));
        Assert.True(ShowsText(window, "C"));
        var box = window.FindControl<TextBox>("ConfirmBox")!;
        Assert.True(box.IsEffectivelyVisible);
        var run = ButtonNamed(window, "Run")!;
        Assert.False(run.IsEffectivelyEnabled);

        box.Text = "143";
        Dispatcher.UIThread.RunJobs();
        Assert.True(run.IsEffectivelyEnabled);

        Click(ButtonNamed(window, "Back")!);
        Assert.Equal(UpgradePhase.Preview, vm.Phase);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SingleDatabase_Confirm_HasNoTypedBox()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);
        var window = Show(vm, out _);
        await LoadSprint143(window, vm);

        Click(ButtonNamed(window, "Run...")!);

        Assert.False(window.FindControl<TextBox>("ConfirmBox")!.IsEffectivelyVisible);
        Assert.True(ButtonNamed(window, "Run")!.IsEffectivelyEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Run_ShowsOneRowPerDatabase_ThenCopyLogAndClose()
    {
        var copied = new List<string>();
        var vm = _f.Vm([UpgradeFixture.Group("G", _f.Db("Alpha"), _f.Db("Beta"))], copy: t => { copied.Add(t); return Task.CompletedTask; });
        var window = Show(vm, out var closed);
        await LoadSprint143(window, vm);
        Click(ButtonNamed(window, "Run...")!);
        window.FindControl<TextBox>("ConfirmBox")!.Text = "143";
        Dispatcher.UIThread.RunJobs();

        Click(ButtonNamed(window, "Run")!);
        await PumpUntil(() => vm.Phase == UpgradePhase.Finished);

        Assert.True(ShowsText(window, "Alpha"));
        Assert.True(ShowsText(window, "Beta"));
        Assert.True(ShowsText(window, "2 of 2 databases done."));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text!.StartsWith("done - 9 scripts"));

        Click(ButtonNamed(window, "Copy log")!);
        await PumpUntil(() => copied.Count > 0);
        Assert.Equal([vm.Log], copied);

        Click(ButtonNamed(window, "Close")!);
        Assert.True(closed());
    }

    [AvaloniaFact]
    public async Task ClosingWhileRunning_DoesNotClose_ItCancelsAll()
    {
        UpgradeViewModel.CancelGrace = TimeSpan.FromMilliseconds(100);
        _f.Override = (_, _) => new TaskCompletionSource<Session?>().Task;   // never connects
        var vm = _f.Vm([UpgradeFixture.Group("G", _f.Db("A"), _f.Db("B"))]);
        var window = Show(vm, out var closed);
        await LoadSprint143(window, vm);
        Click(ButtonNamed(window, "Run...")!);
        window.FindControl<TextBox>("ConfirmBox")!.Text = "143";
        Dispatcher.UIThread.RunJobs();
        var run = vm.ConfirmRunCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(UpgradePhase.Running, vm.Phase);
        Assert.NotNull(ButtonNamed(window, "Cancel all"));
        Assert.False(ButtonNamed(window, "Close")?.IsEffectivelyEnabled ?? false);

        window.Close();   // the X button
        Dispatcher.UIThread.RunJobs();

        Assert.False(closed());                  // refused while running...
        await PumpUntil(() => run.IsCompleted);  // ...and it cancelled everything
        Assert.Equal(UpgradePhase.Finished, vm.Phase);
        Assert.All(vm.Members, m => Assert.Equal(MemberState.Cancelled, m.State));

        window.Close();                          // now it can close
        Dispatcher.UIThread.RunJobs();
        Assert.True(closed());
    }
}
