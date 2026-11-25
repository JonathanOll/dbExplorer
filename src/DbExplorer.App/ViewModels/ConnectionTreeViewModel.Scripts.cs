using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.ViewModels;

/// <summary>The "Scripts" section of the navigator: the script library as real folders and .sql files.</summary>
public partial class ConnectionTreeViewModel
{
    private ScriptsSectionNodeViewModel? _scriptsSection;
    private readonly HashSet<string> _expandedScriptFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The script library behind the Scripts section; null (tests, no data folder) leaves the section out.</summary>
    public ScriptLibrary? Library { get; private set; }

    /// <summary>Opens a library script in a tab (wired to <see cref="MainViewModel"/>); activates the tab when the file is already open.</summary>
    public Func<string, Task>? ScriptFileOpener { get; set; }

    /// <summary>Puts text on the clipboard (wired by the window layer).</summary>
    public Func<string, Task>? CopyText { get; set; }

    /// <summary>Selects a file in the system file manager (wired by the window layer).</summary>
    public Action<string>? RevealInShell { get; set; }

    /// <summary>Opens a folder in the system file manager (wired by the window layer).</summary>
    public Action<string>? OpenFolderInShell { get; set; }

    /// <summary>
    /// Raised after a script or folder was renamed or moved (old path, new path) or deleted (old path, null) so open tabs can
    /// follow their file.
    /// </summary>
    public event Action<string, string?>? ScriptPathChanged;

    /// <summary>Gives the tree its script library and shows the Scripts section (only the directory listing is read).</summary>
    public void UseLibrary(ScriptLibrary library)
    {
        Library = library;
        Rebuild();
    }

    /// <summary>View menu: the Scripts section is shown.</summary>
    public bool ShowScripts => Config.ShowScripts;

    [RelayCommand]
    private void ToggleScripts()
    {
        Config.ShowScripts = !Config.ShowScripts;
        SaveSetting();
        OnPropertyChanged(nameof(ShowScripts));
        Rebuild();
    }

    /// <summary>Adds the section to the node list (called by <see cref="Rebuild"/>, last: it is the least connection-like part).</summary>
    private void AddScriptsSection()
    {
        if (Library is null || !Config.ShowScripts) return;
        _scriptsSection ??= new ScriptsSectionNodeViewModel { Tree = this, IsExpanded = true };
        _scriptsSection.Path = Library.Root;
        FillScriptsSection();
        Nodes.Add(_scriptsSection);
    }

    private void FillScriptsSection()
    {
        if (_scriptsSection is null || Library is null) return;
        var entries = ListSafely(Library.Root);
        _scriptsSection.Children = new ObservableCollection<TreeNodeViewModel>(ScriptNodes(entries));
        _scriptsSection.IsLibraryEmpty = entries.Count == 0;
    }

    private IReadOnlyList<LibraryEntry> ListSafely(string folder)
    {
        try
        {
            return Library!.List(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ScriptLibraryException)
        {
            _status.Error($"Cannot read the script folder {folder}: {ErrorText.Describe(e)}");
            return [];
        }
    }

    /// <summary>Nodes of one level. A folder lists its content when first expanded; one that was expanded before stays expanded.</summary>
    private IEnumerable<TreeNodeViewModel> ScriptNodes(IReadOnlyList<LibraryEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (!entry.IsFolder)
            {
                yield return new ScriptFileNodeViewModel(entry.Path) { Tree = this };
                continue;
            }
            var folder = new ScriptFolderNodeViewModel(entry.Path) { Tree = this, IsExpanded = _expandedScriptFolders.Contains(entry.Path) };
            var path = entry.Path;
            folder.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(TreeNodeViewModel.IsExpanded)) return;
                if (folder.IsExpanded) _expandedScriptFolders.Add(path); else _expandedScriptFolders.Remove(path);
            };
            folder.SetLazyChildren(() => [.. ScriptNodes(ListSafely(path))]);
            yield return folder;
        }
    }

    /// <summary>Re-reads the library into the Scripts section (expanded folders stay expanded).</summary>
    [RelayCommand]
    public void RefreshScripts()
    {
        if (_scriptsSection is null || Library is null || !Config.ShowScripts) return;
        var selected = (SelectedNode as ScriptFileNodeViewModel)?.Path ?? (SelectedNode as ScriptFolderNodeViewModel)?.Path;
        FillScriptsSection();
        ApplyFilter();
        if (selected is not null && FindScriptNode(selected) is { } again) SelectedNode = again;
    }

    /// <summary>The realized node of a library path (a collapsed folder's children do not exist yet), or null.</summary>
    public TreeNodeViewModel? FindScriptNode(string path)
    {
        return _scriptsSection is null ? null : Find(_scriptsSection);

        TreeNodeViewModel? Find(TreeNodeViewModel parent)
        {
            foreach (var child in parent.Children)
            {
                var childPath = (child as ScriptFileNodeViewModel)?.Path ?? (child as ScriptFolderNodeViewModel)?.Path;
                if (string.Equals(childPath, path, StringComparison.OrdinalIgnoreCase)) return child;
                if (child is ScriptFolderNodeViewModel && Find(child) is { } deeper) return deeper;
            }
            return null;
        }
    }

    // ---- commands ----

    private static string? PathOf(TreeNodeViewModel? node) => node switch
    {
        ScriptFileNodeViewModel f => f.Path,
        ScriptFolderNodeViewModel d => d.Path,
        ScriptsSectionNodeViewModel s => s.Path,
        _ => null,
    };

    /// <summary>The folder a "new here" command works in: the folder itself, the folder of a script, the library root otherwise.</summary>
    private string? TargetFolderOf(TreeNodeViewModel? node) => node switch
    {
        ScriptFolderNodeViewModel d => d.Path,
        ScriptFileNodeViewModel f => System.IO.Path.GetDirectoryName(f.Path),
        _ => Library?.Root,
    };

    /// <summary>Runs a library operation: a refusal or an IO problem goes to the status bar, never out as an exception.</summary>
    private bool Try(Func<string> operation, string what, out string result)
    {
        result = "";
        try
        {
            result = operation();
            return true;
        }
        catch (ScriptLibraryException e)
        {
            _status.Error(e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status.Error($"Cannot {what}: {ErrorText.Describe(e)}");
        }
        return false;
    }

    /// <summary>Double-click / Enter on a script: opens it (or goes to its tab).</summary>
    [RelayCommand]
    public async Task OpenScriptAsync(ScriptFileNodeViewModel? node)
    {
        if (node is null) return;
        if (ScriptFileOpener is null)
        {
            _status.Info("Opening scripts is not available.");
            return;
        }
        try
        {
            await ScriptFileOpener(node.Path);
        }
        catch (Exception e)
        {
            _status.Error($"Cannot open {node.Path}: {ErrorText.Describe(e)}");
        }
    }

    [RelayCommand]
    public async Task NewScriptHereAsync(TreeNodeViewModel? node)
    {
        if (Library is null || TargetFolderOf(node) is not { } folder) return;
        var name = await _dialogs.PromptTextAsync(new PromptRequest("New script", $"Name of the new script in {Describe(folder)}", "New script", "Create",
            n => ScriptLibrary.ValidateName(n)));
        if (name is null) return;
        if (!Try(() => Library.SaveNew(folder, name.Trim(), ""), "create the script", out var path)) return;
        ExpandToShow(folder);
        RefreshScripts();
        if (FindScriptNode(path) is { } created) SelectedNode = created;
        if (ScriptFileOpener is { } open)
        {
            try { await open(path); }
            catch (Exception e) { _status.Error($"Cannot open {path}: {ErrorText.Describe(e)}"); }
        }
    }

    [RelayCommand]
    public async Task NewScriptFolderAsync(TreeNodeViewModel? node)
    {
        if (Library is null || TargetFolderOf(node) is not { } parent) return;
        var name = await _dialogs.PromptTextAsync(new PromptRequest("New folder", $"Name of the new folder in {Describe(parent)}", "", "Create",
            n => ScriptLibrary.ValidateName(n)));
        if (name is null) return;
        if (!Try(() => Library.CreateFolder(parent, name.Trim()), "create the folder", out var path)) return;
        ExpandToShow(parent);
        RefreshScripts();
        if (FindScriptNode(path) is { } created) SelectedNode = created;
        _status.Success($"Created folder {name.Trim()}.");
    }

    [RelayCommand]
    public async Task RenameScriptNodeAsync(TreeNodeViewModel? node)
    {
        if (Library is null || node is not (ScriptFileNodeViewModel or ScriptFolderNodeViewModel) || PathOf(node) is not { } path) return;
        var isFolder = node is ScriptFolderNodeViewModel;
        var name = await _dialogs.PromptTextAsync(new PromptRequest(isFolder ? "Rename folder" : "Rename script", "New name", node.Title, "Rename",
            n => ScriptLibrary.ValidateName(n)));
        if (name is null || name.Trim() == node.Title) return;
        if (!Try(() => Library.Rename(path, name.Trim()), "rename", out var renamed)) return;
        MovedExpansion(path, renamed);
        RefreshScripts();
        if (FindScriptNode(renamed) is { } again) SelectedNode = again;
        ScriptPathChanged?.Invoke(path, renamed);
    }

    [RelayCommand]
    public async Task DeleteScriptNodeAsync(TreeNodeViewModel? node)
    {
        if (Library is null || node is not (ScriptFileNodeViewModel or ScriptFolderNodeViewModel) || PathOf(node) is not { } path) return;
        ConfirmRequest request;
        if (node is ScriptFolderNodeViewModel folder)
        {
            int count;
            try { count = Library.CountScripts(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ScriptLibraryException) { count = 0; }
            request = new ConfirmRequest("Delete folder", $"Delete the folder \"{folder.Title}\"" + (count > 0 ? $" and the {count} script{(count == 1 ? "" : "s")} in it?" : "?"),
                "Delete", "The files are deleted for good: they do not go to the recycle bin.", DialogTone.Danger);
        }
        else
        {
            request = new ConfirmRequest("Delete script", $"Delete the script \"{node.Title}\"?", "Delete",
                "The file is deleted for good: it does not go to the recycle bin.", DialogTone.Danger);
        }
        if (!await _dialogs.ConfirmAsync(request)) return;
        if (!Try(() => { Library.Delete(path, recursive: true); return path; }, "delete", out _)) return;
        _expandedScriptFolders.RemoveWhere(p => p.StartsWith(path, StringComparison.OrdinalIgnoreCase));
        RefreshScripts();
        _status.Info($"Deleted {node.Title}.");
        ScriptPathChanged?.Invoke(path, null);
    }

    [RelayCommand]
    public void DuplicateScript(ScriptFileNodeViewModel? node)
    {
        if (Library is null || node is null) return;
        if (!Try(() => Library.Duplicate(node.Path), "duplicate the script", out var copy)) return;
        RefreshScripts();
        if (FindScriptNode(copy) is { } created) SelectedNode = created;
        _status.Success($"Duplicated as {System.IO.Path.GetFileName(copy)}.");
    }

    /// <summary>"Move to folder…": asks for the target in a list of the library folders.</summary>
    [RelayCommand]
    public async Task MoveScriptNodeAsync(TreeNodeViewModel? node)
    {
        if (Library is null || node is not (ScriptFileNodeViewModel or ScriptFolderNodeViewModel) || PathOf(node) is not { } path) return;
        List<string?> targets = [];
        List<string> labels = [];
        var parent = System.IO.Path.GetDirectoryName(path);
        void Offer(string? target, string label)
        {
            if (string.Equals(target ?? Library.Root, parent, StringComparison.OrdinalIgnoreCase)) return;   // already there
            if (node is ScriptFolderNodeViewModel && target is not null
                && (string.Equals(target, path, StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith(path + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return;   // into itself
            targets.Add(target);
            labels.Add(label);
        }
        Offer(null, "Scripts (top level)");
        foreach (var f in Library.AllFolders()) Offer(f.Path, f.RelativeName);
        if (targets.Count == 0)
        {
            _status.Info("There is no other folder to move it to. Create one first.");
            return;
        }
        var choice = await _dialogs.ChooseAsync("Move to folder", $"Move \"{node.Title}\" to:", labels);
        if (choice is not { } index || index < 0 || index >= targets.Count) return;
        MoveScriptNode(path, targets[index]);
    }

    /// <summary>Moves a script or folder into a folder of the library (null = the top level). Used by the menu and by a drop; refusals go to the status bar.</summary>
    public bool MoveScriptNode(string path, string? targetFolder)
    {
        if (Library is null) return false;
        if (!Try(() => Library.Move(path, targetFolder), "move", out var moved)) return false;
        if (string.Equals(moved, path, StringComparison.OrdinalIgnoreCase)) return true;   // dropped where it already was
        MovedExpansion(path, moved);
        if (targetFolder is not null) ExpandToShow(targetFolder);
        RefreshScripts();
        if (FindScriptNode(moved) is { } again) SelectedNode = again;
        _status.Info($"Moved {System.IO.Path.GetFileNameWithoutExtension(moved)} to {Describe(targetFolder ?? Library.Root)}.");
        ScriptPathChanged?.Invoke(path, moved);
        return true;
    }

    [RelayCommand]
    public async Task CopyScriptPathAsync(TreeNodeViewModel? node)
    {
        if (PathOf(node) is not { } path || CopyText is null) return;
        try
        {
            await CopyText(path);
            _status.Info($"Copied {path}.");
        }
        catch (Exception e)
        {
            _status.Error($"Cannot copy to the clipboard: {ErrorText.Describe(e)}");
        }
    }

    [RelayCommand]
    public void ShowScriptInExplorer(TreeNodeViewModel? node)
    {
        if (PathOf(node) is not { } path) return;
        if (node is ScriptsSectionNodeViewModel) OpenLibraryFolder();
        else if (node is ScriptFolderNodeViewModel) OpenFolder(path);
        else RevealInShell?.Invoke(path);
    }

    /// <summary>Opens the library folder in the file manager (created first when it does not exist yet).</summary>
    [RelayCommand]
    public void OpenLibraryFolder()
    {
        if (Library is null) return;
        if (!Try(() => { Library.EnsureRoot(); return Library.Root; }, "create the script folder", out var root)) return;
        OpenFolder(root);
    }

    private void OpenFolder(string path)
    {
        try { OpenFolderInShell?.Invoke(path); }
        catch (Exception e) { _status.Error($"Cannot open {path}: {ErrorText.Describe(e)}"); }
    }

    // ---- helpers ----

    private string Describe(string folder) =>
        Library is not null && string.Equals(folder, Library.Root, StringComparison.OrdinalIgnoreCase)
            ? "the Scripts folder" : $"\"{System.IO.Path.GetFileName(folder)}\"";

    /// <summary>Remembers that <paramref name="folder"/> (and its ancestors) is open so the next refresh shows what was just created in it.</summary>
    private void ExpandToShow(string folder)
    {
        if (Library is null) return;
        for (var dir = folder; dir is not null && !string.Equals(dir, Library.Root, StringComparison.OrdinalIgnoreCase); dir = System.IO.Path.GetDirectoryName(dir))
        {
            _expandedScriptFolders.Add(dir);
            if (FindScriptNode(dir) is ScriptFolderNodeViewModel node) node.IsExpanded = true;
        }
    }

    /// <summary>A folder that moved keeps its expansion (and its sub-folders').</summary>
    private void MovedExpansion(string from, string to)
    {
        var moved = _expandedScriptFolders.Where(p => p.StartsWith(from, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var old in moved)
        {
            _expandedScriptFolders.Remove(old);
            _expandedScriptFolders.Add(to + old[from.Length..]);
        }
    }
}
