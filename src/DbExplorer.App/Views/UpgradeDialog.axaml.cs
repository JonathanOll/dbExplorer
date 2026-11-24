using Avalonia.Controls;
using Avalonia.Interactivity;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// The Upgrade dialog: sprint, preview, confirmation and the per-database status of the run, all in one window (the phase lives in
/// <see cref="UpgradeViewModel"/>). Closing it while a run is in progress is refused and cancels the run instead.
/// </summary>
public partial class UpgradeDialog : Window
{
    public UpgradeDialog()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not UpgradeViewModel { CanClose: false } vm) return;
        e.Cancel = true;   // a run in progress is not abandoned by closing the window: Cancel all, then close
        if (vm.CancelAllCommand.CanExecute(null)) vm.CancelAllCommand.Execute(null);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
