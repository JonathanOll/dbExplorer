using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App;

/// <summary>Real <see cref="IDialogService"/>: modal windows owned by the main window.</summary>
public sealed class DialogService(Window owner) : IDialogService
{
    public async Task<bool> ShowConnectionDialogAsync(ConnectionDialogViewModel viewModel)
    {
        var dialog = new ConnectionDialog { DataContext = viewModel };
        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task<string?> PromptTextAsync(string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, MinWidth = 300 };
        var ok = DialogShell.Button("OK", isDefault: true, primary: true);
        var cancel = DialogShell.Button("Cancel", isCancel: true);
        var heading = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) };
        heading.Classes.Add("dim");
        var window = DialogShell.Create(title, "IconFolderAdd", new StackPanel { Children = { heading, box } }, [ok, cancel]);
        ok.Click += (_, _) => window.Close(box.Text);
        cancel.Click += (_, _) => window.Close(null);
        window.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        return await window.ShowDialog<string?>(owner);
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var yes = DialogShell.Button("Yes", isDefault: true, primary: true);
        var no = DialogShell.Button("No", isCancel: true);
        var window = DialogShell.Create(title, "IconWarning", DialogShell.Message(message), [yes, no]);
        yes.Click += (_, _) => window.Close(true);
        no.Click += (_, _) => window.Close(false);
        return await window.ShowDialog<bool>(owner);
    }
}
