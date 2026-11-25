using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.Settings;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App.Tests.Upgrade;

/// <summary>The dbupdate folder as the Database upgrade page of the Settings dialog shows it.</summary>
public class SettingsDialogUpgradePageTests
{
    private static SettingsDialog Show(OptionsViewModel upgrade, out SettingsViewModel vm, out Func<bool> closed, Action<string?>? apply = null)
    {
        var ctx = new SettingsContext(new SettingsService(new())) { Upgrade = upgrade, ApplyDbUpdatePath = apply };
        vm = new SettingsViewModel(ctx, "upgrade");
        var window = new SettingsDialog { DataContext = vm };
        var isClosed = false;
        window.Closed += (_, _) => isClosed = true;
        closed = () => isClosed;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Button? ButtonNamed(Window window, string text) =>
        window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == text);

    private static TextBox PathBox(Window window) => window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PathBox");

    [AvaloniaFact]
    public void ShowsThePathBox_Browse_AndClose()
    {
        var upgrade = new OptionsViewModel(@"C:\dbupdate", _ => null);
        var window = Show(upgrade, out _, out _);

        Assert.Equal(@"C:\dbupdate", PathBox(window).Text);
        Assert.NotNull(ButtonNamed(window, "Choose folder…"));
        Assert.NotNull(ButtonNamed(window, "Close"));

        upgrade.DbUpdatePath = @"D:\other";   // the box follows the view model
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(@"D:\other", PathBox(window).Text);
        window.Close();
    }

    [AvaloniaFact]
    public void InvalidPath_ShowsTheError_AndCloseDoesNotClose()
    {
        var upgrade = new OptionsViewModel(null, _ => "Folder not found.") { DbUpdatePath = @"C:\nope" };
        var window = Show(upgrade, out _, out var closed);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Folder not found." && t.IsEffectivelyVisible);

        upgrade.ValidatePath();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Folder not found." && t.IsEffectivelyVisible);

        ButtonNamed(window, "Close")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.False(closed());
        Assert.Null(upgrade.Result);
        window.Close();
    }

    [AvaloniaFact]
    public void ValidPath_IsAppliedOnClose_AndInfoLineShows()
    {
        var upgrade = new OptionsViewModel(null, _ => null, _ => "147 version folders found") { DbUpdatePath = @"C:\dbupdate" };
        string? applied = null;
        var window = Show(upgrade, out _, out var closed, p => applied = p);

        upgrade.ValidatePath();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "147 version folders found" && t.IsEffectivelyVisible);

        ButtonNamed(window, "Close")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed());
        Assert.Equal(@"C:\dbupdate", upgrade.Result!.DbUpdatePath);
        Assert.Equal(@"C:\dbupdate", applied);
    }

    [AvaloniaFact]
    public void LeavingTheBox_AppliesAValidPathAtOnce()
    {
        var upgrade = new OptionsViewModel(null, _ => null);
        string? applied = null;
        var window = Show(upgrade, out _, out _, p => applied = p);

        PathBox(window).Focus();
        upgrade.DbUpdatePath = @"C:\dbupdate";
        Dispatcher.UIThread.RunJobs();
        window.FocusManager!.ClearFocus();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(@"C:\dbupdate", applied);
        window.Close();
    }

    [AvaloniaFact]
    public void Escape_ClosesWithoutAResult()
    {
        var upgrade = new OptionsViewModel(@"C:\dbupdate", _ => null);
        var window = Show(upgrade, out _, out var closed);

        window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed());
        Assert.Null(upgrade.Result);
    }
}
