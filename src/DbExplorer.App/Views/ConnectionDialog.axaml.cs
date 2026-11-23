using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

public partial class ConnectionDialog : Window
{
    public ConnectionDialog() => InitializeComponent();

    private ConnectionDialogViewModel? ViewModel => DataContext as ConnectionDialogViewModel;

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.TryConfirm() == true) Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select SQLite database file",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("SQLite database") { Patterns = ["*.db", "*.sqlite", "*.sqlite3", "*.db3"] },
                    FilePickerFileTypes.All,
                ],
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) vm.Database = path;
        }
        catch (Exception ex)
        {
            vm.Error = $"Cannot open the file picker: {ex.Message}";
        }
    }
}

