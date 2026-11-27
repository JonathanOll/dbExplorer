using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Config;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using static DbExplorer.App.Tests.TreeFixture;

namespace DbExplorer.App.Tests;

public class QuickOpenObjectsTests : IDisposable
{
    private readonly TreeFixture _fx = new();
    private readonly List<string> _log = [];

    public void Dispose() => _fx.Dispose();

    private QuickOpenObjects Objects(Func<Session, Task<IReadOnlyList<RoutineInfo>>>? load = null) => new(
        (_, r) =>
        {
            _log.Add($"routine {r.Ref}({r.Arguments})");
            return Task.CompletedTask;
        },
        (_, t, c) =>
        {
            _log.Add($"column {t} {c}");
            return Task.CompletedTask;
        },
        load);

    private Task OpenTable(Session s, TableRef t)
    {
        _log.Add($"table {t}");
        return Task.CompletedTask;
    }

    private static TableInfo Info(string name, bool view, params string[] columns) => new(
        new TableRef("public", name), view,
        columns.Select((c, i) => new ColumnInfo(c, "text", typeof(string), true, false, i)).ToList(), [], []);

    private static RoutineDriver Driver()
    {
        var driver = new RoutineDriver();
        driver.Tables.AddRange([
            Info("orders", false, "id", "total", "orders_ref"),
            Info("customers", false, "id", "email"),
            Info("v_orders", true, "id"),
        ]);
        driver.Routines.AddRange([
            new(new TableRef("public", "close_orders"), RoutineKind.Procedure, "", 1),
            new(new TableRef("public", "order_total"), RoutineKind.Function, "integer", 2),
        ]);
        return driver;
    }

    private static Session NewSession(RoutineDriver driver) =>
        new(Conn("PG", database: "db", kind: DbKind.Postgres), driver, driver.Tables, null);

    private async Task<QuickOpenViewModel> Picker(RoutineDriver driver)
    {
        var vm = QuickOpenViewModel.ForTables(NewSession(driver), OpenTable, _fx.Status,
            objects: Objects(s => s.LoadRoutinesAsync(CancellationToken.None))).UseTypeFilter(QuickOpenTypes.None);
        await vm.Loading;
        return vm;
    }

    [Fact]
    public async Task EmptyQuery_ListsObjects_NotColumns()
    {
        var vm = await Picker(Driver());

        Assert.Equal(5, vm.Results.Count);
        Assert.DoesNotContain(vm.Results, r => r.Type == QuickOpenTypes.Columns);
    }

    [Fact]
    public async Task Query_FindsEveryKind_TablesAheadOfColumnsOfTheSameQuality()
    {
        var vm = await Picker(Driver());

        vm.Query = "order";

        Assert.Equal("public.orders", vm.Results[0].Label);
        Assert.Contains(vm.Results, r => r.Type == QuickOpenTypes.Views);
        Assert.Contains(vm.Results, r => r.Type == QuickOpenTypes.Procedures);
        Assert.Contains(vm.Results, r => r.Type == QuickOpenTypes.Functions);
        var column = Assert.Single(vm.Results, r => r.Type == QuickOpenTypes.Columns);
        Assert.Equal(("orders_ref", "public.orders", "IconColumn"), (column.DisplayLabel, column.Detail, column.Icon));
    }

    [Fact]
    public async Task ExactColumn_RisesAboveScatteredMatches_AndQualifiedQueryFindsIt()
    {
        var vm = await Picker(Driver());

        vm.Query = "email";
        Assert.Equal("email", vm.Results[0].DisplayLabel);

        vm.Query = "orders.total";
        Assert.Equal(("total", "public.orders"), (vm.Results[0].DisplayLabel, vm.Results[0].Detail));
    }

    [Fact]
    public async Task Confirm_OpensEachKindItsWay()
    {
        var driver = Driver();

        foreach (var query in new[] { "customers", "close_orders", "email" })
        {
            var vm = await Picker(driver);
            vm.Query = query;
            await vm.ConfirmAsync();
        }

        Assert.Equal(["table public.customers", "routine public.close_orders()", "column public.customers email"], _log);
    }

    [Fact]
    public async Task Toggles_LeaveKindsOut_AndReportTheChoice()
    {
        QuickOpenTypes? stored = null;
        var vm = await Picker(Driver());
        vm.UseTypeFilter(QuickOpenTypes.Views, hidden => stored = hidden);
        vm.Query = "order";
        Assert.DoesNotContain(vm.Results, r => r.Type == QuickOpenTypes.Views);
        Assert.False(vm.TypeToggles[1].IsOn);

        vm.ToggleType(0);   // Alt+1: tables
        Assert.Equal(QuickOpenTypes.Views | QuickOpenTypes.Tables, stored);
        Assert.DoesNotContain(vm.Results, r => r.Type == QuickOpenTypes.Tables);

        vm.ToggleType(0);
        vm.ToggleType(1);
        Assert.Equal(QuickOpenTypes.None, stored);
        Assert.Contains(vm.Results, r => r.Type == QuickOpenTypes.Views);
    }

    [Fact]
    public async Task OnlyColumns_ListsThemWithoutAQuery_AndNothingOnSaysSo()
    {
        var vm = await Picker(Driver());
        vm.UseTypeFilter(QuickOpenTypes.Tables | QuickOpenTypes.Views | QuickOpenTypes.Procedures | QuickOpenTypes.Functions);

        Assert.Equal(6, vm.Results.Count);

        vm.ToggleType(4);
        Assert.Empty(vm.Results);
        Assert.Contains("switched off", vm.EmptyText);
    }

    [Fact]
    public void Choice_IsPersisted_AndOldFilesSearchEverything()
    {
        var tree = _fx.CreateTree(AppConfig.Empty());
        Assert.True(tree.SetQuickOpenHidden(QuickOpenTypes.Columns | QuickOpenTypes.Views));
        Assert.Equal(QuickOpenTypes.Columns | QuickOpenTypes.Views, _fx.Store.Load().QuickOpenHidden);

        File.WriteAllText(_fx.ConfigPath, """{ "Folders": [], "Connections": [], "VirtualKeys": {} }""");
        Assert.Equal(QuickOpenTypes.None, _fx.Store.Load().QuickOpenHidden);
    }

    [Fact]
    public async Task AllConnections_ReadsTheCacheOnly_ThenOpensRoutineAndColumnLive()
    {
        var driver = Driver();
        var config = Conn("Alpha", database: "db", kind: DbKind.Postgres);
        var sessions = new SessionManager(_fx.Store, _fx.Cache, _fx.Protector, _ => driver);
        await sessions.LoadRoutinesAsync(await sessions.GetOrOpenAsync(config, CancellationToken.None), CancellationToken.None);
        sessions.Close(config.Id);
        var connects = 0;
        async Task<Session?> Connect(ConnectionConfig c)
        {
            connects++;
            return await sessions.GetOrOpenAsync(c, CancellationToken.None);
        }

        QuickOpenViewModel New() =>
            QuickOpenViewModel.ForAllConnections(_fx.Cache, [config], Connect, OpenTable, _fx.Status, Objects()).UseTypeFilter(QuickOpenTypes.None);

        var routine = New();
        routine.Query = "order_total";
        Assert.Equal("Alpha / db", routine.Results[0].Context);
        var column = New();
        column.Query = "email";
        Assert.Equal("email", column.Results[0].DisplayLabel);
        Assert.Equal(0, connects);

        driver.Routines[1] = driver.Routines[1] with { Id = 99 };   // recreated since it was cached
        await routine.ConfirmAsync();
        await column.ConfirmAsync();

        Assert.Equal(["routine public.order_total(integer)", "column public.customers email"], _log);

        var stale = New();
        stale.Query = "order_total";
        driver.Routines.RemoveAt(1);
        sessions.Close(config.Id);
        await stale.ConfirmAsync();

        Assert.Equal(2, _log.Count);
        Assert.True(_fx.Status.IsError);
        Assert.Contains("no longer exists", _fx.Status.Message);
    }

    [Fact]
    public async Task Main_ColumnResult_OpensTheTableOnThatColumn()
    {
        var config = Conn("Lite", database: _fx.CreateSqliteFile());
        var tree = _fx.CreateTree(new AppConfig([], [config], new()));
        var main = new MainViewModel(tree, _fx.Status, _fx.Cache);
        var session = await _fx.Sessions.GetOrOpenAsync(config, CancellationToken.None);
        int? focused = null;
        main.UseTableTabs(_fx.Sessions);
        var factory = main.TableTabFactory;
        main.TableTabFactory = (s, t) =>
        {
            var tab = factory(s, t);
            if (tab is TableTabViewModel table) table.FocusColumnRequested += i => focused = i;
            return tab;
        };

        await main.OpenColumn(session, new TableRef(null, "users"), "name");

        Assert.IsType<TableTabViewModel>(Assert.Single(main.Tabs));
        Assert.Equal(1, focused);
    }

    [AvaloniaFact]
    public async Task Overlay_ChipsToggleWithAltDigit_AndAreReachableByKeyboard()
    {
        var vm = await Picker(Driver());
        var overlay = new QuickOpenOverlay { DataContext = vm };
        var window = new Window { Content = overlay, Width = 900, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var chips = overlay.GetVisualDescendants().OfType<ToggleButton>().ToList();
        Assert.Equal(5, chips.Count);
        Assert.All(chips, c => Assert.NotNull(ToolTip.GetTip(c)));

        window.KeyPressQwerty(PhysicalKey.Digit1, RawInputModifiers.Alt);
        Assert.False(vm.TypeToggles[0].IsOn);

        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
        Assert.True(chips[0].IsFocused);
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.True(chips[1].IsFocused);
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Assert.False(vm.TypeToggles[1].IsOn);
        window.Close();
    }
}
