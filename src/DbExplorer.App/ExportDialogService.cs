using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App;

/// <summary>Real <see cref="IExportDialogs"/>: modal windows and the platform file picker of the main window.</summary>
public sealed class ExportDialogService(Window owner) : IExportDialogs
{
    private Window? _dialog;   // the file picker opens over the dialog that asks for it

    public async Task<string?> ShowAdvancedCopyAsync(AdvancedCopyViewModel viewModel) =>
        await new AdvancedCopyDialog { DataContext = viewModel }.ShowDialog<string?>(owner);

    public async Task ShowExportAsync(ExportDialogViewModel viewModel)
    {
        var dialog = new ExportDialog { DataContext = viewModel };
        _dialog = dialog;
        try { await dialog.ShowDialog(owner); }
        finally { _dialog = null; }
    }

    public async Task<string?> PickFileAsync(string suggestedName, string extension)
    {
        var type = new FilePickerFileType(extension.ToUpperInvariant() + " files") { Patterns = [$"*.{extension}"] };
        var file = await (_dialog ?? owner).StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export rows",
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = [type, new FilePickerFileType("All files") { Patterns = ["*"] }],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }
}
