using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Config;
using DbExplorer.Core.Metadata;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace UiShots;

public static class Entry
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DbExplorer.App.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).LogToTrace(Avalonia.Logging.LogEventLevel.Warning);
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("usage: UiShots <outDir> [scene,scene...] [light|dark]   |   UiShots --dump <outDir>");
            return 1;
        }

        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        AppDomain.CurrentDomain.FirstChanceException += (_, e) => { if (e.Exception is IndexOutOfRangeException) Console.WriteLine(e.Exception.ToString()); };
        var session = HeadlessUnitTestSession.StartNew(typeof(Entry));
        if (args[0] == "--dump")
        {
            await session.Dispatch(() => { Dump(args[1]); return 0; }, CancellationToken.None);
            return 0;
        }

        if (args[0] == "--icon")
        {
            await session.Dispatch(() => { IconMaker.Write(Path.GetFullPath(args[1])); return 0; }, CancellationToken.None);
            return 0;
        }
        if (args[0] == "--scroll")
        {
            await session.Dispatch(async () => { await ScrollBench.Run(args.Length > 1 ? int.Parse(args[1]) : 3); return 0; }, CancellationToken.None);
            return 0;
        }
        if (args[0] == "--tsql")
        {
            await session.Dispatch(() =>
            {
                var def = AvaloniaEdit.Highlighting.HighlightingManager.Instance.GetDefinition("TSQL");
                foreach (var c in def.NamedHighlightingColors) Console.WriteLine($"{c.Name}: fg={c.Foreground} weight={c.FontWeight} style={c.FontStyle}");
                foreach (var r in def.MainRuleSet.Rules) Console.WriteLine($"rule fg={r.Color?.Foreground} name={r.Color?.Name}");
                foreach (var s in def.MainRuleSet.Spans) Console.WriteLine($"span fg={s.SpanColor?.Foreground} name={s.SpanColor?.Name}");
                return 0;
            }, CancellationToken.None);
            return 0;
        }
        if (args[0] == "--res")
        {
            await session.Dispatch(() =>
            {
                foreach (var key in args.Skip(1))
                {
                    var ok = Application.Current!.TryGetResource(key, ThemeVariant.Light, out var v);
                    Console.WriteLine($"{key}: {ok} {v?.GetType().Name} {v}");
                }
                return 0;
            }, CancellationToken.None);
            return 0;
        }
        var outDir = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outDir);
        var only = args.Length > 1 && args[1] != "all" ? args[1].Split(',').ToHashSet() : null;
        var themes = args.Length > 2 ? [args[2]] : new[] { "light", "dark" };
        await session.Dispatch(async () =>
        {
            foreach (var theme in themes) await Scenes.RunAsync(outDir, theme, only);
            return 0;
        }, CancellationToken.None);
        return 0;
    }

    // Writes the avares:// resources of the theme assemblies (their XAML sources) so the styling can be studied.
    private static void Dump(string dir)
    {
        foreach (var asm in new[] { "Avalonia.Themes.Fluent", "Avalonia.Controls.DataGrid", "AvaloniaEdit" })
        {
            foreach (var uri in Avalonia.Platform.AssetLoader.GetAssets(new Uri($"avares://{asm}/"), null))
            {
                var rel = uri.AbsolutePath.TrimStart('/');
                var target = Path.Combine(dir, asm, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var s = Avalonia.Platform.AssetLoader.Open(uri);
                using var f = File.Create(target);
                s.CopyTo(f);
            }
        }
    }
}

internal sealed class PlainProtector : ISecretProtector
{
    public string Protect(string plain) => "p:" + plain;
    public string Unprotect(string protectedValue) => protectedValue[2..];
}

internal sealed class QuietDialogs : IDialogService
{
    /// <summary>The report the last "Check schema differences" produced (the scenes show it in a real dialog).</summary>
    public SchemaDiffViewModel? LastReport { get; private set; }

    public Task<bool> ShowConnectionDialogAsync(ConnectionDialogViewModel viewModel) => Task.FromResult(false);
    public Task<string?> PromptTextAsync(string title, string label, string initial) => Task.FromResult<string?>(null);
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
    public Task<bool> ShowGroupEditorAsync(GroupEditorViewModel viewModel) => Task.FromResult(false);
    public Task ShowSchemaDiffAsync(SchemaDiffViewModel viewModel)
    {
        LastReport = viewModel;
        return Task.CompletedTask;
    }
    public Task<int?> ChooseAsync(string title, string label, IReadOnlyList<string> options) => Task.FromResult<int?>(null);
}

internal sealed class NoFiles : IScriptFiles
{
    public Task<string?> PickSaveAsync(string suggestedName) => Task.FromResult<string?>(null);
    public Task<string?> PickOpenAsync() => Task.FromResult<string?>(null);
}

internal sealed class QuietEditDialogs : IEditDialogs
{
    public Task<SaveChoice> AskSaveDiscardAsync(string title, string message) => Task.FromResult(SaveChoice.Cancel);
    public Task<bool> ShowSqlPreviewAsync(IReadOnlyList<string> statements, string target) => Task.FromResult(false);
    public Task<string[]?> ShowVirtualKeyDialogAsync(VirtualKeyDialogViewModel viewModel) => Task.FromResult<string[]?>(null);
}

/// <summary>A throw-away data dir with a synthetic config (2 folders, 6 connections) and real SQLite files.</summary>
internal sealed class Demo : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "dbx-uishots-" + Guid.NewGuid().ToString("N"));
    public ConnectionConfig Shop { get; }
    public ConnectionConfig Scratch { get; }
    public MainViewModel Main { get; }
    public ConnectionTreeViewModel Tree { get; }
    public SessionManager Sessions { get; }
    public MainWindow Window { get; }
    public QuietDialogs Dialogs { get; } = new();
    public ConnectionConfig Notes { get; }
    public ConnectionConfig NotesArchive { get; }

    /// <param name="withGroups">Adds the linked-group scenes' data: three SQLite "notes" databases (one file missing) and two engine groups.</param>
    public Demo(bool emptyConfig = false, bool withGroups = false)
    {
        Directory.CreateDirectory(Dir);
        var shopFile = Path.Combine(Dir, "shop.db");
        CreateShop(shopFile);
        var scratchFile = Path.Combine(Dir, "scratch.db");
        CreateScratch(scratchFile);

        ConnectionConfig C(string name, DbKind kind, string? folder, string db, bool ro = false) =>
            new(Det(name), name, kind, folder, kind == DbKind.Sqlite ? null : "db.example.internal", null, db, "app", null, false, true, ro);

        Shop = C("Shop (local)", DbKind.Sqlite, "Production", shopFile);
        Scratch = C("Scratch", DbKind.Sqlite, "Staging", scratchFile);
        Notes = C("Notes", DbKind.Sqlite, null, Path.Combine(Dir, "notes.db"));
        NotesArchive = C("Notes archive", DbKind.Sqlite, null, Path.Combine(Dir, "archive.db"));   // never created: unreachable
        if (withGroups) Exec(Path.Combine(Dir, "notes.db"), """
            CREATE TABLE notes (id INTEGER PRIMARY KEY, title TEXT, body TEXT, pinned INTEGER);
            CREATE TABLE tags (id INTEGER PRIMARY KEY, label TEXT);
            """);
        var billing = C("Billing", DbKind.Postgres, "Production", "billing");
        var stagingBilling = C("Staging billing", DbKind.Postgres, "Staging", "billing_stg");
        var reports = C("Reports", DbKind.SqlServer, "Staging", "reports");
        var config = emptyConfig
            ? AppConfig.Empty()
            : new AppConfig(
                ["Production", "Staging"],
                [
                    Shop,
                    billing,
                    C("ERP", DbKind.SqlServer, "Production", "erp", ro: true),
                    Scratch,
                    stagingBilling,
                    reports,
                    Notes,
                    .. withGroups ? [NotesArchive] : Array.Empty<ConnectionConfig>(),
                ],
                new())
            {
                Groups = withGroups
                    ?
                    [
                        new ConnectionGroup(Det("group:Notes"), "Notes databases", [Scratch.Id, Notes.Id, NotesArchive.Id]),
                        new ConnectionGroup(Det("group:Billing"), "Billing", [billing.Id, stagingBilling.Id]),
                        new ConnectionGroup(Det("group:Draft"), "Reports (draft)", [reports.Id]),
                    ]
                    : [],
            };

        var protector = new PlainProtector();
        var store = new ConfigStore(Path.Combine(Dir, "config.json"), protector);
        var cache = new MetadataCache(Path.Combine(Dir, "cache"));
        Sessions = new SessionManager(store, cache, protector);
        var status = new StatusModel();
        Window = new MainWindow { Width = 1280, Height = 800 };
        var dialogs = Dialogs;
        Tree = new ConnectionTreeViewModel(store, config, Sessions, status, dialogs, Path.Combine(Dir, "dbeaver"));
        Main = new MainViewModel(Tree, status, cache);
        Main.UseTableTabs(Sessions, new QuietEditDialogs());
        Main.UseScriptTabs(new NoFiles(), null, new QuietEditDialogs());
        Window.DataContext = Main;
    }

    private static Guid Det(string name) => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name)));

    public async Task ConnectAsync()
    {
        foreach (var node in AllConnectionNodes().Where(n => n.Config.Kind == DbKind.Sqlite && !n.Config.Name.StartsWith("Notes")))
            await Tree.ConnectAsync(node);
    }

    public IEnumerable<ConnectionNodeViewModel> AllConnectionNodes() =>
        Tree.Nodes.SelectMany(n => n is FolderNodeViewModel f ? f.Children : [n]).OfType<ConnectionNodeViewModel>();

    public static void Exec(string file, string sql)
    {
        using var c = new SqliteConnection($"Data Source={file};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void CreateScratch(string file) => Exec(file, """
        CREATE TABLE notes (id INTEGER PRIMARY KEY, title TEXT, body TEXT);
        INSERT INTO notes VALUES (1, 'todo', 'write the report'), (2, 'ideas', NULL);
        """);

    private static void CreateShop(string file) => Exec(file, """
        CREATE TABLE categories (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
        INSERT INTO categories VALUES (1,'Books'),(2,'Games'),(3,'Garden'),(4,'Kitchen'),(5,'Music'),(6,'Toys');
        CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT, country TEXT, created DATETIME, vip BOOLEAN, credit REAL);
        WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 60)
        INSERT INTO customers SELECT i, 'Customer ' || printf('%02d', i), 'customer' || i || '@example.com',
          CASE i % 5 WHEN 0 THEN 'FR' WHEN 1 THEN 'DE' WHEN 2 THEN 'ES' WHEN 3 THEN 'IT' ELSE 'NL' END,
          date('2024-01-01', '+' || (i * 5) || ' days') || ' 09:30:00', i % 7 = 0, CASE WHEN i % 4 = 0 THEN NULL ELSE i * 12.5 END FROM n;
        CREATE TABLE products (id INTEGER PRIMARY KEY, sku TEXT NOT NULL, name TEXT NOT NULL, price REAL, stock INTEGER, category_id INTEGER REFERENCES categories(id));
        WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 40)
        INSERT INTO products SELECT i, 'SKU-' || printf('%04d', i * 13), 'Product ' || i, round(4.5 + (i * 37 % 900) / 10.0, 2), (i * 11) % 120, i % 6 + 1 FROM n;
        CREATE TABLE orders (
          id INTEGER PRIMARY KEY, customer_id INTEGER REFERENCES customers(id), status TEXT NOT NULL, total REAL,
          currency TEXT, created DATETIME, shipped DATE, paid BOOLEAN, notes TEXT, coupon TEXT, carrier TEXT, tracking_ref TEXT);
        WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 500)
        INSERT INTO orders SELECT i, (i * 7) % 60 + 1,
          CASE i % 4 WHEN 0 THEN 'open' WHEN 1 THEN 'paid' WHEN 2 THEN 'shipped' ELSE 'closed' END,
          round(5 + (i * 37 % 1000) / 10.0, 2), 'EUR',
          date('2025-01-01', '+' || (i % 300) || ' days') || ' ' || printf('%02d:%02d:00', i % 24, i % 60),
          CASE WHEN i % 4 >= 2 THEN date('2025-01-04', '+' || (i % 300) || ' days') ELSE NULL END,
          i % 4 <> 0, CASE WHEN i % 9 = 0 THEN 'gift wrap, call before delivery' ELSE NULL END,
          CASE WHEN i % 13 = 0 THEN 'SPRING' ELSE NULL END, CASE i % 3 WHEN 0 THEN 'DHL' WHEN 1 THEN 'UPS' ELSE 'Colissimo' END,
          CASE WHEN i % 4 >= 2 THEN 'TRK' || printf('%08d', i * 977) ELSE NULL END FROM n;
        CREATE TABLE order_items (id INTEGER PRIMARY KEY, order_id INTEGER NOT NULL REFERENCES orders(id), product_id INTEGER NOT NULL REFERENCES products(id), quantity INTEGER NOT NULL, unit_price REAL);
        WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1200)
        INSERT INTO order_items SELECT i, (i - 1) / 3 + 1, (i * 7) % 40 + 1, i % 5 + 1, round(4.5 + (i * 37 % 900) / 10.0, 2) FROM n;
        CREATE INDEX ix_orders_customer ON orders(customer_id);
        CREATE UNIQUE INDEX ux_products_sku ON products(sku);
        CREATE TABLE wide (id INTEGER PRIMARY KEY, c01 TEXT, c02 TEXT, c03 INTEGER, c04 REAL, c05 TEXT, c06 TEXT, c07 INTEGER, c08 REAL, c09 TEXT, c10 TEXT,
          c11 INTEGER, c12 REAL, c13 TEXT, c14 TEXT, c15 INTEGER, c16 REAL, c17 TEXT, c18 TEXT, c19 DATETIME);
        WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 200)
        INSERT INTO wide SELECT i, 'alpha ' || i, 'beta ' || (i * 3), i * 7, i / 3.0, 'gamma', NULL, i % 17, i * 1.5, 'delta ' || i, 'epsilon', i, i * 0.25,
          'zeta zeta zeta ' || i, 'eta', i * 11, i * 2.5, 'theta ' || i, 'iota', '2025-03-01 10:00:00' FROM n;
        CREATE TABLE audit_log (at DATETIME, who TEXT, action TEXT);
        INSERT INTO audit_log VALUES ('2025-03-01 10:00:00', 'admin', 'login'), ('2025-03-01 10:05:00', 'admin', 'update orders');
        CREATE VIEW v_order_totals AS SELECT customer_id, count(*) AS orders, sum(total) AS total FROM orders GROUP BY customer_id;
        """);

    public void Dispose()
    {
        Window.Close();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

internal static class Scenes
{
    private static readonly (string Name, Func<Demo, Task<Window?>> Run)[] All =
    [
        ("empty", Empty),
        ("main-table", MainTable),
        ("table-edit", TableEdit),
        ("table-structure", TableStructure),
        ("script", s => Script(s, false)),
        ("script-error", s => Script(s, true)),
        ("quick-open", QuickOpen),
        ("connection-dialog", ConnectionDialogScene),
        ("sql-preview", SqlPreview),
        ("virtual-key", VirtualKey),
        ("confirm-dialog", ConfirmDialog),
        ("live-theme-switch", LiveThemeSwitch),
        ("main-table-125", MainTable125),
        ("groups-tree", GroupsTree),
        ("group-editor", GroupEditor),
        ("group-editor-warning", GroupEditorWarning),
        ("group-schema-diff", GroupSchemaDiff),
        ("group-switch-table", GroupSwitchTable),
        ("group-switch-script", GroupSwitchScript),
        ("group-run", GroupRun),
        ("group-run-differences", GroupRunDifferences),
    ];

    private static string Theme = "light";

    public static async Task RunAsync(string outDir, string theme, HashSet<string>? only)
    {
        Theme = theme;
        Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        foreach (var (name, run) in All)
        {
            if (only is not null && !only.Contains(name)) continue;
            using var demo = new Demo(emptyConfig: false, withGroups: name.StartsWith("group"));
            try
            {
                var window = await run(demo);
                var target = window ?? demo.Window;
                await Task.Delay(250);   // late layout passes (text measured before its final value arrived)
                await Settle();
                Save(target, Path.Combine(outDir, $"{name}-{theme}.png"));
                if (!ReferenceEquals(target, demo.Window)) target.Close();
                Console.WriteLine($"{name}-{theme}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"FAILED {name}-{theme}: {e}");
            }
        }
    }

    public static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Save(TopLevel window, string path)
    {
        // Several passes with a pause: the headless renderer otherwise hands back a frame from before the last layout settled
        // (wrapped or missing tabs, clipped texts) and the output differs from one run to the next.
        for (var i = 0; i < 12; i++) { window.UpdateLayout(); Pump(); Thread.Sleep(40); }
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame captured");
        frame.Save(path);
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(40);
            Pump();
        }
    }

    private static async Task Open(Demo d)
    {
        d.Window.Show();
        Pump();
        await d.ConnectAsync();
        // A failed (unreachable) engine, as the tree shows it after a refused connection.
        var failed = d.AllConnectionNodes().First(n => n.Config.Name == "Staging billing");
        failed.State = ConnectionState.Failed;
        failed.ErrorMessage = "could not connect to db.example.internal:5432";
        foreach (var folder in d.Tree.Nodes.OfType<FolderNodeViewModel>()) folder.IsExpanded = true;
        d.Tree.SelectedNode = d.AllConnectionNodes().First(n => n.Config == d.Shop);
        await Settle();
    }

    private static async Task<TableTabViewModel> OpenTable(Demo d, string table)
    {
        var session = d.Sessions.Find(d.Shop.Id)!;
        await d.Main.OpenTable(session, new TableRef(null, table));
        var tab = (TableTabViewModel)d.Main.ActiveTab!;
        for (var i = 0; i < 100 && (tab.IsLoading || tab.Rows.Count == 0); i++) await Task.Delay(20);
        await Settle();
        return tab;
    }

    private static async Task<Window?> Empty(Demo d)
    {
        d.Window.Show();
        await Settle();
        return null;
    }

    private static async Task<Window?> MainTable(Demo d)
    {
        await Open(d);
        await OpenTable(d, "customers");
        var orders = await OpenTable(d, "orders");
        orders.ColumnFilters["status"] = "paid";
        orders.RawWhere = "total > 20";
        await orders.ApplyFiltersAsync();
        await orders.ToggleSortAsync("created");
        await Settle();
        return null;
    }

    private static async Task<Window?> TableEdit(Demo d)
    {
        await Open(d);
        var tab = await OpenTable(d, "orders");
        tab.SetCell(1, 2, "refunded");
        tab.SetCell(2, 3, "129.90");
        tab.SetCell(4, 8, null);
        tab.DeleteRow(6);
        tab.AddRow();
        tab.ErrorMessage = "no such column: foo";
        tab.CellError = "'abc' is not a valid number for total";
        await Settle();
        return null;
    }

    private static async Task<Window?> TableStructure(Demo d)
    {
        await Open(d);
        await OpenTable(d, "orders");
        d.Window.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "SubTabs").SelectedIndex = 1;
        await Settle();
        return null;
    }

    private static async Task<Window?> Script(Demo d, bool error)
    {
        await Open(d);
        await OpenTable(d, "customers");
        var script = d.Main.NewScript()!;
        script.Text = """
            -- Best customers this year
            SELECT c.name, c.country, count(*) AS orders, round(sum(o.total), 2) AS total
            FROM orders o
            JOIN customers c ON c.id = o.customer_id
            WHERE o.status <> 'open'
            GROUP BY c.id
            ORDER BY total DESC
            LIMIT 8;

            UPDATE customers SET vip = 1 WHERE credit > 600;

            """ + (error ? "SELECT * FROM missing_table;" : "SELECT id, name, country, vip, credit, created FROM customers ORDER BY credit DESC;");
        d.Window.Show();
        Pump();
        await script.RunScriptAsync();
        await Settle();
        await Task.Delay(100);
        Pump();
        return null;
    }

    private static async Task<Window?> QuickOpen(Demo d)
    {
        await Open(d);
        await OpenTable(d, "customers");
        d.Main.ShowTablePicker();
        d.Main.QuickOpen!.Query = "ord";
        await Settle();
        return null;
    }

    private static async Task<Window?> ConnectionDialogScene(Demo d)
    {
        await Open(d);
        var vm = new ConnectionDialogViewModel(new PlainProtector(), ["Production", "Staging"], ["Shop (local)"], null)
        {
            Name = "Billing replica", Host = "billing-replica.example.internal", Database = "billing", User = "report_ro",
        };
        var dialog = new ConnectionDialog { DataContext = vm };
        dialog.Show(d.Window);
        await Settle();
        vm.TestMessage = "Connected successfully (PostgreSQL 16.2)";
        vm.TestSucceeded = true;
        await Settle();
        return dialog;
    }

    private static async Task<Window?> SqlPreview(Demo d)
    {
        await Open(d);
        await OpenTable(d, "orders");
        var dialog = new SqlPreviewDialog(
        [
            "UPDATE \"orders\" SET \"status\" = 'refunded' WHERE \"id\" = 2;",
            "UPDATE \"orders\" SET \"total\" = 129.90 WHERE \"id\" = 3;",
            "DELETE FROM \"orders\" WHERE \"id\" = 7;",
            "INSERT INTO \"orders\" (\"status\") VALUES ('open');",
        ], "Shop (local) · /data/shop.db");
        dialog.Show(d.Window);
        await Settle();
        return dialog;
    }

    // Opens in the other theme, then switches while the window is showing: everything must follow without being rebuilt.
    private static async Task<Window?> LiveThemeSwitch(Demo d)
    {
        var target = Theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        Application.Current!.RequestedThemeVariant = Theme == "dark" ? ThemeVariant.Light : ThemeVariant.Dark;
        await Open(d);
        await OpenTable(d, "orders");
        d.Main.ShowTablePicker();
        await Settle();
        Application.Current.RequestedThemeVariant = target;
        await Settle();
        return null;
    }

    // 1280x800 at 125 percent scaling leaves 1024x640 device-independent pixels: the layout must still hold.
    private static async Task<Window?> MainTable125(Demo d)
    {
        d.Window.Width = 1024;
        d.Window.Height = 640;
        return await MainTable(d);
    }

    // The navigator with the Groups section: three groups (one with an unreachable member, one under-populated), expanded.
    private static async Task<Window?> GroupsTree(Demo d)
    {
        await Open(d);
        foreach (var group in d.Tree.Nodes.OfType<GroupsSectionNodeViewModel>().SelectMany(s => s.Children)) group.IsExpanded = true;
        d.Tree.SelectedNode = d.Tree.Nodes.OfType<GroupsSectionNodeViewModel>().First().Children[0].Children[0];
        await Settle();
        return null;
    }

    private static async Task<Window?> GroupEditor(Demo d)
    {
        await Open(d);
        var vm = new GroupEditorViewModel(d.Tree.Config, null) { Name = "Notes databases" };
        vm.Add(d.Scratch);
        vm.Add(d.Notes);
        var dialog = new GroupEditorDialog { DataContext = vm };
        dialog.Show(d.Window);
        await Settle();
        return dialog;
    }

    // A group of one database, saved with a clash on the name: the warning and the error band.
    private static async Task<Window?> GroupEditorWarning(Demo d)
    {
        await Open(d);
        var vm = new GroupEditorViewModel(d.Tree.Config, null) { Name = "billing" };
        vm.Add(d.Scratch);
        vm.TryConfirm();
        var dialog = new GroupEditorDialog { DataContext = vm };
        dialog.Show(d.Window);
        await Settle();
        return dialog;
    }

    private static async Task<Window?> GroupSchemaDiff(Demo d)
    {
        await Open(d);
        await d.Tree.CheckSchemaDifferencesAsync(d.Tree.Config.Groups[0]);
        var dialog = new SchemaDiffDialog { DataContext = d.Dialogs.LastReport ?? throw new InvalidOperationException("no report") };
        dialog.Show(d.Window);
        await Settle();
        return dialog;
    }

    // A table tab on "Scratch", a member of the "Notes databases" group: the database selector strip above the tab.
    private static async Task<Window?> GroupSwitchTable(Demo d)
    {
        await Open(d);
        await d.Main.OpenTable(d.Sessions.Find(d.Scratch.Id)!, new TableRef(null, "notes"));
        var tab = (TableTabViewModel)d.Main.ActiveTab!;
        for (var i = 0; i < 100 && (tab.IsLoading || tab.Rows.Count == 0); i++) await Task.Delay(20);
        await Settle();
        return null;
    }

    // A script tab on the same group: the selector replaces the connection badge in the toolbar.
    private static async Task<Window?> GroupSwitchScript(Demo d)
    {
        await Open(d);
        d.Tree.SelectedNode = d.AllConnectionNodes().First(n => n.Config == d.Scratch);
        var script = d.Main.NewScript()!;
        script.Text = "SELECT id, title, body FROM notes ORDER BY id;";
        d.Window.Show();
        Pump();
        await script.RunScriptAsync();
        await Settle();
        return null;
    }

    // Run on group: the same SELECT on three databases (Scratch, Notes and a freshly made archive); Notes has one more column.
    private static Task<Window?> GroupRun(Demo d) => GroupRunCore(d, differences: false);

    private static async Task<Window?> GroupRunCore(Demo d, bool differences)
    {
        Demo.Exec(Path.Combine(d.Dir, "archive.db"), """
            CREATE TABLE notes (id INTEGER PRIMARY KEY, title TEXT, body TEXT);
            INSERT INTO notes VALUES (1, 'todo', 'write the report'), (2, 'ideas', 'ship it'), (3, 'old', 'archived');
            """);
        Demo.Exec(Path.Combine(d.Dir, "notes.db"), """
            INSERT INTO notes VALUES (1, 'todo', 'write the report', 0), (2, 'ideas', NULL, 1), (4, 'new', 'fresh', 0);
            """);
        await Open(d);
        d.Tree.SelectedNode = d.AllConnectionNodes().First(n => n.Config == d.Scratch);
        var script = d.Main.NewScript()!;
        script.Text = "SELECT id, title, body FROM notes ORDER BY id;";
        d.Window.Show();
        Pump();
        await script.RunStatementOnGroupAsync();
        if (differences) script.GroupRun!.ShowDifferences = true;
        await Settle();
        return null;
    }

    private static Task<Window?> GroupRunDifferences(Demo d) => GroupRunCore(d, differences: true);

    private static async Task<Window?> ConfirmDialog(Demo d)
    {
        await Open(d);
        // The destructive look of ConfirmAsync(ConfirmRequest): red verb button, red icon, Cancel is the default.
        var window = DbExplorer.App.DialogShell.Create("Delete connection", "IconWarning",
            DbExplorer.App.DialogShell.Message("Delete connection 'Billing'?", "Its saved password is removed. Open tabs on it are closed."),
            [DbExplorer.App.DialogShell.Button("Delete", danger: true), DbExplorer.App.DialogShell.Button("Cancel", isDefault: true, isCancel: true)],
            tone: DbExplorer.App.ViewModels.DialogTone.Danger);
        window.Show(d.Window);
        await Settle();
        return window;
    }

    private static async Task<Window?> VirtualKey(Demo d)
    {
        await Open(d);
        var session = d.Sessions.Find(d.Shop.Id)!;
        var info = session.Schema.First(t => t.Ref.Name == "audit_log");
        var dialog = new VirtualKeyDialog(new VirtualKeyDialogViewModel(info, DbKind.Sqlite, null));
        dialog.Show(d.Window);
        await Settle();
        return dialog;
    }
}
