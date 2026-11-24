using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>Application options (today the dbupdate folder). OK validates (the rules live in the view model), Cancel closes.</summary>
public partial class OptionsDialog : Window
{
    public OptionsDialog()
    {
        InitializeComponent();
    }

    private OptionsViewModel? ViewModel => DataContext as OptionsViewModel;

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.TryConfirm() == true) Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnPathLostFocus(object? sender, RoutedEventArgs e) => ViewModel?.ValidatePath();

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
