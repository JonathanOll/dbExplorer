using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App;

/// <summary>Real <see cref="IScriptFiles"/>: the platform file pickers of the main window.</summary>
public sealed class ScriptFiles(Window owner) : IScriptFiles
{
    private static readonly FilePickerFileType SqlFiles = new("SQL scripts") { Patterns = ["*.sql"] };
    private static readonly FilePickerFileType AllFiles = new("All files") { Patterns = ["*"] };

    public async Task<string?> PickSaveAsync(string suggestedName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save script",
            SuggestedFileName = suggestedName,
            DefaultExtension = "sql",
            FileTypeChoices = [SqlFiles, AllFiles],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickExportAsync(string suggestedName, string extension)
    {
        var type = new FilePickerFileType(extension.ToUpperInvariant() + " files") { Patterns = [$"*.{extension}"] };
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export result",
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = [type, AllFiles],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open script",
            AllowMultiple = false,
            FileTypeFilter = [SqlFiles, AllFiles],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
