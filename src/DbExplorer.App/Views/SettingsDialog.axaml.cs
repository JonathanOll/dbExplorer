using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// The Settings dialog. Settings apply as they change, so there is only "Close": it first keeps a pending dbupdate folder (and
/// stays open on that page when the folder is not usable). Esc clears the search, then closes without that check.
/// </summary>
public partial class SettingsDialog : Window
{
    public SettingsDialog()
    {
        InitializeComponent();
        Opened += (_, _) => SearchBox.Focus();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.TryCommitPending() == false) return;
        Close(true);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e is { Key: Key.F, KeyModifiers: KeyModifiers.Control })
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e is { Key: Key.Escape, KeyModifiers: KeyModifiers.None } && !e.Handled)
        {
            e.Handled = true;
            if (ViewModel is { HasSearch: true } vm) vm.SearchText = "";
            else Close(false);
        }
    }
}
