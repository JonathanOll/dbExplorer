using Avalonia.Controls;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;

namespace UiShots;

internal static partial class Scenes
{
    // The demo connections with an environment each; "Shop (local)", the one the tabs are on, is production.
    private static async Task Environments(Demo d)
    {
        await Open(d);
        foreach (var c in d.Tree.Config.Connections.ToList())
        {
            var environment = c.Name switch
            {
                "Shop (local)" or "Billing" or "ERP" => DbEnvironment.Production,
                "Staging billing" => DbEnvironment.PreProduction,
                "Reports" => DbEnvironment.Test,
                "Scratch" => DbEnvironment.Development,
                _ => DbEnvironment.None,
            };
            d.Tree.UpdateConnection(c with { Environment = environment });
        }
        await Settle();
    }

    private static async Task<Window?> EnvironmentTable(Demo d)
    {
        await Environments(d);
        await OpenTable(d, "customers");
        await OpenTable(d, "orders");
        return null;
    }

    private static async Task<Window?> EnvironmentScript(Demo d)
    {
        await Environments(d);
        d.Tree.SelectedNode = d.AllConnectionNodes().First(n => n.Config.Id == d.Shop.Id);
        var script = d.Main.NewScript()!;
        script.Text = "SELECT id, name, country FROM customers ORDER BY id LIMIT 8;";
        await script.RunScriptAsync();
        await Settle();
        return null;
    }

    private static async Task<Window?> EnvironmentQuickOpen(Demo d)
    {
        await Environments(d);
        await OpenTable(d, "orders");
        d.Main.ShowCommandPalette();
        await Settle();
        var palette = d.Main.CommandPalette!;
        palette.Query = "Edit connection";
        await Settle();
        await palette.ConfirmAsync();
        await Settle();
        return null;
    }

    private static async Task<Window?> EnvironmentConfirm(Demo d)
    {
        await Environments(d);
        var billing = d.Tree.Config.Connections.First(c => c.Name == "Billing");
        var dialog = ProductionDialog.Create(new ProductionWriteRequest(
            [ProductionTarget.Of(billing)], "Run 2 statements that may write",
            ["UPDATE customers SET vip = 1 WHERE credit > 600", "DELETE FROM orders WHERE status = 'open' AND created < '2024-01-01'"], true));
        dialog.Show(d.Window);
        await Settle();
        return dialog;
    }

    private static async Task<Window?> EnvironmentConnectionDialog(Demo d)
    {
        await Environments(d);
        var billing = d.Tree.Config.Connections.First(c => c.Name == "Billing");
        var dialog = new ConnectionDialog { DataContext = new ConnectionDialogViewModel(new PlainProtector(), d.Tree.Config.Folders, [], billing) };
        dialog.Show(d.Window);
        await Settle();
        return dialog;
    }
}
