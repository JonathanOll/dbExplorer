using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>"ER diagram" in the context menus of connections, schemas and tables.</summary>
public partial class ConnectionTreeViewModel
{
    /// <summary>Opens a diagram tab; wired to <see cref="MainViewModel.OpenDiagram"/>.</summary>
    public Action<ConnectionConfig, DiagramRequest>? DiagramOpener { get; set; }

    /// <summary>A connection: its whole database. A schema: that schema. A table: it and its neighbours.</summary>
    [RelayCommand]
    public void ShowDiagram(TreeNodeViewModel? node)
    {
        switch (node)
        {
            case ConnectionNodeViewModel connection:
                DiagramOpener?.Invoke(connection.Config, new DiagramRequest());
                break;
            case SchemaNodeViewModel schema
                when _connectionNodes.Values.FirstOrDefault(c => c.Children.Contains(schema)) is { } owner:
                DiagramOpener?.Invoke(owner.Config, new DiagramRequest(Schema: schema.Title));
                break;
            case TableNodeViewModel { IsView: false } table:
                DiagramOpener?.Invoke(table.Session.Config, new DiagramRequest(Table: table.Table.Ref));
                break;
        }
    }
}
