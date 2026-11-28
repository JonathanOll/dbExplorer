using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;
using Microsoft.Data.Sqlite;

namespace DbExplorer.App.Tests;

/// <summary>The main window area: status bar model, navigator filter and states, quick-open extras, tab commands, confirmation requests.</summary>
public class MainAreaTests : IDisposable
{
    private readonly TreeFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private string CreateDb(string name, string sql)
    {
        var file = Path.Combine(_fx.Dir, name);
        using var c = new SqliteConnection($"Data Source={file};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        return file;
    }

    private MainViewModel NewMain(AppConfig config) => new(_fx.CreateTree(config), _fx.Status, _fx.Cache);

    private MainViewModel NewMain(params ConnectionConfig[] configs) => NewMain(new AppConfig([], configs.ToList(), new()));

    // ---- status model ----

    [Fact]
    public void Status_InfoAndSuccessFade_ErrorsStayUntilDismissed()
    {
        var status = new StatusModel();
        Action? fade = null;
        status.Schedule = (_, action) => fade = action;

        status.Success("Saved.");
        Assert.Equal(StatusSeverity.Success, status.Severity);
        Assert.False(status.IsSticky);
        fade!();
        Assert.Equal("", status.Message);

        fade = null;
        status.Error("It failed.");
        Assert.True(status.IsError);
        Assert.True(status.IsSticky);
        Assert.Null(fade);   // errors are never scheduled to fade
        status.Dismiss();
        Assert.Equal("", status.Message);
        Assert.False(status.IsError);
    }

    [Fact]
    public void Status_ANewerMessageIsNotClearedByTheOlderTimer()
    {
        var status = new StatusModel();
        var timers = new List<Action>();
        status.Schedule = (_, action) => timers.Add(action);

        status.Info("first");
        status.Info("second");
        timers[0]();

        Assert.Equal("second", status.Message);
        timers[1]();
        Assert.Equal("", status.Message);
    }

    [Fact]
    public void Status_KeepsAHistory_NewestFirst_AndCapsIt()
    {
        var status = new StatusModel { AutoClearDelay = null };
        for (var i = 0; i < StatusModel.MaxHistory + 5; i++) status.Info($"m{i}");

        Assert.Equal(StatusModel.MaxHistory, status.History.Count);
        Assert.Equal($"m{StatusModel.MaxHistory + 4}", status.History[0].Message);
        Assert.True(status.HasHistory);
    }

    [Fact]
    public void Status_BusyWithCancel_OffersCancelUntilTheScopeEnds()
    {
        var status = new StatusModel();
        var cancelled = 0;

        using (status.Busy("Working…", () => cancelled++))
        {
            Assert.True(status.IsBusy);
            Assert.True(status.CanCancel);
            Assert.False(status.ShowMessage);
            status.CancelBusy();
        }

        Assert.Equal(1, cancelled);
        Assert.False(status.IsBusy);
        Assert.False(status.CanCancel);
    }

    [Fact]
    public void Status_BusyWithoutCancel_OffersNoCancel()
    {
        var status = new StatusModel();
        using (status.Busy("Importing…")) Assert.False(status.CanCancel);
    }

    // ---- navigator ----

    [Fact]
    public void Tree_IsEmptyWithoutConfiguration_AndGroupsComeLast()
    {
        Assert.True(_fx.CreateTree(new AppConfig([], [], new())).IsEmpty);

        var a = TreeFixture.Conn("Alpha");
        var b = TreeFixture.Conn("Beta");
        var group = new ConnectionGroup(Guid.NewGuid(), "Pair", [a.Id, b.Id]);
        var tree = _fx.CreateTree(new AppConfig(["Prod"], [a with { Folder = "Prod" }, b], new()) { Groups = [group] });

        Assert.False(tree.IsEmpty);
        Assert.IsType<GroupsSectionNodeViewModel>(tree.Nodes[^1]);
        Assert.IsType<FolderNodeViewModel>(tree.Nodes[0]);
    }

    [Fact]
    public void Filter_ShowsMatchingConnectionsAndFolders_AndSaysWhenNothingMatches()
    {
        var tree = _fx.CreateTree(new AppConfig(["Prod"],
            [TreeFixture.Conn("Billing", "Prod"), TreeFixture.Conn("Shop", "Prod"), TreeFixture.Conn("Notes")], new()));
        var folder = tree.Nodes.OfType<FolderNodeViewModel>().Single();
        var notes = tree.Nodes.OfType<ConnectionNodeViewModel>().Single();

        tree.FilterText = "bill";

        Assert.True(folder.IsShown);   // holds a match
        Assert.True(folder.IsExpanded);
        Assert.True(folder.Children.Cast<ConnectionNodeViewModel>().Single(c => c.Title == "Billing").IsShown);
        Assert.False(folder.Children.Cast<ConnectionNodeViewModel>().Single(c => c.Title == "Shop").IsShown);
        Assert.False(notes.IsShown);
        Assert.False(tree.NoMatch);

        tree.FilterText = "zzz";
        Assert.True(tree.NoMatch);
        Assert.True(tree.HasFilter);

        tree.ClearFilterCommand.Execute(null);
        Assert.False(tree.NoMatch);
        Assert.True(notes.IsShown);
        Assert.All(folder.Children, c => Assert.True(c.IsShown));
    }

    [Fact]
    public void Filter_MatchingAFolderKeepsEverythingInIt()
    {
        var tree = _fx.CreateTree(new AppConfig(["Production"], [TreeFixture.Conn("Billing", "Production"), TreeFixture.Conn("Notes")], new()));

        tree.FilterText = "produc";

        var folder = tree.Nodes.OfType<FolderNodeViewModel>().Single();
        Assert.All(folder.Children, c => Assert.True(c.IsShown));
    }

    [Fact]
    public async Task Filter_FindsTablesOfAConnectedConnection()
    {
        var config = TreeFixture.Conn("Shop", database: CreateDb("shop.db", "CREATE TABLE orders (id INTEGER); CREATE TABLE users (id INTEGER)"));
        var tree = _fx.CreateTree(new AppConfig([], [config, TreeFixture.Conn("Notes")], new()));
        var shop = tree.Nodes.OfType<ConnectionNodeViewModel>().Single(n => n.Title == "Shop");
        await tree.ConnectAsync(shop);

        tree.FilterText = "ord";

        Assert.True(shop.IsShown);
        Assert.Equal("orders", shop.Children.Single(c => c.IsShown).Title);
        Assert.False(tree.Nodes.OfType<ConnectionNodeViewModel>().Single(n => n.Title == "Notes").IsShown);
    }

    [Fact]
    public void ConnectionNode_DescribesItsState_ForTooltipsAndScreenReaders()
    {
        var node = new ConnectionNodeViewModel(TreeFixture.Conn("Shop", database: "/data/shop.db") with { ReadOnly = true });

        Assert.Equal("Not connected", node.StateText);
        Assert.True(node.IsDisconnected);
        Assert.Contains("Shop", node.Tooltip);
        Assert.Contains("SQLite · /data/shop.db", node.Tooltip);
        Assert.Contains("Read-only", node.Tooltip);

        node.State = ConnectionState.Failed;
        node.ErrorMessage = "Login failed for user.";
        Assert.Equal("Failed: Login failed for user.", node.StateText);
        Assert.Contains("Double-click to retry", node.Tooltip);
        Assert.False(node.IsDisconnected);

        node.State = ConnectionState.Connected;
        Assert.Equal("Connected", node.StateText);
    }

    [Fact]
    public void ConnectionNode_TooltipNamesHostPortAndUser_NeverThePassword()
    {
        var config = TreeFixture.Conn("Billing", database: "billing", kind: DbKind.Postgres) with { Port = 5433, User = "app", ProtectedPassword = "p:secret" };

        var tip = new ConnectionNodeViewModel(config).Tooltip;

        Assert.Contains("PostgreSQL · localhost:5433/billing · app", tip);
        Assert.DoesNotContain("secret", tip);
    }

    [Fact]
    public void DuplicateConnection_AddsACopyWithAFreshNameAndId()
    {
        var original = TreeFixture.Conn("Shop");
        var tree = _fx.CreateTree(new AppConfig([], [original], new()));
        var node = tree.Nodes.OfType<ConnectionNodeViewModel>().Single();

        tree.DuplicateConnectionCommand.Execute(node);
        tree.DuplicateConnectionCommand.Execute(node);

        var names = tree.Config.Connections.Select(c => c.Name).ToList();
        Assert.Equal(["Shop", "Shop (copy)", "Shop (copy 2)"], names);
        Assert.Equal(3, tree.Config.Connections.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public async Task Connect_PluralisesTheCount_AndOffersCancelWhileBusy()
    {
        var one = TreeFixture.Conn("One", database: CreateDb("one.db", "CREATE TABLE t (id INTEGER)"));
        var tree = _fx.CreateTree(new AppConfig([], [one], new()));
        var node = tree.Nodes.OfType<ConnectionNodeViewModel>().Single();

        await tree.ConnectAsync(node);

        Assert.Contains("1 table or view", _fx.Status.Message);
        Assert.Equal(StatusSeverity.Success, _fx.Status.Severity);
        Assert.False(_fx.Status.CanCancel);
    }

    [Fact]
    public async Task Connect_Failure_SaysHowToRetry()
    {
        var broken = TreeFixture.Conn("Broken", database: Path.Combine(_fx.Dir, "missing", "none.db"));
        var tree = _fx.CreateTree(new AppConfig([], [broken], new()));
        var node = tree.Nodes.OfType<ConnectionNodeViewModel>().Single();

        await tree.ConnectAsync(node);

        Assert.True(node.HasError);
        Assert.Contains("double-click it to retry", _fx.Status.Message);
        _fx.Status.Dismiss();
        tree.ShowErrorCommand.Execute(node);
        Assert.True(_fx.Status.IsError);
    }

    // ---- confirmation and prompt requests ----

    [Fact]
    public async Task DeleteConnection_AsksWithADangerRequest_AndTheDefaultFakeStillAnswers()
    {
        var tree = _fx.CreateTree(new AppConfig([], [TreeFixture.Conn("Shop")], new()));
        var dialogs = new RecordingDialogs();
        var asking = new ConnectionTreeViewModel(_fx.Store, tree.Config, _fx.Sessions, _fx.Status, dialogs, _fx.DBeaverDir);

        await asking.DeleteConnectionCommand.ExecuteAsync(asking.Nodes.OfType<ConnectionNodeViewModel>().Single());

        var request = Assert.Single(dialogs.Confirms);
        Assert.Equal("Delete", request.ConfirmText);
        Assert.Equal(DialogTone.Danger, request.Tone);
        Assert.Contains("'Shop'", request.Message);
        Assert.NotNull(request.Detail);
        Assert.Empty(asking.Config.Connections);

        // a dialog service that only knows the plain form still works through the default interface method
        var kept = _fx.CreateTree(new AppConfig([], [TreeFixture.Conn("Kept")], new()));
        _fx.Dialogs.ConfirmResult = false;
        await kept.DeleteConnectionCommand.ExecuteAsync(kept.Nodes.OfType<ConnectionNodeViewModel>().Single());
        Assert.Single(kept.Config.Connections);
    }

    [Fact]
    public async Task FolderPrompt_ValidatesEmptyAndDuplicateNames()
    {
        var tree = _fx.CreateTree(new AppConfig(["Prod"], [], new()));
        var dialogs = new RecordingDialogs { PromptResult = "Staging" };
        var asking = new ConnectionTreeViewModel(_fx.Store, tree.Config, _fx.Sessions, _fx.Status, dialogs, _fx.DBeaverDir);

        await asking.AddFolderCommand.ExecuteAsync(null);

        var request = Assert.Single(dialogs.Prompts);
        Assert.Equal("Create", request.OkText);
        Assert.NotNull(request.Validate!("   "));
        Assert.Contains("already exists", request.Validate("prod"));
        Assert.Null(request.Validate("Zeta"));
        Assert.Contains("Staging", asking.Config.Folders);
    }

    private static QuickOpenItem Item(string label) => new(label, null, () => Task.CompletedTask);

    // ---- quick open ----

    [Fact]
    public void QuickOpen_CountsMatches_AndSaysWhenNothingMatches()
    {
        var items = Enumerable.Range(0, 70).Select(i => new QuickOpenItem($"table{i:00}", null, () => Task.CompletedTask)).ToList();
        var vm = new QuickOpenViewModel("Go to table", items);

        Assert.Equal(70, vm.MatchCount);
        Assert.Equal("50 of 70", vm.CountText);

        var few = new QuickOpenViewModel("Go to table", [Item("orders"), Item("order_items"), Item("customers")]);
        Assert.Equal("3 results", few.CountText);
        few.Query = "customers";
        Assert.Equal(1, few.MatchCount);
        Assert.Equal("1 result", few.CountText);

        few.Query = "zzzzzzzz";
        Assert.True(few.IsEmpty);
        Assert.Contains("zzzzzzzz", few.EmptyText);
    }

    [Fact]
    public void QuickOpen_PagingStopsAtTheEnds()
    {
        var items = Enumerable.Range(0, 20).Select(i => new QuickOpenItem($"t{i:00}", null, () => Task.CompletedTask)).ToList();
        var vm = new QuickOpenViewModel("Go", items);

        vm.MoveBy(8);
        Assert.Equal(8, vm.SelectedIndex);
        vm.MoveBy(100);
        Assert.Equal(19, vm.SelectedIndex);
        vm.MoveBy(-100);
        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public void QuickOpen_AllConnectionsRows_ShowTableAndConnectionSeparately_ButSearchBoth()
    {
        var cfg = TreeFixture.Conn("Billing");
        var cache = _fx.Cache;
        cache.Save(cfg.Id, [new TableInfo(new TableRef(null, "invoices"), false, [], [], [])]);

        var vm = QuickOpenViewModel.ForAllConnections(cache, [cfg], _ => Task.FromResult<DbExplorer.Core.Sessions.Session?>(null), (_, _) => Task.CompletedTask);

        var row = vm.Results.Single();
        Assert.Equal("invoices", row.DisplayLabel);
        Assert.Equal("Billing / x", row.Context);
        Assert.True(row.HasContext);
        vm.Query = "billing inv";
        Assert.Single(vm.Results);
    }

    [Fact]
    public void HighlightText_MarksTheQueryCharactersInOrder()
    {
        Assert.Equal([0, 1, 4], Views.HighlightText.Match("order_items", "orr"));
        Assert.Empty(Views.HighlightText.Match("orders", "xyz"));
        Assert.Empty(Views.HighlightText.Match("orders", ""));
        Assert.Equal([0, 3], Views.HighlightText.Match("orders", "O e"));
    }

    // ---- main view model ----

    [Fact]
    public void Main_WelcomeHintAndTarget_FollowTheSelection()
    {
        var empty = NewMain(new AppConfig([], [], new()));
        Assert.False(empty.HasConnections);
        Assert.False(empty.HasTarget);
        Assert.Contains("Add a connection", empty.WelcomeHint);

        var main = NewMain(TreeFixture.Conn("Shop"));
        Assert.True(main.HasConnections);
        Assert.False(main.HasTarget);
        Assert.Contains("Select a connection", main.WelcomeHint);

        main.Tree.SelectedNode = main.Tree.Nodes.OfType<ConnectionNodeViewModel>().Single();
        Assert.True(main.HasTarget);
        Assert.Equal("Selected connection: Shop", main.WelcomeHint);
        Assert.Equal("Shop", main.CurrentConnectionNode!.Title);
    }

    [Fact]
    public async Task Main_SwitchingToAnotherConnectionsTab_ClearsTheConnectedMessageOfTheFirst()
    {
        var shop = TreeFixture.Conn("Shop", database: CreateDb("shop-msg.db", "CREATE TABLE t (id INTEGER)"));
        var billing = TreeFixture.Conn("Billing", database: CreateDb("billing-msg.db", "CREATE TABLE u (id INTEGER)"));
        var main = NewMain(shop, billing);
        await main.OpenTable(await _fx.Sessions.GetOrOpenAsync(shop, CancellationToken.None), new TableRef(null, "t"));
        await main.OpenTable(await _fx.Sessions.GetOrOpenAsync(billing, CancellationToken.None), new TableRef(null, "u"));

        main.Status.Success("Connected to Billing: 1 table or view.", billing.Id);
        main.ActiveTab = main.Tabs[1];   // still Billing: the message is about it
        Assert.Equal("Connected to Billing: 1 table or view.", main.Status.Message);

        main.ActiveTab = main.Tabs[0];   // Shop: the message would read as if it were about this tab
        Assert.Equal("", main.Status.Message);

        main.Status.Error("Billing failed");
        main.ActiveTab = main.Tabs[1];
        Assert.Equal("Billing failed", main.Status.Message);   // errors stay until dismissed
    }

    [Fact]
    public void Main_MenuTips_SayWhyScriptAndTableEntriesAreDisabled()
    {
        var shop = TreeFixture.Conn("Shop", database: CreateDb("shop-tips.db", "CREATE TABLE users (id INTEGER)"));
        var main = NewMain(shop);
        Assert.Equal("Open a script tab first", main.ScriptMenuTip);
        Assert.Equal("Open a script tab first", main.SaveAsMenuTip);
        Assert.Equal("Open a script tab first", main.ActualPlanMenuTip);
        Assert.Equal("Open a table first", main.TableMenuTip);

        main.Tree.SelectedNode = main.Tree.Nodes.OfType<ConnectionNodeViewModel>().Single();
        Assert.NotNull(main.NewScript());
        Assert.Null(main.ScriptMenuTip);
        Assert.Equal("Choose any file with the system file dialog", main.SaveAsMenuTip);
        Assert.Equal("SQLite only has an estimated plan", main.ActualPlanMenuTip);
        Assert.Equal("Only SQL Server has IO and time statistics", main.StatisticsMenuTip);
        Assert.Equal("Open a table first", main.TableMenuTip);
    }

    [Fact]
    public async Task Main_WindowTitle_NamesTheActiveTab()
    {
        var shop = TreeFixture.Conn("Shop", database: CreateDb("shop.db", "CREATE TABLE users (id INTEGER)"));
        var main = NewMain(shop);
        Assert.Equal("dbExplorer", main.WindowTitle);

        var session = await _fx.Sessions.GetOrOpenAsync(shop, CancellationToken.None);
        await main.OpenTable(session, new TableRef(null, "users"));

        Assert.Equal("Shop · users – dbExplorer", main.WindowTitle);
    }

    [Fact]
    public async Task Main_TabCommands_CloseOthersToTheRightAndAll()
    {
        var shop = TreeFixture.Conn("Shop", database: CreateDb("shop.db", "CREATE TABLE a (id INTEGER); CREATE TABLE b (id INTEGER); CREATE TABLE c (id INTEGER)"));
        var main = NewMain(shop);
        var session = await _fx.Sessions.GetOrOpenAsync(shop, CancellationToken.None);
        foreach (var name in new[] { "a", "b", "c" }) await main.OpenTable(session, new TableRef(null, name));
        var middle = main.Tabs[1];

        await main.CloseTabsToTheRightCommand.ExecuteAsync(middle);
        Assert.Equal(2, main.Tabs.Count);

        await main.OpenTable(session, new TableRef(null, "c"));
        await main.CloseOtherTabsCommand.ExecuteAsync(middle);
        Assert.Single(main.Tabs);
        Assert.Same(middle, main.Tabs[0]);

        await main.CloseAllTabsCommand.ExecuteAsync(null);
        Assert.Empty(main.Tabs);
        Assert.True(main.IsEmptyState);
    }

    [Fact]
    public void Main_ShortcutsCard_OpensWithF1_AndClosesWithEscape()
    {
        var main = NewMain(new AppConfig([], [], new()));

        Assert.True(Views.MainShortcuts.Handle(main, Avalonia.Input.Key.F1, Avalonia.Input.KeyModifiers.None));
        Assert.True(main.IsShortcutsVisible);
        Assert.True(Views.MainShortcuts.Handle(main, Avalonia.Input.Key.Escape, Avalonia.Input.KeyModifiers.None));
        Assert.False(main.IsShortcutsVisible);
        Assert.False(Views.MainShortcuts.Handle(main, Avalonia.Input.Key.Escape, Avalonia.Input.KeyModifiers.None));   // Esc belongs to others otherwise
    }

    [Fact]
    public void ShortcutCatalog_ListsEveryHighlightInTheFullList()
    {
        var all = Views.ShortcutCatalog.Sections.SelectMany(s => s.Items).Select(i => i.Keys).ToHashSet();
        Assert.All(Views.ShortcutCatalog.Highlights, h => Assert.Contains(h.Keys, all));
    }

    private sealed class RecordingDialogs : IDialogService
    {
        public List<ConfirmRequest> Confirms { get; } = [];
        public List<PromptRequest> Prompts { get; } = [];
        public string? PromptResult { get; set; }

        public Task<bool> ConfirmAsync(ConfirmRequest request)
        {
            Confirms.Add(request);
            return Task.FromResult(true);
        }

        public Task<string?> PromptTextAsync(PromptRequest request)
        {
            Prompts.Add(request);
            return Task.FromResult(PromptResult);
        }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task<string?> PromptTextAsync(string title, string label, string initial) => Task.FromResult(PromptResult);
        public Task<bool> ShowConnectionDialogAsync(ConnectionDialogViewModel viewModel) => Task.FromResult(false);
        public Task<bool> ShowGroupEditorAsync(GroupEditorViewModel viewModel) => Task.FromResult(false);
        public Task ShowSchemaDiffAsync(SchemaDiffViewModel viewModel) => Task.CompletedTask;
        public Task<int?> ChooseAsync(string title, string label, IReadOnlyList<string> options) => Task.FromResult<int?>(null);
    }
}
