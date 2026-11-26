using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.ViewModels;

/// <summary>Opening what the navigator and the quick open find besides tables: definitions and columns.</summary>
public partial class MainViewModel
{
    /// <summary>
    /// Shows the source text of a database object in a script tab of its connection; the tab already showing that very text
    /// is activated instead. Nothing is executed.
    /// </summary>
    public ScriptTabViewModel OpenDefinition(Session session, string name, string sql)
    {
        var connection = Tree.Config.Connections.FirstOrDefault(c => c.Id == session.Config.Id) ?? session.Config;
        var shown = Tabs.OfType<ScriptTabViewModel>().FirstOrDefault(t =>
            t.IsUnmodifiedDefinition && t.DocumentName == name && t.Connection.Id == connection.Id && t.Text == sql);
        if (shown is not null)
        {
            ActiveTab = shown;
            return shown;
        }

        var tab = CreateScriptTab(connection, null);
        tab.ShowDocument(name, sql);
        Tabs.Add(tab);
        ActiveTab = tab;
        return tab;
    }

    /// <summary>Opens a new tab for the table and, once its rows are there, scrolls to the column and selects it.</summary>
    public async Task OpenColumn(Session session, TableRef table, string column)
    {
        var tab = TableTabFactory(session, table);
        Tabs.Add(tab);
        ActiveTab = tab;
        if (tab is not TableTabViewModel loading) return;
        await loading.LoadAsync();
        if (!loading.FocusColumn(column)) Status.Error($"{table} has no column {column} any more.");
    }

    private QuickOpenObjects SearchObjects => new(Tree.OpenRoutineDefinitionAsync, OpenColumn, Tree.LoadRoutinesAsync);

    private QuickOpenViewModel TablesPicker(Session session, string query = "") =>
        QuickOpenViewModel.ForTables(session, OpenTable, Status, query, SearchObjects);

    /// <summary>Adds the type chips, remembered in the settings.</summary>
    private QuickOpenViewModel Filtered(QuickOpenViewModel picker) =>
        picker.UseTypeFilter(Tree.Config.QuickOpenHidden, hidden => Tree.SetQuickOpenHidden(hidden));
}
