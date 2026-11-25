using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// "Save script": a name and a library folder. Save validates (the rules live in the view model), "Choose another location…"
/// ends the dialog so the caller shows the platform file picker. Closes with true when a name was taken or another location chosen.
/// </summary>
public partial class SaveScriptDialog : Window
{
    /// <summary>The left indent of a nested folder row.</summary>
    public static readonly IValueConverter IndentMargin = new FuncValueConverter<double, Thickness>(indent => new Thickness(indent, 0, 0, 0));

    public SaveScriptDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public SaveScriptDialog(SaveScriptViewModel viewModel) : this() => DataContext = viewModel;

    private SaveScriptViewModel? ViewModel => DataContext as SaveScriptViewModel;

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.TryConfirm() == true) Close(true);
        else NameBox.Focus();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnOtherLocationClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ChooseAnotherLocation();
        Close(true);
    }

    // The command opens the row; the focus goes to its box once it is visible.
    private void OnNewFolderClick(object? sender, RoutedEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => FolderBox.Focus(), Avalonia.Threading.DispatcherPriority.Background);

    // Enter creates the folder, Escape closes the row, neither reaches the dialog's own default / cancel buttons.
    private void OnFolderBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ViewModel?.TryCreateFolder();
        else if (e.Key == Key.Escape) ViewModel?.CancelNewFolderCommand.Execute(null);
        else return;
        e.Handled = true;
    }

    // A double click on a folder saves into it.
    private void OnFolderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is { SelectedFolder: not null }) OnOkClick(sender, e);
    }
}
