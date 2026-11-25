using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>Application options (today the dbupdate folder). Save validates (the rules live in the view model), Cancel closes.</summary>
public partial class OptionsDialog : Window
{
    public OptionsDialog()
    {
        InitializeComponent();
        Opened += (_, _) => PathBox.Focus();
        // The folder is checked shortly after the last keystroke (off the UI thread: it may be a share), so the answer is already
        // under the box when the user is done typing.
        PathBox.TextChanged += (_, _) => _ = ViewModel?.ValidateSoonAsync();
    }

    private OptionsViewModel? ViewModel => DataContext as OptionsViewModel;

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.TryConfirm() == true) Close(true);
        else PathBox.Focus();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "dbupdate folder",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        vm.DbUpdatePath = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
        vm.ValidatePath();
    }
}
