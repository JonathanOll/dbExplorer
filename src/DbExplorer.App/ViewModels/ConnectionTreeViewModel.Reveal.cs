using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>"Show active item in the navigator" (Visual Studio's "Sync with Active Document"): expands down to the node of a tab.</summary>
public partial class ConnectionTreeViewModel
{
    /// <summary>Raised after a node was revealed and selected; the view scrolls it into sight once its row exists.</summary>
    public event Action<TreeNodeViewModel>? NodeRevealed;

    /// <summary>
    /// Expands the folders and schemas down to what <paramref name="tab"/> is on and selects it: a table tab
    /// its table, a script saved in the library its file, any other tab its connection (a connection that is not open is
    /// only selected: its tables do not exist yet). Clears a filter that hides the node. Returns false when there is nothing to show.
    /// </summary>
    public bool RevealTab(TabViewModel? tab)
    {
        if (tab is null)
        {
            _status.Info("Open a tab first: the navigator shows where the active one lives.");
            return false;
        }

        var target = tab switch
        {
            ScriptTabViewModel { FilePath: { } path } when RevealScript(path) is { } script => script,
            TableTabViewModel table => RevealTable(table),
            _ => RevealConnection(tab.Connection.Id),
        };
        if (target is null)
        {
            _status.Info($"'{tab.Title}' is not in the navigator.");
            return false;
        }

        if (FilterText.Length > 0 && !target.IsShown) FilterText = "";   // a filter must not hide what was asked for
        SelectedNode = target;
        NodeRevealed?.Invoke(target);
        return true;
    }

    private ConnectionNodeViewModel? RevealConnection(Guid id)
    {
        if (!_connectionNodes.TryGetValue(id, out var node)) return null;
        // The folder only exists as a node while folders are shown.
        if (Config.ShowFolders && node.Config.Folder is { Length: > 0 } folder && _folderNodes.TryGetValue(folder, out var folderNode))
            folderNode.IsExpanded = true;
        return node;
    }

    private TreeNodeViewModel? RevealTable(TableTabViewModel tab)
    {
        var connection = RevealConnection(tab.Connection.Id);
        if (connection is not { IsConnected: true }) return connection;

        connection.IsExpanded = true;   // creates the children of a lazily filled node
        var schema = tab.Table.Schema ?? "";
        foreach (var child in connection.Children)
        {
            if (child is TableNodeViewModel direct && direct.Table.Ref == tab.Table) return direct;
            if (child is not SchemaNodeViewModel schemaNode || !string.Equals(schemaNode.Title, schema, StringComparison.OrdinalIgnoreCase)) continue;
            schemaNode.IsExpanded = true;
            return schemaNode.Children.OfType<TableNodeViewModel>().FirstOrDefault(t => t.Table.Ref == tab.Table) ?? (TreeNodeViewModel)schemaNode;
        }
        return connection;
    }

    private TreeNodeViewModel? RevealScript(string path)
    {
        if (_scriptsSection is not { } section || Library is not { } library || !Config.ShowScripts) return null;
        var relative = System.IO.Path.GetRelativePath(library.Root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative)) return null;

        TreeNodeViewModel current = section;
        var walked = library.Root;
        foreach (var segment in relative.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
        {
            current.IsExpanded = true;
            current.RealizeChildren();
            walked = System.IO.Path.Combine(walked, segment);
            var next = current.Children.FirstOrDefault(c =>
                string.Equals((c as ScriptFileNodeViewModel)?.Path ?? (c as ScriptFolderNodeViewModel)?.Path, walked, StringComparison.OrdinalIgnoreCase));
            if (next is null) return null;
            current = next;
        }
        return current;
    }
}
