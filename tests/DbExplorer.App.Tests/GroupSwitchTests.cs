using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;
using DbExplorer.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace DbExplorer.App.Tests;

/// <summary>
/// Members A, B and Prod (read-only) share the table <c>big</c> (450 rows, labels start with the member's letter);
/// <c>only_a</c> exists in A only. Group "Items" = [A, B, Prod]; group "Broken" = [A, NoBig (no such table), Down (unreachable)];
/// Solo is in no group. Everything goes through a real tree, like the shell does.
/// </summary>
internal sealed class GroupSwitchFixture : IDisposable
{
    public TreeFixture Fx { get; } = new();
    public FakeEditDialogs Dialogs { get; } = new();
    public ConnectionConfig A { get; }
    public ConnectionConfig B { get; }
    public ConnectionConfig Prod { get; }
    public ConnectionConfig NoBig { get; }
    public ConnectionConfig Down { get; }
    public ConnectionConfig Solo { get; }
    public ConnectionGroup Items { get; }
    public ConnectionGroup Broken { get; }
    public ConnectionTreeViewModel Tree { get; }
    public string FileA { get; }
    private readonly List<ScriptTabViewModel> _scripts = [];

    public GroupSwitchFixture()
    {
        string Big(string name, string prefix, bool extra = false) => Fx.CreateSqliteFileWith(name, $"""
            CREATE TABLE big (id INTEGER PRIMARY KEY, label TEXT NOT NULL);
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 450)
            INSERT INTO big SELECT i, '{prefix}' || i FROM n;
            {(extra ? "CREATE TABLE only_a (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO only_a VALUES (1, 'x');" : "")}
            """);

        FileA = Big("a.db", "a", extra: true);
        A = TreeFixture.Conn("A", database: FileA);
        B = TreeFixture.Conn("B", database: Big("b.db", "b"));
        Prod = TreeFixture.Conn("Prod", database: Big("p.db", "p")) with { ReadOnly = true };
        NoBig = TreeFixture.Conn("NoBig", database: Fx.CreateSqliteFileWith("n.db", "CREATE TABLE other (id INTEGER PRIMARY KEY);"));
        Down = TreeFixture.Conn("Down", database: Path.Combine(Fx.Dir, "no-such-dir", "down.db"));
        Solo = TreeFixture.Conn("Solo", database: Big("s.db", "s"));

        Items = new ConnectionGroup(Guid.NewGuid(), "Items", [A.Id, B.Id, Prod.Id]);
        Broken = new ConnectionGroup(Guid.NewGuid(), "Broken", [A.Id, NoBig.Id, Down.Id]);
        var config = new AppConfig([], [A, B, Prod, NoBig, Down, Solo], new()) { Groups = [Items, Broken] };
        Tree = Fx.CreateTree(config);
    }

    /// <summary>What MainViewModel hands to its tabs: sessions resolved through the tree (node state follows).</summary>
    public GroupSwitchHooks Switch => new(() => Tree.Config, ResolveAsync);

    public async Task<Session?> ResolveAsync(Guid id, CancellationToken ct)
    {
        var config = Tree.Config.Connections.FirstOrDefault(c => c.Id == id);
        if (config is null) return null;
        return await Tree.EnsureConnectedAsync(config, ct)
               ?? throw new InvalidOperationException($"{config.Name} is not available (see the status bar).");
    }

    public async Task<TableTabViewModel> OpenTable(ConnectionConfig connection, string table = "big", bool withSwitch = true)
    {
        var session = (await ResolveAsync(connection.Id, CancellationToken.None))!;
        var hooks = new TableTabHooks(
            ct => ResolveAsync(connection.Id, ct), (_, _, _) => Task.CompletedTask, Dialogs, () => Tree.Config,
            Tree.SetVirtualKey, withSwitch ? Switch : null);
        var tab = new TableTabViewModel(session, new TableRef(null, table), hooks);
        await tab.LoadAsync();
        return tab;
    }

    public async Task<ScriptTabViewModel> NewScript(ConnectionConfig connection, string text = "")
    {
        var session = await ResolveAsync(connection.Id, CancellationToken.None);
        var hooks = new ScriptTabHooks(ct => ResolveAsync(connection.Id, ct), null, null, Fx.Status, Dialogs, Switch);
        var tab = new ScriptTabViewModel(connection, session, hooks, 1) { Text = text };
        _scripts.Add(tab);
        return tab;
    }

    public static async Task Run(ScriptTabViewModel tab, string sql)
    {
        tab.Text = sql;
        tab.CaretOffset = 0;
        await tab.RunStatementAsync();
    }

    public long Scalar(string file, string sql)
    {
        using var c = new SqliteConnection($"Data Source={file};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Dispose()
    {
        foreach (var script in _scripts) script.Close();
        foreach (var session in Fx.Sessions.Open) Fx.Sessions.Close(session.Config.Id);
        Fx.Dispose();
    }
}

public class GroupSwitchTests : IDisposable
{
    private readonly GroupSwitchFixture _g = new();

    public void Dispose() => _g.Dispose();

    private static string Label(TableTabViewModel tab, int row = 0) => (string)tab.Rows[row].Values[tab.FindColumn("label")!.Value]!;

    private static long Id(TableTabViewModel tab, int row = 0) => (long)tab.Rows[row].Values[tab.FindColumn("id")!.Value]!;

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(10);
        }
    }

    // ---- table tabs ----

    [Fact]
    public async Task Table_Switch_ReloadsSameTable_WithSameFilters_PageZero()
    {
        var tab = await _g.OpenTable(_g.A);
        tab.ColumnFilters["id"] = ">100";
        tab.RawWhere = "id < 400";
        await tab.ApplyFiltersAsync();
        await tab.ToggleSortAsync("id");
        await tab.ToggleSortAsync("id");   // descending
        await tab.NextPage();
        Assert.Equal(1, tab.PageIndex);
        Assert.StartsWith("a", Label(tab));

        Assert.True(await tab.SwitchConnectionAsync(_g.B));

        Assert.Equal(_g.B.Id, tab.Connection.Id);
        Assert.Equal(0, tab.PageIndex);
        Assert.Equal(200, tab.Rows.Count);
        Assert.True(tab.HasNextPage);
        Assert.Equal(399, Id(tab));   // same filters, raw WHERE and sort (descending)
        Assert.StartsWith("b", Label(tab));
        Assert.Equal(">100", tab.ColumnFilters["id"]);
        Assert.Equal("id < 400", tab.RawWhere);
        Assert.Equal(new SortSpec("id", true), tab.Sort);
        Assert.Null(tab.ErrorMessage);
        Assert.Null(tab.TotalCount);
        Assert.True(_g.Tree.NodeOf(_g.B.Id)!.IsConnected);   // resolved through the tree
    }

    [Fact]
    public async Task Table_Switch_WithPendingEdits_PromptsFirst_CancelKeepsTheTab()
    {
        var tab = await _g.OpenTable(_g.A);
        Assert.True(tab.SetCell(0, tab.FindColumn("label")!.Value, "edited"));
        _g.Dialogs.Choice = SaveChoice.Cancel;

        Assert.False(await tab.SwitchConnectionAsync(_g.B));

        Assert.Equal(1, _g.Dialogs.AskCount);
        Assert.Equal(_g.A.Id, tab.Connection.Id);
        Assert.True(tab.HasPendingEdits);
        Assert.Equal("edited", Label(tab));
        Assert.False(_g.Tree.NodeOf(_g.B.Id)!.IsConnected);   // asked BEFORE connecting anywhere
    }

    [Fact]
    public async Task Table_Switch_WithPendingEdits_Discard_SwitchesAndDropsTheEdits()
    {
        var tab = await _g.OpenTable(_g.A);
        tab.SetCell(0, tab.FindColumn("label")!.Value, "edited");
        _g.Dialogs.Choice = SaveChoice.Discard;

        Assert.True(await tab.SwitchConnectionAsync(_g.B));

        Assert.Equal(_g.B.Id, tab.Connection.Id);
        Assert.False(tab.HasPendingEdits);
        Assert.Equal("b1", Label(tab));
        Assert.Equal(0, _g.Scalar(_g.FileA, "SELECT COUNT(*) FROM big WHERE label = 'edited'"));
    }

    [Fact]
    public async Task Table_Switch_TableMissingInTarget_ShowsErrorStrip_AndKeepsPreviousRows()
    {
        var tab = await _g.OpenTable(_g.A, "only_a");
        var rows = tab.Rows;
        Assert.Single(rows);

        Assert.False(await tab.SwitchConnectionAsync(_g.B));   // B has no only_a

        Assert.Contains("only_a", tab.ErrorMessage);
        Assert.Contains("B", tab.ErrorMessage);
        Assert.Same(rows, tab.Rows);
        Assert.Equal(_g.A.Id, tab.Connection.Id);
        Assert.False(tab.IsLoading);
    }

    [Fact]
    public async Task Table_Switch_ChipColorAndTitleFollowTheNewConnection()
    {
        var tab = await _g.OpenTable(_g.A);
        Assert.Equal("A · big", tab.Title);
        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(await tab.SwitchConnectionAsync(_g.B));

        Assert.Equal("B · big", tab.Title);
        Assert.Equal(ConnectionPalette.ColorFor(_g.B.Id), tab.ChipColor);
        Assert.Contains(nameof(TabViewModel.Connection), changed);
        Assert.Contains(nameof(TabViewModel.ChipColor), changed);
        Assert.Contains(nameof(TabViewModel.Title), changed);
        Assert.Equal(_g.B.Id, tab.SelectedMember!.Id);
    }

    [Fact]
    public async Task Table_Switch_ToReadOnlyMember_DisablesEditing_AndSwitchingBackReenablesIt()
    {
        var tab = await _g.OpenTable(_g.A);
        Assert.True(tab.CanEdit);
        Assert.True(tab.CanInsert);

        Assert.True(await tab.SwitchConnectionAsync(_g.Prod));

        Assert.Equal(_g.Prod.Id, tab.Connection.Id);
        Assert.False(tab.CanEdit);
        Assert.False(tab.CanInsert);
        Assert.Equal("read-only", tab.Badge);
        Assert.False(tab.SetCell(0, tab.FindColumn("label")!.Value, "x"));
        Assert.False(tab.HasPendingEdits);

        Assert.True(await tab.SwitchConnectionAsync(_g.A));
        Assert.True(tab.CanEdit);
        Assert.Null(tab.Badge);
        Assert.True(tab.SetCell(0, tab.FindColumn("label")!.Value, "x"));
    }

    [Fact]
    public async Task Table_Reload_AfterASwitch_StaysOnTheNewConnection()
    {
        var tab = await _g.OpenTable(_g.A);
        Assert.True(await tab.SwitchConnectionAsync(_g.B));

        await tab.ReloadAsync();   // its own hook is bound to A: the switch hook must win

        Assert.Equal(_g.B.Id, tab.Connection.Id);
        Assert.StartsWith("b", Label(tab));
        Assert.Null(tab.ErrorMessage);
    }

    [Fact]
    public async Task Switch_ToUnreachableMember_ShowsError_StaysOnPreviousConnection()
    {
        var tab = await _g.OpenTable(_g.A);
        tab.ActiveGroup = _g.Broken;
        var rows = tab.Rows;

        Assert.False(await tab.SwitchConnectionAsync(_g.Down));

        Assert.Contains("Down", tab.ErrorMessage);
        Assert.Equal(_g.A.Id, tab.Connection.Id);
        Assert.Same(rows, tab.Rows);
        Assert.False(tab.NeedsReload);
        Assert.False(tab.IsLoading);
        Assert.False(tab.IsSwitching);
        Assert.Equal("A · big", tab.Title);
    }

    [Fact]
    public async Task Table_Switch_ToMemberWithoutTheTable_InAnotherGroup_IsAnError_NotACrash()
    {
        var tab = await _g.OpenTable(_g.A);
        tab.ActiveGroup = _g.Broken;

        Assert.False(await tab.SwitchConnectionAsync(_g.NoBig));

        Assert.Contains("big", tab.ErrorMessage);
        Assert.Equal(_g.A.Id, tab.Connection.Id);
    }

    // ---- script tabs ----

    [Fact]
    public async Task Script_Switch_KeepsTextAndResults_UsesNewConnectionFromNextRun()
    {
        var tab = await _g.NewScript(_g.A);
        await GroupSwitchFixture.Run(tab, "BEGIN");
        await GroupSwitchFixture.Run(tab, "INSERT INTO big VALUES (9999, 'in-tx')");   // open transaction: A is write-locked
        var text = tab.Text;
        var results = tab.Results.ToList();
        Assert.Single(results);
        Assert.False(results[0].HasError);

        Assert.True(await tab.SwitchConnectionAsync(_g.B));

        Assert.Equal(_g.B.Id, tab.Connection.Id);
        Assert.Equal(text, tab.Text);
        Assert.Equal(results, tab.Results);   // same result objects
        Assert.Equal("• Script 1 · B", tab.Title);   // dirty: the text was never saved
        // The old dedicated connection was released: its transaction is rolled back and nobody holds A's write lock.
        Assert.Equal(0, _g.Scalar(_g.FileA, "SELECT COUNT(*) FROM big WHERE id = 9999"));
        using (var c = new SqliteConnection($"Data Source={_g.FileA};Pooling=False;Default Timeout=1"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO big VALUES (9998, 'free')";
            cmd.ExecuteNonQuery();
        }

        await GroupSwitchFixture.Run(tab, "SELECT label FROM big WHERE id = 1");

        Assert.Null(tab.ErrorMessage);
        Assert.Equal("b1", tab.Results.Single().Sets[0].Rows[0][0]);   // the next run is on B
    }

    [Fact]
    public async Task Script_Switch_WhileRunning_IsRefused()
    {
        var tab = await _g.NewScript(_g.A, ScriptTabFixture.Endless);
        var run = tab.RunScriptAsync();
        Assert.True(tab.IsRunning);
        await Task.Delay(100);

        Assert.False(await tab.SwitchConnectionAsync(_g.B));

        Assert.Equal(_g.A.Id, tab.Connection.Id);
        Assert.False(_g.Tree.NodeOf(_g.B.Id)!.IsConnected);
        tab.Cancel();
        await run;
    }

    [Fact]
    public async Task Script_Switch_ToUnreachableMember_ShowsError_StaysOnPreviousConnection()
    {
        var tab = await _g.NewScript(_g.A, "SELECT 1");
        tab.ActiveGroup = _g.Broken;

        Assert.False(await tab.SwitchConnectionAsync(_g.Down));

        Assert.Contains("Down", tab.ErrorMessage);
        Assert.Equal(_g.A.Id, tab.Connection.Id);
        Assert.Equal("SELECT 1", tab.Text);
        await GroupSwitchFixture.Run(tab, "SELECT label FROM big WHERE id = 1");
        Assert.Equal("a1", tab.Results.Single().Sets[0].Rows[0][0]);   // still works on A
    }

    [Fact]
    public async Task Script_Switch_ThenReconnect_ReconnectsTheNewConnection()
    {
        var tab = await _g.NewScript(_g.A);
        Assert.True(await tab.SwitchConnectionAsync(_g.B));
        _g.Tree.Disconnect(_g.B.Id);   // Disconnect B
        await GroupSwitchFixture.Run(tab, "SELECT 1");
        Assert.True(tab.NeedsReconnect);

        await tab.ReconnectAsync();

        Assert.False(tab.NeedsReconnect);
        await GroupSwitchFixture.Run(tab, "SELECT label FROM big WHERE id = 1");
        Assert.Equal("b1", tab.Results.Single().Sets[0].Rows[0][0]);   // B, not the connection the tab was created on
    }

    // ---- membership, refusals, wrap-around ----

    [Fact]
    public async Task Switch_TargetNotInGroup_IsRefused()
    {
        var table = await _g.OpenTable(_g.A);
        Assert.False(await table.SwitchConnectionAsync(_g.Solo));
        Assert.Equal(_g.A.Id, table.Connection.Id);
        Assert.NotNull(table.ErrorMessage);

        var script = await _g.NewScript(_g.A);
        Assert.False(await script.SwitchConnectionAsync(_g.NoBig));   // in the other group: not the active one
        Assert.Equal(_g.A.Id, script.Connection.Id);
    }

    [Fact]
    public async Task Switch_Relative_WrapsAround()
    {
        var tab = await _g.OpenTable(_g.A);   // Items = [A, B, Prod]

        Assert.True(await tab.SwitchRelativeAsync(-1));
        Assert.Equal(_g.Prod.Id, tab.Connection.Id);   // before the first: the last
        Assert.True(await tab.SwitchRelativeAsync(+1));
        Assert.Equal(_g.A.Id, tab.Connection.Id);       // after the last: the first
        Assert.True(await tab.SwitchRelativeAsync(+1));
        Assert.Equal(_g.B.Id, tab.Connection.Id);
        Assert.True(await tab.SwitchRelativeAsync(+1));
        Assert.Equal(_g.Prod.Id, tab.Connection.Id);
    }

    [Fact]
    public async Task ConnectionInNoGroup_HasNoMembers_NoSelector()
    {
        var tab = await _g.OpenTable(_g.Solo);

        Assert.Empty(tab.Groups);
        Assert.Null(tab.ActiveGroup);
        Assert.Empty(tab.GroupMembers);
        Assert.False(tab.HasGroupSwitch);
        Assert.False(tab.HasGroupPicker);
        Assert.Null(tab.SelectedMember);
        Assert.False(await tab.SwitchRelativeAsync(+1));
        Assert.False(await tab.SwitchConnectionAsync(_g.A));

        var withoutHooks = await _g.OpenTable(_g.A, withSwitch: false);   // a tab built without the shell's hooks never switches
        Assert.False(withoutHooks.HasGroupSwitch);
        Assert.False(await withoutHooks.SwitchConnectionAsync(_g.B));
    }

    [Fact]
    public async Task ConnectionInSeveralGroups_FirstIsActive_AndThePickerChangesTheMembers()
    {
        var tab = await _g.OpenTable(_g.A);

        Assert.Equal(["Items", "Broken"], tab.Groups.Select(g => g.Name));
        Assert.True(tab.HasGroupPicker);
        Assert.Equal("Items", tab.ActiveGroup!.Name);
        Assert.Equal(["A", "B", "Prod"], tab.GroupMembers.Select(m => m.Name));

        tab.ActiveGroup = _g.Broken;
        Assert.Equal(["A", "NoBig", "Down"], tab.GroupMembers.Select(m => m.Name));

        tab.ActiveGroup = new ConnectionGroup(Guid.NewGuid(), "Unknown", []);   // not a group of this connection: ignored
        Assert.Equal("Broken", tab.ActiveGroup!.Name);

        // After a switch inside the group the chosen group stays chosen.
        tab.ActiveGroup = _g.Items;
        Assert.True(await tab.SwitchConnectionAsync(_g.B));
        Assert.Equal("Items", tab.ActiveGroup!.Name);
        Assert.Equal(["A", "B", "Prod"], tab.GroupMembers.Select(m => m.Name));
    }

    [Fact]
    public async Task Tab_RepaintsItsSelector_WhenTheTreeRebuilds()
    {
        var main = new MainViewModel(_g.Tree, _g.Fx.Status);
        var tab = await _g.OpenTable(_g.A);
        main.Tabs.Add(tab);
        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _g.Tree.Rebuild();

        Assert.Contains(nameof(TabViewModel.GroupMembers), changed);
        Assert.Contains(nameof(TabViewModel.HasGroupSwitch), changed);
    }

    // ---- the selector control (headless) ----

    private static async Task Pump(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
    }

    [AvaloniaFact]
    public async Task Selector_IsHiddenForAConnectionWithoutGroup()
    {
        var tab = await _g.OpenTable(_g.Solo);
        var switcher = new GroupSwitcher { DataContext = tab };
        var window = new Window { Content = switcher };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(switcher.FindControl<StackPanel>("Root")!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Selector_ListsTheMembers_SwitchesTheTab_AndGoesBackWhenTheSwitchFails()
    {
        var tab = await _g.OpenTable(_g.A);
        var switcher = new GroupSwitcher { DataContext = tab };
        var window = new Window { Content = switcher };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var members = switcher.FindControl<ComboBox>("MemberBox")!;
        var groups = switcher.FindControl<ComboBox>("GroupBox")!;

        Assert.True(switcher.FindControl<StackPanel>("Root")!.IsVisible);
        Assert.True(groups.IsVisible);   // A is in two groups
        Assert.Equal(["A", "B", "Prod"], members.Items.OfType<ConnectionConfig>().Select(m => m.Name));
        Assert.Equal(_g.A.Id, ((ConnectionConfig)members.SelectedItem!).Id);

        members.SelectedItem = members.Items.OfType<ConnectionConfig>().Single(m => m.Name == "B");
        await Pump(() => tab.Connection.Id == _g.B.Id && !tab.IsSwitching && !tab.IsLoading, "switch to B");
        Assert.Equal(_g.B.Id, ((ConnectionConfig)members.SelectedItem!).Id);
        Assert.StartsWith("b", Label(tab));

        Assert.False(groups.IsVisible);   // B is in one group only

        // Another group: picking it changes the member list; an unreachable member leaves the selector on the real connection.
        var onA = await _g.OpenTable(_g.A);
        switcher.DataContext = onA;
        Dispatcher.UIThread.RunJobs();
        Assert.True(groups.IsVisible);
        groups.SelectedItem = groups.Items.OfType<ConnectionGroup>().Single(g => g.Name == "Broken");
        Assert.Equal("Broken", onA.ActiveGroup!.Name);
        Assert.Equal(["A", "NoBig", "Down"], members.Items.OfType<ConnectionConfig>().Select(m => m.Name));
        members.SelectedItem = members.Items.OfType<ConnectionConfig>().Single(m => m.Name == "Down");
        await Pump(() => onA.ErrorMessage is not null && !onA.IsSwitching, "failed switch");
        Assert.Equal(_g.A.Id, onA.Connection.Id);
        Assert.Equal(_g.A.Id, ((ConnectionConfig)members.SelectedItem!).Id);   // back on A
        window.Close();
    }

    // ---- shortcut ----

    [Fact]
    public async Task Shortcut_CtrlAltPageDown_SwitchesTheActiveTab_OtherChordsDoNot()
    {
        var main = new MainViewModel(_g.Tree, _g.Fx.Status);
        var tab = await _g.OpenTable(_g.A);
        main.Tabs.Add(tab);
        main.ActiveTab = tab;
        const KeyModifiers ctrlAlt = KeyModifiers.Control | KeyModifiers.Alt;

        // Modifiers compare for equality: Ctrl+PageDown (grid / editor / browser style tab switch) and extra modifiers are not ours.
        Assert.False(MainShortcuts.Handle(main, Key.PageDown, KeyModifiers.Control));
        Assert.False(MainShortcuts.Handle(main, Key.PageDown, KeyModifiers.None));
        Assert.False(MainShortcuts.Handle(main, Key.PageDown, ctrlAlt | KeyModifiers.Shift));
        Assert.False(MainShortcuts.Handle(main, Key.PageUp, KeyModifiers.Alt));
        Assert.Equal(_g.A.Id, tab.Connection.Id);

        Assert.True(MainShortcuts.Handle(main, Key.PageDown, ctrlAlt));
        await Eventually(() => tab.Connection.Id == _g.B.Id && !tab.IsSwitching && !tab.IsLoading, "switch to B");
        Assert.Equal(_g.B.Id, main.CurrentConnection!.Id);   // the status bar follows the tab

        Assert.True(MainShortcuts.Handle(main, Key.PageUp, ctrlAlt));
        await Eventually(() => tab.Connection.Id == _g.A.Id && !tab.IsSwitching && !tab.IsLoading, "back to A");
        Assert.True(MainShortcuts.Handle(main, Key.PageUp, ctrlAlt));
        await Eventually(() => tab.Connection.Id == _g.Prod.Id && !tab.IsSwitching && !tab.IsLoading, "wrap to Prod");
    }

    [Fact]
    public async Task Shortcut_InATabWithoutGroup_OnlySaysSo()
    {
        var main = new MainViewModel(_g.Tree, _g.Fx.Status);
        var tab = await _g.OpenTable(_g.Solo);
        main.Tabs.Add(tab);
        main.ActiveTab = tab;

        Assert.True(MainShortcuts.Handle(main, Key.PageDown, KeyModifiers.Control | KeyModifiers.Alt));

        await Eventually(() => _g.Fx.Status.Message.Contains("not in a group"), "status message");
        Assert.Equal(_g.Solo.Id, tab.Connection.Id);
    }
}
