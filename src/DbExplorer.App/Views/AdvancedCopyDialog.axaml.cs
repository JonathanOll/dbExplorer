using Avalonia.Controls;
using Avalonia.Interactivity;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>Closes with the text to copy, or null; the clipboard is written by the grid that opened it.</summary>
public partial class AdvancedCopyDialog : Window
{
    public AdvancedCopyDialog() => InitializeComponent();

    private void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AdvancedCopyViewModel vm && vm.Copy() is { } text) Close(text);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
