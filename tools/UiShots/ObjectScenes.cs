using System.Data.Common;
using Avalonia.Controls;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;

namespace UiShots;

/// <summary>A PostgreSQL-like engine with canned views and routines (no server needed).</summary>
internal sealed class SampleRoutineDriver : IDbDriver
{
    private readonly IDbDriver _postgres = new PostgresDriver();

    public static readonly IReadOnlyList<TableInfo> Tables =
    [
        Table("invoices", false, "id", "order_id", "total", "issued"),
        Table("orders", false, "id", "customer_id", "total", "status"),
        Table("payments", false, "id", "invoice_id", "amount"),
        Table("open_orders", true, "id", "customer_id", "total"),
        Table("order_totals_by_month", true, "month", "total"),
    ];

    private static TableInfo Table(string name, bool view, params string[] columns) => new(
        new TableRef("public", name), view,
        columns.Select((c, i) => new ColumnInfo(c, "text", typeof(string), true, i == 0, i)).ToList(), [], []);

    public DbKind Kind => DbKind.Postgres;
    public bool HasRoutines => true;
    public string ParamPrefix => _postgres.ParamPrefix;
    public string Quote(string identifier) => _postgres.Quote(identifier);
    public string QuoteTable(TableRef t) => _postgres.QuoteTable(t);
    public string CastToText(string quotedColumn) => _postgres.CastToText(quotedColumn);
    public string EscapeLike(string text) => _postgres.EscapeLike(text);
    public string PageSql(string selectSql, string orderBy, int offset, int limit) => _postgres.PageSql(selectSql, orderBy, offset, limit);

    public DbConnection CreateConnection(ConnectionConfig cfg, string? plainPassword) =>
        new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");

    public Task<IReadOnlyList<TableInfo>> LoadSchemaAsync(DbConnection c, CancellationToken ct) => Task.FromResult(Tables);

    public Task<IReadOnlyList<RoutineInfo>> LoadRoutinesAsync(DbConnection c, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RoutineInfo>>(
        [
            new(new TableRef("public", "close_order"), RoutineKind.Procedure, "order_id integer", 1),
            new(new TableRef("public", "purge_old_orders"), RoutineKind.Procedure, "", 2),
            new(new TableRef("public", "order_total"), RoutineKind.Function, "order_id integer", 3),
            new(new TableRef("public", "order_total"), RoutineKind.Function, "order_id integer, currency text", 4),
            new(new TableRef("public", "vat_amount"), RoutineKind.Function, "amount numeric", 5),
        ]);

    public Task<string> LoadRoutineDefinitionAsync(DbConnection c, RoutineInfo routine, CancellationToken ct) =>
        Task.FromResult("""
            CREATE OR REPLACE FUNCTION public.order_total(order_id integer)
             RETURNS numeric
             LANGUAGE sql
             STABLE
            AS $function$
              -- invoiced amount of one order
              SELECT coalesce(sum(total), 0) FROM invoices i WHERE i.order_id = order_total.order_id
            $function$
            """);
}

internal static partial class Scenes
{
    private static Session SampleSession(Demo d)
    {
        var config = d.AllConnectionNodes().First(n => n.Config.Name == "Billing").Config;
        return new Session(config, new SampleRoutineDriver(), SampleRoutineDriver.Tables);
    }

    private static async Task<Window?> ObjectsTree(Demo d)
    {
        await Open(d);
        var node = d.AllConnectionNodes().First(n => n.Config.Name == "Billing");
        ConnectionTreeViewModel.ApplySchema(node, SampleSession(d));
        node.State = ConnectionState.Connected;
        d.AllConnectionNodes().First(n => n.Config == d.Shop).IsExpanded = false;
        var schema = node.Children[0];
        schema.IsExpanded = true;
        foreach (var group in schema.Children.OfType<ObjectGroupNodeViewModel>().ToList()) group.IsExpanded = true;
        foreach (var group in schema.Children.OfType<RoutineGroupNodeViewModel>()) await group.LoadAsync();
        d.Tree.SelectedNode = schema.Children.OfType<RoutineGroupNodeViewModel>().Last().Children[0];
        await Settle();
        return null;
    }

    private static async Task<Window?> ObjectDefinition(Demo d)
    {
        await ObjectsTree(d);
        await d.Tree.OpenDefinitionAsync(d.Tree.SelectedNode);
        await Settle();
        return null;
    }

    private static async Task<Window?> QuickOpenObjectsScene(Demo d, string query)
    {
        await Open(d);
        await OpenTable(d, "customers");
        var session = SampleSession(d);
        await session.LoadRoutinesAsync(CancellationToken.None);
        var objects = new QuickOpenObjects((_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask);
        var picker = QuickOpenViewModel.ForTables(session, (_, _) => Task.CompletedTask, objects: objects)
            .UseTypeFilter(query == "order" ? QuickOpenTypes.None : QuickOpenTypes.Procedures);
        picker.ScopeHint = "Search all connections";
        d.Main.QuickOpen = picker;
        picker.Query = query;
        await Settle();
        return null;
    }
}
