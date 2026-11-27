using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

// Folder order: Config.Folders is the order of the navigator; folders known only through connections come after it.
public partial class ConnectionTreeViewModel
{
    /// <summary>Every folder in the order the navigator shows them.</summary>
    private List<string> FolderOrder() =>
        Config.Folders.Concat(Config.Connections.Select(c => c.Folder)
                .Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static int IndexOfFolder(List<string> folders, string name) =>
        folders.FindIndex(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Stores the shown order, so folders that only connections knew about get a place of their own.</summary>
    private void StoreFolderOrder(List<string> order)
    {
        Config.Folders.Clear();
        Config.Folders.AddRange(order);
    }

    /// <summary>True when placing <paramref name="name"/> before or after <paramref name="target"/> changes the order.</summary>
    public bool CanMoveFolder(string name, string target, bool after) => ReorderedFolders(name, target, after) is not null;

    /// <summary>
    /// Places a folder just before or after another one and saves the order; the selection is kept. False when nothing
    /// changes (unknown folder, itself, or already there). Also used by drag and drop.
    /// </summary>
    public bool MoveFolder(string name, string target, bool after)
    {
        if (ReorderedFolders(name, target, after) is not { } order) return false;
        var selected = SelectedNode;
        StoreFolderOrder(order);
        Persist();
        // Rebuilding the list makes the tree view drop its selection; the node objects themselves are reused.
        if (selected is not null && Nodes.Any(n => ReferenceEquals(n, selected) || Contains(n, selected))) SelectedNode = selected;
        return true;
    }

    private List<string>? ReorderedFolders(string name, string target, bool after)
    {
        var order = FolderOrder();
        var from = IndexOfFolder(order, name);
        var to = IndexOfFolder(order, target);
        if (from < 0 || to < 0 || from == to) return null;
        var result = new List<string>(order);
        result.RemoveAt(from);
        result.Insert(IndexOfFolder(result, target) + (after ? 1 : 0), order[from]);
        return result.SequenceEqual(order) ? null : result;
    }

    /// <summary>
    /// Folder context menu "Set environment of its connections…": a one-off change of every connection of the folder (the
    /// folder itself stores nothing, so a connection moved there later keeps its own).
    /// </summary>
    [RelayCommand]
    private async Task SetFolderEnvironmentAsync(FolderNodeViewModel? node)
    {
        if (node is null) return;
        bool InFolder(ConnectionConfig c) => string.Equals(c.Folder, node.Name, StringComparison.OrdinalIgnoreCase);
        var count = Config.Connections.Count(InFolder);
        if (count == 0)
        {
            _status.Info($"Folder '{node.Name}' has no connection.");
            return;
        }
        var choice = await _dialogs.ChooseAsync("Set environment",
            $"Environment of the {(count == 1 ? "connection" : $"{count} connections")} in '{node.Name}'",
            [.. DbEnvironments.All.Select(e => e.Label())]);
        if (choice is not { } index) return;
        var environment = DbEnvironments.All[index];
        foreach (var connection in Config.Connections.Where(InFolder).ToList())
            UpdateConnection(connection with { Environment = environment });
    }

    /// <summary>Folder context menu "Move up".</summary>
    [RelayCommand]
    private void MoveFolderUp(FolderNodeViewModel? node) => MoveFolderBy(node, -1);

    /// <summary>Folder context menu "Move down".</summary>
    [RelayCommand]
    private void MoveFolderDown(FolderNodeViewModel? node) => MoveFolderBy(node, 1);

    private void MoveFolderBy(FolderNodeViewModel? node, int step)
    {
        if (node is null) return;
        var order = FolderOrder();
        var index = IndexOfFolder(order, node.Name);
        if (index < 0 || index + step < 0 || index + step >= order.Count) return;
        MoveFolder(node.Name, order[index + step], after: step > 0);
    }
}
