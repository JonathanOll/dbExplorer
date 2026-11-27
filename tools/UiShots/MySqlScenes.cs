using Avalonia.Controls;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;

namespace UiShots;

internal static partial class Scenes
{
    // A MySQL connection: four engine cards, the TLS settings shared with PostgreSQL.
    private static async Task<Window?> MySqlConnectionDialog(Demo d)
    {
        await Open(d);
        var vm = new ConnectionDialogViewModel(new PlainProtector(), ["Production", "Staging"], [])
        {
            Name = "Shop (MariaDB)", Kind = DbKind.MySql, Host = "shop-db.internal", Database = "shop", User = "report_ro",
            TlsMode = TlsMode.VerifyCA, TlsRootFile = @"C:\certs\company-ca.pem",
        };
        var dialog = new ConnectionDialog { DataContext = vm };
        dialog.Show(d.Window);
        await Settle();
        return dialog;
    }

    // The tree text of a MySQL EXPLAIN ANALYZE, as the server writes it.
    private static async Task<Window?> PlanMySql(Demo d)
    {
        await Open(d);
        var script = d.Main.NewScript()!;
        script.Text = "SELECT c.name, SUM(o.total) AS total\nFROM orders o JOIN customers c ON c.id = o.customer_id\nWHERE o.status <> 'open'\nGROUP BY c.id, c.name ORDER BY total DESC LIMIT 8;";
        d.Window.Show();
        Pump();
        var plan = DbExplorer.Core.Plans.MySqlPlanParser.Parse(
            """
            -> Limit: 8 row(s)  (cost=9120 rows=8) (actual time=412..412 rows=8 loops=1)
                -> Sort: total DESC, limit input to 8 row(s) per chunk  (cost=9120 rows=1200) (actual time=412..412 rows=8 loops=1)
                    -> Aggregate using temporary table  (cost=8800 rows=1200) (actual time=405..408 rows=950 loops=1)
                        -> Nested loop inner join  (cost=7600 rows=1200) (actual time=0.8..352 rows=84000 loops=1)
                            -> Filter: (o.`status` <> 'open')  (cost=3400 rows=1200) (actual time=0.5..190 rows=84000 loops=1)
                                -> Table scan on o  (cost=3400 rows=96000) (actual time=0.4..150 rows=96000 loops=1)
                            -> Single-row index lookup on c using PRIMARY (id=o.customer_id)  (cost=0.35 rows=1) (actual time=0.0015..0.0016 rows=1 loops=84000)
            """,
            DbExplorer.Core.Plans.PlanMode.Actual, script.Text);
        var tab = new PlanResultTab(plan.Statements.Select(st => (plan, st)).ToList(), DbExplorer.Core.Plans.PlanMode.Actual, DbKind.MySql);
        script.ResultTabs.Add(tab);
        script.SelectedResultTab = tab;
        tab.Selected!.SelectedNode = tab.Selected.Nodes.First(n => n.Node.Name == "Table scan");
        await Settle();
        await Task.Delay(100);
        Pump();
        return null;
    }
}
