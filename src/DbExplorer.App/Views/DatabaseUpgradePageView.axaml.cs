using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// The dbupdate folder box. The folder may be a network share, so it is checked off the UI thread shortly after typing stops, and
/// kept (validated) when the box loses focus, on Enter and after Browse: apply-on-change like the other pages.
/// </summary>
public partial class DatabaseUpgradePageView : UserControl
{
    public DatabaseUpgradePageView()
    {
        InitializeComponent();
        PathBox.TextChanged += OnPathTextChanged;
        PathBox.LostFocus += (_, _) => Page?.Commit();
        PathBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Page?.Commit();
        };
        BrowseButton.Click += OnBrowseClick;
    }

    private DatabaseUpgradeSettingsPage? Page => DataContext as DatabaseUpgradeSettingsPage;

    private async void OnPathTextChanged(object? sender, TextChangedEventArgs e)
    {
        try
        {
            if (Page?.Options is { } options) await options.ValidateSoonAsync();
        }
        catch (Exception)
        {
            // the check reports its own problems under the box; nothing here may reach the dispatcher as an unhandled fault
        }
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Page is not { } page || TopLevel.GetTopLevel(this) is not { } top) return;
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "dbupdate folder", AllowMultiple = false });
            if (folders.Count == 0) return;
            page.Options.DbUpdatePath = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
            page.Commit();
        }
        catch (Exception)
        {
            // the picker is a convenience: typing the path still works
        }
    }
}
