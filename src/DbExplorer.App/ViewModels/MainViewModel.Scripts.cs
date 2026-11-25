using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.ViewModels;

/// <summary>One line of the File > Recent scripts menu.</summary>
/// <param name="Path">Full path of the script file (the tooltip).</param>
public sealed record RecentScriptItem(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>The folder, as a second line of context.</summary>
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

/// <summary>The script library and the recent scripts: opening, the File menu entries and keeping open tabs in step with the files.</summary>
public partial class MainViewModel
{
    private ScriptLibrary? _library;
    private RecentScripts? _recent;

    /// <summary>The recent scripts the File menu shows; refreshed by <see cref="RefreshRecentScripts"/> when the submenu opens.</summary>
    public ObservableCollection<RecentScriptItem> RecentItems { get; } = [];

    public bool HasRecent => RecentItems.Count > 0;

    /// <summary>
    /// Gives the shell its script library (the Scripts section of the navigator, the "Save script" dialog) and the recent list.
    /// Only stores them: the directory is listed by the tree, the recent file is read when the menu first opens.
    /// </summary>
    public void UseScriptLibrary(ScriptLibrary library, RecentScripts recent)
    {
        _library = library;
        _recent = recent;
        Tree.ScriptFileOpener = OpenScriptPathAsync;
        Tree.ScriptPathChanged += OnLibraryPathChanged;
        Tree.UseLibrary(library);
    }

    /// <summary>Re-reads the recent list into <see cref="RecentItems"/> (files that are gone are dropped).</summary>
    public void RefreshRecentScripts()
    {
        RecentItems.Clear();
        if (_recent is null) return;
        foreach (var path in _recent.Items) RecentItems.Add(new RecentScriptItem(path));
        OnPropertyChanged(nameof(HasRecent));
    }

    /// <summary>A script file was opened or saved: remember it, and show it in the Scripts section when it lives in the library.</summary>
    private void OnScriptFileUsed(string path)
    {
        _recent?.Add(path);
        if (_library is { } library && library.Contains(path)) Tree.RefreshScripts();
    }

    [RelayCommand]
    private async Task OpenRecentScriptAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (!File.Exists(path))
        {
            _recent?.Remove(path);
            Status.Error($"{path} no longer exists: it was removed from the recent scripts.");
            return;
        }
        await OpenScriptPathAsync(path);
    }

    [RelayCommand]
    private void ClearRecentScripts()
    {
        _recent?.Clear();
        RefreshRecentScripts();
        Status.Info("Recent scripts cleared.");
    }

    [RelayCommand]
    private void OpenLibraryFolder() => Tree.OpenLibraryFolderCommand.Execute(null);

    /// <summary>File > Save script as…: always the file picker.</summary>
    [RelayCommand]
    private Task SaveScriptAsAsync() => ActiveScript is { } script ? script.SaveAsAsync() : Task.CompletedTask;

    /// <summary>
    /// Opens a script file on the active (or selected) connection: goes to its tab when it is already open, else reuses the empty
    /// script tab in front, else opens a new tab. Failures end up in the status bar.
    /// </summary>
    public async Task OpenScriptPathAsync(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var open = Tabs.OfType<ScriptTabViewModel>()
                .FirstOrDefault(t => t.FilePath is { } p && string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase));
            if (open is not null)
            {
                ActiveTab = open;
                return;
            }
            if ((ScriptTarget() ?? LibraryScriptTarget()) is not { } connection)
            {
                Status.Info("Select a connection in the tree or open a tab first, then open the script.");
                return;
            }

            var tab = ActiveTab is ScriptTabViewModel { FilePath: null } empty && string.IsNullOrWhiteSpace(empty.Text) ? empty : null;
            if (tab is null)
            {
                tab = CreateScriptTab(connection, null);
                Tabs.Add(tab);
            }
            ActiveTab = tab;
            await tab.OpenAsync(full);
        }
        catch (Exception e)
        {
            Status.Error($"Cannot open the script: {ErrorText.Describe(e)}");
        }
    }

    /// <summary>
    /// The connection a library script opens on when nothing selects one (a script node of the tree is not a connection): the
    /// last one selected, else the only one there is.
    /// </summary>
    private ConnectionConfig? LibraryScriptTarget()
    {
        var connections = Tree.Config.Connections;
        if (Tree.LastSelectedConnection is { } last && connections.FirstOrDefault(c => c.Id == last.Id) is { } current) return current;
        return connections.Count == 1 ? connections[0] : null;
    }

    /// <summary>A script or folder of the library was renamed, moved (new path) or deleted (null): the tabs on it follow.</summary>
    private void OnLibraryPathChanged(string oldPath, string? newPath)
    {
        foreach (var tab in Tabs.OfType<ScriptTabViewModel>())
        {
            if (tab.FilePath is not { } file) continue;
            if (string.Equals(file, oldPath, StringComparison.OrdinalIgnoreCase)) tab.FollowFile(newPath);
            else if (file.StartsWith(oldPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                tab.FollowFile(newPath is null ? null : newPath + file[oldPath.Length..]);
        }
    }
}
