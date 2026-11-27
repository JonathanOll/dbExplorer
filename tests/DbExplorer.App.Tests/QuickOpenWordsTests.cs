using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using Microsoft.Data.Sqlite;
using static DbExplorer.App.Tests.TreeFixture;

namespace DbExplorer.App.Tests;

/// <summary>Queries of several words ("accounts resto2"), and the extension of Ctrl+T to the other connections.</summary>
public class QuickOpenWordsTests : IDisposable
{
    private readonly TreeFixture _fx = new();
    private readonly List<string> _opened = [];

    public void Dispose() => _fx.Dispose();

    private string CreateDb(string name, string table)
    {
        var file = Path.Combine(_fx.Dir, name);
        using var c = new SqliteConnection($"Data Source={file};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"CREATE TABLE {table} (id INTEGER PRIMARY KEY)";
        cmd.ExecuteNonQuery();
        return file;
    }

    private static TableInfo Info(string schema, string name, params string[] columns) => new(
        new TableRef(schema, name), false,
        columns.Select((c, i) => new ColumnInfo(c, "text", typeof(string), true, false, i)).ToList(), [], []);

    private static ConnectionConfig Server(string name, string database, string? folder = null,
        DbEnvironment environment = DbEnvironment.None) =>
        Conn(name, database: database, kind: DbKind.Postgres) with { Folder = folder, Environment = environment };

    private ConnectionConfig Cached(ConnectionConfig config, params TableInfo[] tables)
    {
        _fx.Cache.Save(config.Id, tables);
        return config;
    }

    private Task Open(Session s, TableRef t)
    {
        _opened.Add($"{s.Config.Name} {t}");
        return Task.CompletedTask;
    }

    private static QuickOpenObjects Objects() => new((_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask);

    private readonly List<string> _connects = [];

    private Task<Session?> NoConnect(ConnectionConfig c)
    {
        _connects.Add(c.Name);
        return Task.FromResult<Session?>(null);
    }

    private static readonly TableInfo[] Restaurant =
    [
        Info("dbo", "accounts", "id", "email"),
        Info("dbo", "account_roles", "id", "role"),
        Info("dbo", "orders", "id", "total"),
    ];

    private QuickOpenViewModel All(IReadOnlyList<ConnectionGroup>? groups = null, params ConnectionConfig[] configs) =>
        QuickOpenViewModel.ForAllConnections(_fx.Cache, configs, NoConnect, Open, _fx.Status, Objects(), groups)
            .UseTypeFilter(QuickOpenTypes.None);

    private (QuickOpenViewModel Vm, Session Session) Here(ConnectionConfig active, TableInfo[] tables, params ConnectionConfig[] others)
    {
        var session = new Session(active, new RoutineDriver(), tables, null);
        var elsewhere = new QuickOpenElsewhere(_fx.Cache, [active, .. others], [], NoConnect);
        var vm = QuickOpenViewModel.ForTables(session, Open, _fx.Status, objects: Objects(), elsewhere: elsewhere)
            .UseTypeFilter(QuickOpenTypes.None);
        return (vm, session);
    }

    private static async Task Type(QuickOpenViewModel vm, string query)
    {
        vm.Query = query;
        await vm.Extending;
    }

    private static string First(QuickOpenViewModel vm) => vm.Results[0].Label;

    private (ConnectionConfig One, ConnectionConfig Two) TwoRestaurants() =>
        (Cached(Server("Resto one", "resto1"), Restaurant), Cached(Server("Resto two", "resto2"), Restaurant));

    // ---- all connections ----

    [Theory]
    [InlineData("accounts resto2")]
    [InlineData("resto2 accounts")]
    [InlineData("  resto2    ACCOUNTS ")]
    [InlineData("dbo accounts resto2")]
    [InlineData("dbo resto2 accounts")]
    [InlineData("accounts dbo resto2")]
    [InlineData("accounts resto2 dbo")]
    [InlineData("resto2 dbo accounts")]
    [InlineData("resto2 accounts dbo")]
    [InlineData("acc resto2")]
    [InlineData("acounts resto2")]
    [InlineData("dbo.accounts resto2")]
    [InlineData("resto2 dbo.acc")]
    [InlineData("two accounts")]
    [InlineData("resot2 accounts")]
    public void AllConnections_EveryOrder_FindsTheTableOfTheNamedDatabase(string query)
    {
        var (one, two) = TwoRestaurants();
        var vm = All(null, one, two);

        vm.Query = query;

        Assert.Equal("Resto two · dbo.accounts", First(vm));
        Assert.Equal("Resto two / resto2", vm.Results[0].Context);
    }

    [Fact]
    public void EveryWordMustMatchSomething()
    {
        var (one, two) = TwoRestaurants();
        var vm = All(null, one, two);

        vm.Query = "accounts resto2 zzzz";

        Assert.Empty(vm.Results);
    }

    [Fact]
    public void AmbiguousWord_ListsEveryDatabaseThatFits_ByName()
    {
        var (one, two) = TwoRestaurants();
        var vm = All(null, two, one);

        vm.Query = "accounts resto";

        Assert.Equal(["Resto one · dbo.accounts", "Resto two · dbo.accounts"], vm.Results.Take(2).Select(r => r.Label));
    }

    [Fact]
    public void DatabaseNameAlone_ListsItsTables_NotItsColumns()
    {
        var (one, two) = TwoRestaurants();
        var vm = All(null, one, two);

        vm.Query = "resto2";

        Assert.Equal(["Resto two · dbo.account_roles", "Resto two · dbo.accounts", "Resto two · dbo.orders"],
            vm.Results.Select(r => r.Label).Order());
    }

    [Fact]
    public void ObjectCalledLikeTheWord_ComesBeforeTheObjectsOfTheDatabaseCalledSo()
    {
        var sales = Cached(Server("A", "sales"), Info("dbo", "zeta"));
        var other = Cached(Server("B", "crm"), Info("dbo", "sales"));
        var vm = All(null, sales, other);

        vm.Query = "sales";

        Assert.Equal(["B · dbo.sales", "A · dbo.zeta"], vm.Results.Select(r => r.Label));
    }

    [Fact]
    public void Column_IsFoundByItsNameTableAndDatabase_InAnyOrder()
    {
        var (one, two) = TwoRestaurants();
        var vm = All(null, one, two);

        foreach (var query in new[] { "email accounts resto2", "resto2 email accounts", "accounts.email resto2", "resto2 email" })
        {
            vm.Query = query;
            Assert.Equal(("email", "dbo.accounts", "Resto two / resto2"),
                (vm.Results[0].DisplayLabel, vm.Results[0].Detail, vm.Results[0].Context));
        }
    }

    [Fact]
    public void Routine_IsFoundWithTheDatabase()
    {
        var (one, two) = TwoRestaurants();
        _fx.Cache.SaveRoutines(two.Id, [new(new TableRef("dbo", "close_day"), RoutineKind.Procedure, "", 1)]);
        var vm = All(null, one, two);

        vm.Query = "resto2 close";

        Assert.Equal("Resto two · dbo.close_day", First(vm));
    }

    [Theory]
    [InlineData("accounts bistro")]       // folder
    [InlineData("accounts franchise")]    // group of databases
    [InlineData("accounts prod")]         // environment, short
    [InlineData("production accounts")]   // environment
    public void FolderGroupAndEnvironment_NameAConnection(string query)
    {
        var one = Cached(Server("One", "db1"), Restaurant);
        var two = Cached(Server("Two", "db2", "Bistros", DbEnvironment.Production), Restaurant);
        var vm = All([new ConnectionGroup(Guid.NewGuid(), "Franchise", [two.Id])], one, two);

        vm.Query = query;

        Assert.Equal("Two · dbo.accounts", First(vm));
        Assert.DoesNotContain(vm.Results, r => r.Label.StartsWith("One ", StringComparison.Ordinal));
    }

    [Fact]
    public void GroupMembers_AreToldApartByTheirDatabase()
    {
        var one = Cached(Server("Shop A", "resto1"), Restaurant);
        var two = Cached(Server("Shop B", "resto2"), Restaurant);
        var group = new ConnectionGroup(Guid.NewGuid(), "Restaurants", [one.Id, two.Id]);
        var vm = All([group], one, two);

        vm.Query = "restaurants accounts";
        Assert.Equal(["Shop A · dbo.accounts", "Shop B · dbo.accounts"], vm.Results.Take(2).Select(r => r.Label));

        vm.Query = "restaurants resto2 accounts";
        Assert.Equal("Shop B · dbo.accounts", First(vm));
    }

    [Fact]
    public void Sqlite_DatabaseIsTheFileName_NotItsFolders()
    {
        var lite = Cached(Conn("Local", database: Path.Combine("somewhere", "resto7.db")), Info("main", "accounts"));
        var vm = All(null, lite);

        vm.Query = "accounts resto7";
        Assert.Equal("Local / resto7", Assert.Single(vm.Results).Context);

        vm.Query = "accounts somewhere";
        Assert.Empty(vm.Results);
    }

    // ---- Ctrl+T: scope extension ----

    [Theory]
    [InlineData("accounts resto2")]
    [InlineData("resto2 accounts")]
    [InlineData("dbo resto2 acc")]
    [InlineData("resot2 accounts")]
    public async Task OtherDatabaseNamed_ExtendsTheSearch_WithoutConnecting(string query)
    {
        var (one, two) = TwoRestaurants();
        var (vm, _) = Here(one, Restaurant, two);

        await Type(vm, query);

        Assert.Equal("Resto two · dbo.accounts", First(vm));
        Assert.Equal("Resto two / resto2", vm.Results[0].Context);
        Assert.Contains("also searching other connections", vm.ExtensionText);
        Assert.Empty(_connects);
        Assert.Empty(_fx.Sessions.Open);

        await vm.ConfirmAsync();
        Assert.Equal(["Resto two"], _connects);
    }

    [Fact]
    public async Task ChosenResultOfAnotherConnection_OpensThere()
    {
        var file = CreateDb("resto2.db", "accounts");
        var one = Server("Resto one", "resto1");
        var two = Conn("Two", database: file);
        await _fx.Sessions.GetOrOpenAsync(two, CancellationToken.None);
        _fx.Sessions.Close(two.Id);
        var session = new Session(one, new RoutineDriver(), Restaurant, null);
        var tree = _fx.CreateTree(new AppConfig([], [one, two], new()));
        var vm = QuickOpenViewModel.ForTables(session, Open, _fx.Status,
            elsewhere: new QuickOpenElsewhere(_fx.Cache, [one, two], [], tree.EnsureConnectedAsync));

        await Type(vm, "accounts resto2");
        await vm.ConfirmAsync();

        Assert.Equal(["Two accounts"], _opened);
        Assert.NotNull(_fx.Sessions.Find(two.Id));
    }

    [Fact]
    public async Task PlainQuery_StaysInTheConnection()
    {
        var (one, two) = TwoRestaurants();
        var (vm, _) = Here(one, Restaurant, two);

        foreach (var query in new[] { "accounts", "acc", "dbo.orders", "accounts resto1", "accounts resto", "email accounts", "" })
        {
            await Type(vm, query);
            Assert.Null(vm.ExtensionText);
            Assert.All(vm.Results, r => Assert.Null(r.Context));
        }

        Assert.Equal(3, vm.Results.Count);
    }

    [Fact]
    public async Task WordTheActiveConnectionHasInAName_DoesNotExtend()
    {
        var one = Server("Main", "shop");
        var two = Cached(Server("Archive", "archive"), Info("dbo", "accounts"));
        var (vm, _) = Here(one, [Info("dbo", "accounts"), Info("dbo", "archive_log")], two);

        await Type(vm, "archive");

        Assert.Null(vm.ExtensionText);
        Assert.Equal("dbo.archive_log", Assert.Single(vm.Results).Label);
    }

    [Fact]
    public async Task NothingHere_AndAnotherConnectionNamed_Extends()
    {
        var one = Server("Main", "shop");
        var two = Cached(Server("Archive", "archive"), Info("dbo", "invoices"));
        var (vm, _) = Here(one, [Info("dbo", "accounts"), Info("dbo", "archive_log")], two);

        await Type(vm, "invoices archive");

        Assert.Equal("Archive · dbo.invoices", Assert.Single(vm.Results).Label);
        Assert.NotNull(vm.ExtensionText);
    }

    [Fact]
    public async Task Extended_ExactMatchOfTheActiveConnectionStaysFirst()
    {
        var one = Server("Main", "shop");
        var two = Cached(Server("Stock", "wares"), Info("dbo", "accounts"));
        // "wares" is not written anywhere here, but is scattered over the schema of the second table.
        var (vm, _) = Here(one, [Info("dbo", "accounts_old"), Info("warehouse_sales", "accounts")], two);

        await Type(vm, "accounts wares");

        Assert.NotNull(vm.ExtensionText);
        Assert.Equal(["warehouse_sales.accounts", "Stock · dbo.accounts"], vm.Results.Select(r => r.Label));
    }

    [Fact]
    public async Task WordNamingTheActiveConnectionToo_ExtendsOnlyWhenNothingIsHere()
    {
        var one = Server("Zulu", "a", environment: DbEnvironment.Production);
        var two = Cached(Server("Alpha", "b", environment: DbEnvironment.Production), Info("dbo", "accounts"), Info("dbo", "invoices"));
        var (vm, _) = Here(one, [Info("dbo", "accounts")], two);

        await Type(vm, "accounts prod");
        Assert.Null(vm.ExtensionText); // satisfied here
        Assert.Equal("dbo.accounts", Assert.Single(vm.Results).Label);

        await Type(vm, "invoices prod");
        Assert.Equal("Alpha · dbo.invoices", Assert.Single(vm.Results).Label);
        Assert.StartsWith("Nothing in Zulu matches", vm.ExtensionText);
    }

    [Fact]
    public async Task Extended_RowsOfTheActiveConnectionCarryTheirContext()
    {
        var one = Server("Main", "shop");
        var two = Cached(Server("Stock", "wares"), Info("dbo", "accounts"));
        // "wares" is nothing here by name, but a scattered match of a table of this connection.
        var (vm, _) = Here(one, [Info("dbo", "warehouse_sales")], two);

        await Type(vm, "wares");

        Assert.Equal("Main / shop", Assert.Single(vm.Results, r => r.Label == "dbo.warehouse_sales").Context);
        Assert.Contains(vm.Results, r => r.Label == "Stock · dbo.accounts");
    }

    [Fact]
    public async Task Extension_FindsColumnsOfOtherConnections()
    {
        var (one, two) = TwoRestaurants();
        var (vm, _) = Here(one, Restaurant, two);

        await Type(vm, "email resto2");

        Assert.Equal(("email", "Resto two / resto2"), (vm.Results[0].DisplayLabel, vm.Results[0].Context));
    }

    [Fact]
    public async Task Extension_NamedConnectionWithoutCache_SaysSo()
    {
        var one = Server("Main", "shop");
        var never = Server("Never", "resto9");
        var (vm, _) = Here(one, Restaurant, never);

        await Type(vm, "accounts resto9");

        Assert.Empty(vm.Results);
        Assert.Contains("No cache yet for Never", vm.ExtensionText);
    }

    [Fact]
    public async Task Extension_GoesAwayWithTheWord_AndFollowsTheScopeSwitch()
    {
        var (one, two) = TwoRestaurants();
        var (vm, _) = Here(one, Restaurant, two);
        vm.OtherScope = () => All(null, one, two);

        await Type(vm, "accounts resto2");
        Assert.NotNull(vm.ExtensionText);
        vm.SwitchScope();
        Assert.Null(vm.ExtensionText);
        Assert.Equal("Resto two · dbo.accounts", First(vm));
        vm.SwitchScope();
        Assert.NotNull(vm.ExtensionText);

        await Type(vm, "accounts");
        Assert.Null(vm.ExtensionText);
        Assert.Equal("dbo.accounts", First(vm));
    }

    [Fact]
    public async Task Main_CtrlT_NamingAnotherDatabase_OpensTheTableThere()
    {
        var a = Conn("Alpha", database: CreateDb("resto1.db", "accounts"));
        var b = Conn("Beta", database: CreateDb("resto2.db", "accounts"));
        await _fx.Sessions.GetOrOpenAsync(b, CancellationToken.None);
        _fx.Sessions.Close(b.Id);
        var main = new MainViewModel(_fx.CreateTree(new AppConfig([], [a, b], new())), _fx.Status, _fx.Cache);
        await main.OpenTable(await _fx.Sessions.GetOrOpenAsync(a, CancellationToken.None), new TableRef(null, "accounts"));

        main.ShowTablePicker();
        await Type(main.QuickOpen!, "accounts resto2");
        Assert.Null(_fx.Sessions.Find(b.Id));
        await main.QuickOpen!.ConfirmAsync();

        Assert.Equal("Beta · accounts", main.ActiveTab!.Title);
    }

    // ---- highlight ----

    [Fact]
    public void Highlight_MarksEveryWord_InAnyOrder()
    {
        Assert.Equal([0, 1, 2, 4, 5, 6], HighlightText.Match("dbo.accounts", "acc dbo"));
        Assert.Equal([4, 5, 6], HighlightText.Match("dbo.accounts", "resto2 acc"));
        // The context chip marks only what it contains.
        Assert.Equal([7, 8, 9], HighlightText.Match("Main / abc", "abc mn", wholeOnly: true));
        // A word read in the chip is not scattered over the name.
        Assert.Empty(HighlightText.Match("report_store", "resto", elsewhere: "Resto two"));
    }
}
