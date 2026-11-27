using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

public partial class ConnectionDialog : Window
{
    public ConnectionDialog()
    {
        InitializeComponent();
        // The engine tiles are ListBox items dressed as selectable cards (ListBoxItem.choice, see docs/ui/README.md).
        EngineList.ContainerPrepared += (_, e) => e.Container.Classes.Add("choice");
        Opened += (_, _) =>
        {
            CapHeight();
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private ConnectionDialogViewModel? ViewModel => DataContext as ConnectionDialogViewModel;

    /// <summary>The form is tall (SQL Server shows the most): never taller than the screen it opens on, the body scrolls instead.</summary>
    private void CapHeight()
    {
        if (Screens.ScreenFromWindow(this) is { } screen)
        {
            MaxHeight = Math.Max(420, screen.WorkingArea.Height / screen.Scaling - 24);
            if (Bounds.Height > MaxHeight)
            {
                SizeToContent = SizeToContent.Manual;   // the content no longer fits: fix the height, the body scrolls
                Height = MaxHeight;
            }
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.TryConfirm() == true)
        {
            Close(true);
            return;
        }

        // Land on the first field that needs attention.
        var first = ViewModel switch
        {
            { NameError: not null } => NameBox,
            { HostError: not null } => HostBox,
            { PortError: not null } => HostBox,
            { DatabaseError: not null } => DatabaseBox,
            { AuthError: not null } => AuthBox,
            { UserError: not null } => UserBox,
            { SshHostError: not null } => SshHostBox,
            { SshPortError: not null } => SshPortBox,
            { SshUserError: not null } => SshUserBox,
            { SshKeyError: not null } => SshKeyBox,
            _ => (Control?)null,
        };
        first?.Focus();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnRevealClick(object? sender, RoutedEventArgs e)
    {
        var shown = RevealButton.IsChecked == true;
        PasswordBox.RevealPassword = shown;
        RevealButton.Content = shown ? "Hide" : "Show";
        Avalonia.Automation.AutomationProperties.SetName(RevealButton, shown ? "Hide password" : "Show password");
    }

    private async void OnBrowseKeyClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        try
        {
            var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select private key file",
                AllowMultiple = false,
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(ssh),
                FileTypeFilter = [FilePickerFileTypes.All],
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) vm.SshKeyFile = path;
        }
        catch (Exception ex)
        {
            vm.Error = $"Cannot open the file picker: {ex.Message}";
        }
    }

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
