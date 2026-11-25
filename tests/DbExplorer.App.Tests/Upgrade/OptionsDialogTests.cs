using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App.Tests.Upgrade;

public class OptionsDialogTests
{
    private static OptionsDialog Show(OptionsViewModel vm, out Func<bool> closed)
    {
        var window = new OptionsDialog { DataContext = vm };
        var isClosed = false;
        window.Closed += (_, _) => isClosed = true;
        closed = () => isClosed;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Button? ButtonNamed(Window window, string text) =>
        window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == text);

    [AvaloniaFact]
    public void ShowsThePathBox_Browse_Save_AndCancel()
    {
        var vm = new OptionsViewModel(@"C:\dbupdate", _ => null);
        var window = Show(vm, out _);

        var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PathBox");
        Assert.Equal(@"C:\dbupdate", box.Text);
        Assert.NotNull(ButtonNamed(window, "Choose folder…"));
        Assert.NotNull(ButtonNamed(window, "Save"));
        Assert.NotNull(ButtonNamed(window, "Cancel"));

        vm.DbUpdatePath = @"D:\other";   // the box follows the view model
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(@"D:\other", box.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void InvalidPath_ShowsTheErrorBanner_AndOkDoesNotClose()
    {
        var vm = new OptionsViewModel(null, _ => "Folder not found.") { DbUpdatePath = @"C:\nope" };
        var window = Show(vm, out var closed);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Folder not found." && t.IsEffectivelyVisible);

        vm.ValidatePath();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Folder not found." && t.IsEffectivelyVisible);

        ButtonNamed(window, "Save")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.False(closed());
        Assert.Null(vm.Result);
        window.Close();
    }

    [AvaloniaFact]
    public void ValidPath_SaveConfirmsAndCloses_AndInfoLineShows()
    {
        var vm = new OptionsViewModel(null, _ => null, _ => "147 version folders found") { DbUpdatePath = @"C:\dbupdate" };
        var window = Show(vm, out var closed);

        vm.ValidatePath();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "147 version folders found" && t.IsEffectivelyVisible);

        ButtonNamed(window, "Save")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed());
        Assert.Equal(@"C:\dbupdate", vm.Result!.DbUpdatePath);
    }

    [AvaloniaFact]
    public void Cancel_ClosesWithoutAResult()
    {
        var vm = new OptionsViewModel(@"C:\dbupdate", _ => null);
        var window = Show(vm, out var closed);

        ButtonNamed(window, "Cancel")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed());
        Assert.Null(vm.Result);
    }
}
