using Avalonia.Controls;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App;

/// <summary>Real <see cref="IEditDialogs"/>: modal windows owned by the main window.</summary>
public sealed class EditDialogService(Window owner) : IEditDialogs
{
    public async Task<SaveChoice> AskSaveDiscardAsync(string title, string message)
    {
        var save = DialogShell.Button("Save…", isDefault: true, primary: true);
        var discard = DialogShell.Button("Discard");
        var cancel = DialogShell.Button("Cancel", isCancel: true);
        var window = DialogShell.Create(title, "IconSave", DialogShell.Message(message), [save, discard, cancel]);
        save.Click += (_, _) => window.Close(SaveChoice.Save);
        discard.Click += (_, _) => window.Close(SaveChoice.Discard);
        cancel.Click += (_, _) => window.Close(SaveChoice.Cancel);
        return await window.ShowDialog<SaveChoice>(owner);   // the X closes with the default value, Cancel
    }

    public async Task<bool> ShowSqlPreviewAsync(IReadOnlyList<string> statements, string target) =>
        await new SqlPreviewDialog(statements, target).ShowDialog<bool>(owner);

    public async Task<string[]?> ShowVirtualKeyDialogAsync(VirtualKeyDialogViewModel viewModel) =>
        await new VirtualKeyDialog(viewModel).ShowDialog<string[]?>(owner);
}
