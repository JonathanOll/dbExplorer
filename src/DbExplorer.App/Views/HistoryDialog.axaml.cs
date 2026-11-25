using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// The query history window. The logic is in <see cref="HistoryViewModel"/>; this only wires the keys: Enter opens the selected
/// statement, Delete removes it (not while typing in the search box), Down in the search box goes to the list, Esc closes.
/// </summary>
public partial class HistoryDialog : Window
{
    public HistoryDialog()
    {
        InitializeComponent();
        Opened += (_, _) => SearchBox.Focus();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is HistoryViewModel vm) vm.CloseRequested += (_, _) => Close();
        };
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    private HistoryViewModel? ViewModel => DataContext as HistoryViewModel;

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || e.KeyModifiers != KeyModifiers.None) return;
        var typing = e.Source is TextBox { IsReadOnly: false };
        switch (e.Key)
        {
            case Key.Enter when vm.OpenCommand.CanExecute(null) && e.Source is not Button:
                vm.OpenCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete when !typing && vm.DeleteCommand.CanExecute(null):
                vm.DeleteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down when typing && vm.HasItems:
                List.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is { } vm && vm.OpenCommand.CanExecute(null)) vm.OpenCommand.Execute(null);
    }

    private void OnClearSearchClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) vm.SearchText = "";
        SearchBox.Focus();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
