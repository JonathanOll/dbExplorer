using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>ER diagram tabs.</summary>
public partial class MainViewModel
{
    /// <summary>
    /// Opens the ER diagram of a connection (whole database, one schema, or around one table); the tab already opened on the
    /// same thing is activated instead. The connection is connected first when needed; failures show in the tab.
    /// </summary>
    public DiagramTabViewModel OpenDiagram(ConnectionConfig connection, DiagramRequest request)
    {
        var shown = Tabs.OfType<DiagramTabViewModel>()
            .FirstOrDefault(t => t.Connection.Id == connection.Id && t.Request == request);
        if (shown is not null)
        {
            ActiveTab = shown;
            return shown;
        }

        var tab = new DiagramTabViewModel(connection, request, new DiagramHooks(
            ct => ResolveScriptSessionAsync(connection.Id, ct),
            (session, table) => OpenTable(session, table)));
        Tabs.Add(tab);
        ActiveTab = tab;
        _ = tab.LoadAsync();
        return tab;
    }
}
