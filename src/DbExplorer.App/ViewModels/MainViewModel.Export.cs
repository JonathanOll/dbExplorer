using System.Collections.Specialized;

namespace DbExplorer.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>Gives every table and script tab, now and later, the copy and export dialogs and their remembered choices.</summary>
    public void UseExport(IExportDialogs dialogs)
    {
        var services = new ExportServices(Status, dialogs, () => Tree.Config.Export, value => Tree.SetExportPreferences(value));

        void Give(TabViewModel tab)
        {
            if (tab is TableTabViewModel table) table.ExportServices = services;
            else if (tab is ScriptTabViewModel script) script.ExportServices = services;
        }

        foreach (var tab in Tabs) Give(tab);
        Tabs.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Move || e.NewItems is null) return;
            foreach (var tab in e.NewItems.OfType<TabViewModel>()) Give(tab);
        };
    }
}
