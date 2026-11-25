using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;
using DbExplorer.Core.Plans;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.Tests;

public class HistoryRecordingTests : IDisposable
{
    private readonly ScriptTabFixture _t = new();
    private readonly string _file;
    private bool _record = true;

    public HistoryRecordingTests() => _file = Path.Combine(_t.Fx.Dir, "history.jsonl");

    public void Dispose() => _t.Dispose();

    private async Task<(ScriptTabViewModel Tab, QueryHistory History)> NewTab(string text, ConnectionConfig? config = null)
    {
        config ??= _t.Config;
        var history = new QueryHistory(_file);
        var session = await _t.Sessions.GetOrOpenAsync(config, CancellationToken.None);
        var hooks = new ScriptTabHooks(
            ct => _t.Sessions.GetOrOpenAsync(config, ct), _t.Files, _t.Store, _t.Fx.Status, History: history, RecordHistory: () => _record);
        var tab = _t.Track(new ScriptTabViewModel(config, session, hooks, 1) { Text = text });
        return (tab, history);
    }

    [Fact]
    public async Task EveryStatementThatRan_IsRecorded_WithRowsAndConnection()
    {
        var (tab, history) = await NewTab("SELECT * FROM t; UPDATE t SET name = 'z' WHERE id = 1;");

        await tab.RunScriptAsync();

        var entries = history.Query();
        Assert.Equal(2, entries.Count);
        var update = entries[0];
        Assert.Equal("UPDATE t SET name = 'z' WHERE id = 1", update.Sql);
        Assert.Equal(1, update.Rows);
        Assert.Null(update.Error);
        Assert.Equal(_t.Config.Id, update.ConnectionId);
        Assert.Equal(_t.Config.Name, update.ConnectionName);
        Assert.Equal(DbKind.Sqlite, update.Kind);
        Assert.Equal(PlanMode.None, update.Plan);
        Assert.Equal(3, entries[1].Rows);
        Assert.True(entries[1].At <= DateTime.UtcNow && entries[1].At > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task AFailingStatement_IsRecordedWithItsError_AndLaterOnesAreNot()
    {
        var (tab, history) = await NewTab("SELECT 1; SELECT * FROM nope; SELECT 3;");

        await tab.RunScriptAsync();

        var entries = history.Query();
        Assert.Equal(2, entries.Count);
        Assert.Contains("nope", entries[0].Error);
        Assert.Null(entries[1].Error);
    }

    [Fact]
    public async Task ARefusedWrite_OnAReadOnlyConnection_IsRecordedAsAnError()
    {
        using var ro = new ScriptTabFixture(readOnly: true);
        var history = new QueryHistory(Path.Combine(ro.Fx.Dir, "h.jsonl"));
        var session = await ro.Sessions.GetOrOpenAsync(ro.Config, CancellationToken.None);
        var hooks = new ScriptTabHooks(ct => ro.Sessions.GetOrOpenAsync(ro.Config, ct), null, null, ro.Fx.Status, History: history);
        var tab = ro.Track(new ScriptTabViewModel(ro.Config, session, hooks, 1) { Text = "DELETE FROM t" });

        await tab.RunScriptAsync();

        var entry = Assert.Single(history.Query());
        Assert.StartsWith("Refused:", entry.Error);
        Assert.Equal(3, ro.Scalar("SELECT count(*) FROM t"));
    }

    [Fact]
    public async Task SettingOff_RecordsNothing()
    {
        _record = false;
        var (tab, history) = await NewTab("SELECT 1");

        await tab.RunScriptAsync();

        Assert.Empty(history.Query());
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public async Task EstimatedPlan_IsRecordedWithItsPlanKind()
    {
        var (tab, history) = await NewTab("SELECT * FROM t");

        await tab.ShowEstimatedPlanAsync();

        Assert.Equal(PlanMode.Estimated, Assert.Single(history.Query()).Plan);
    }

    [Fact]
    public async Task AnUnwritableHistory_DoesNotBreakTheRun()
    {
        Directory.CreateDirectory(_file);   // a folder where the file should be
        var (tab, _) = await NewTab("SELECT 1");

        await tab.RunScriptAsync();

        Assert.Single(tab.Results);
        Assert.Null(tab.Results[0].Error);
    }
}

public class HistoryViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-histvm-" + Guid.NewGuid().ToString("N"));
    private readonly QueryHistory _history;
    private readonly FakeDialogs _dialogs = new();
    private readonly List<(HistoryEntry Entry, bool Run)> _opened = [];
    private readonly List<string> _copied = [];
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    public HistoryViewModelTests()
    {
        _history = new QueryHistory(Path.Combine(_dir, "h.jsonl"));
        Add("SELECT * FROM orders", A, "Shop", 120);
        Add("DELETE FROM orders", A, "Shop", 30, error: "Refused: read-only");
        Add("SELECT count(*) FROM customers", B, "Billing", 1);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private void Add(string sql, Guid conn, string name, int minutesAgo, string? error = null) =>
        _history.Append(new HistoryEntry(Guid.NewGuid(), Now.AddMinutes(-minutesAgo), conn, name, DbKind.Sqlite, "x.db", sql, 15, 2, error));

    private HistoryViewModel Vm(Guid? current = null, Func<Guid, bool>? exists = null) => new(
        _history, exists ?? (_ => true), current, _dialogs,
        (e, run) => { _opened.Add((e, run)); return Task.CompletedTask; },
        text => { _copied.Add(text); return Task.CompletedTask; },
        () => Now);

    [Fact]
    public async Task Load_ListsNewestFirst_AndSelectsTheFirst()
    {
        var vm = Vm();
        await vm.LoadAsync();

        Assert.Equal(["SELECT count(*) FROM customers", "DELETE FROM orders", "SELECT * FROM orders"], vm.Items.Select(i => i.Sql).ToArray());
        Assert.Same(vm.Items[0], vm.Selected);
        Assert.Equal("3 statements", vm.CountText);
        Assert.False(vm.IsEmpty);
        Assert.Equal("1 min ago", vm.Items[0].TimeAgo);
        Assert.Equal("2 h ago", vm.Items[2].TimeAgo);
        Assert.Equal("2 rows", vm.Items[0].RowsText);
    }

    [Fact]
    public async Task SearchText_FiltersLive_AndAnEmptyResultSaysNoMatch()
    {
        var vm = Vm();
        await vm.LoadAsync();

        vm.SearchText = "ORDERS";
        Assert.Equal(2, vm.Items.Count);
        vm.SearchText = "zzz";
        Assert.True(vm.IsEmpty);
        Assert.Equal("No match", vm.EmptyTitle);
        Assert.Null(vm.Selected);
        vm.SearchText = "";
        Assert.Equal(3, vm.Items.Count);
    }

    [Fact]
    public async Task Filters_ErrorsAndThisConnection()
    {
        var vm = Vm(current: B);
        await vm.LoadAsync();

        vm.ShowErrors = true;
        Assert.Equal("DELETE FROM orders", Assert.Single(vm.Items).Sql);
        Assert.True(vm.Items[0].HasError);

        vm.ShowThisConnection = true;
        Assert.Equal("SELECT count(*) FROM customers", Assert.Single(vm.Items).Sql);

        vm.ShowAll = true;
        Assert.Equal(3, vm.Items.Count);
        Assert.True(vm.CanFilterByConnection);
        Assert.False(Vm().CanFilterByConnection);
    }

    [Fact]
    public async Task Open_AndRunAgain_PassTheEntry_AndCloseTheWindow()
    {
        var vm = Vm();
        await vm.LoadAsync();
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;
        vm.Selected = vm.Items[1];

        await vm.OpenCommand.ExecuteAsync(null);
        await vm.RunAgainCommand.ExecuteAsync(null);

        Assert.Equal(["DELETE FROM orders", "DELETE FROM orders"], _opened.Select(o => o.Entry.Sql).ToArray());
        Assert.Equal([false, true], _opened.Select(o => o.Run).ToArray());
        Assert.Equal(2, closed);
    }

    [Fact]
    public async Task Open_Failure_ShowsAMessage_AndKeepsTheWindow()
    {
        var vm = new HistoryViewModel(_history, _ => true, null, _dialogs, (_, _) => throw new InvalidOperationException("no connection"), null, () => Now);
        await vm.LoadAsync();
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        await vm.OpenCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.Contains("no connection", vm.Message);
    }

    [Fact]
    public async Task CopySql_UsesTheClipboardDelegate()
    {
        var vm = Vm();
        await vm.LoadAsync();

        await vm.CopySqlCommand.ExecuteAsync(null);

        Assert.Equal(["SELECT count(*) FROM customers"], _copied);
    }

    [Fact]
    public async Task Delete_RemovesTheEntry_FromTheListAndTheFile_AndSelectsTheNeighbour()
    {
        var vm = Vm();
        await vm.LoadAsync();
        vm.Selected = vm.Items[1];

        vm.DeleteCommand.Execute(null);

        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("SELECT * FROM orders", vm.Selected!.Sql);
        Assert.Equal(2, new QueryHistory(Path.Combine(_dir, "h.jsonl")).Query().Count);
    }

    [Fact]
    public async Task ClearAll_AsksFirst_ThenEmptiesTheHistory()
    {
        var vm = Vm();
        await vm.LoadAsync();

        _dialogs.ConfirmResult = false;
        await vm.ClearAllCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Items.Count);

        _dialogs.ConfirmResult = true;
        await vm.ClearAllCommand.ExecuteAsync(null);
        Assert.Empty(vm.Items);
        Assert.True(vm.IsEmpty);
        Assert.Equal("Nothing recorded yet", vm.EmptyTitle);
        Assert.Empty(new QueryHistory(Path.Combine(_dir, "h.jsonl")).Query());
    }

    [Fact]
    public async Task Commands_AreDisabled_WithoutASelection()
    {
        var vm = Vm();
        vm.SearchText = "zzz";
        await vm.LoadAsync();

        Assert.False(vm.OpenCommand.CanExecute(null));
        Assert.False(vm.DeleteCommand.CanExecute(null));
    }

    [Fact]
    public void Preview_IsTheFirstTwoNonEmptyLines()
    {
        var e = new HistoryEntry(Guid.NewGuid(), Now, A, "Shop", DbKind.Sqlite, "x.db", "\n  SELECT a,\n\n   b\nFROM t\nWHERE 1=1", 5, null, null);
        var item = new HistoryItemViewModel(e, Now, true);

        Assert.Equal("SELECT a,\nb", item.Preview);
        Assert.False(item.HasRows);
    }

    [Theory]
    [InlineData(10, "just now")]
    [InlineData(90, "2 min ago")]
    [InlineData(7200, "2 h ago")]
    [InlineData(100_000, "yesterday")]
    [InlineData(400_000, "4 days ago")]
    public void TimeAgo_ReadsNaturally(int seconds, string expected) =>
        Assert.Equal(expected, HistoryTime.Ago(Now.AddSeconds(-seconds), Now));
}

public class HistoryOptionsTests
{
    [Fact]
    public void RecordHistory_FlowsIntoTheResult()
    {
        var vm = new OptionsViewModel(null, _ => null, null, recordHistory: true) { RecordHistory = false };

        Assert.True(vm.TryConfirm());
        Assert.False(vm.Result!.RecordHistory);
    }

    [Fact]
    public void ClearHistory_NeedsASecondClick()
    {
        var cleared = 0;
        var vm = new OptionsViewModel(null, _ => null, null, true, () => cleared++);

        vm.ClearHistoryCommand.Execute(null);
        Assert.Equal(0, cleared);
        Assert.Contains("again", vm.ClearHistoryText);

        vm.ClearHistoryCommand.Execute(null);
        Assert.Equal(1, cleared);
        Assert.Equal("History cleared.", vm.ClearHistoryInfo);
    }

    [Fact]
    public void WithoutAHistory_TheButtonIsHidden() =>
        Assert.False(new OptionsViewModel(null, _ => null).CanClearHistory);
}

public class HistoryConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-histcfg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void RecordHistory_DefaultsOn_AndIsKeptThroughSaveAndLoad()
    {
        var store = new DbExplorer.Core.Config.ConfigStore(Path.Combine(_dir, "c.json"), new PlainProtector());
        Assert.True(store.Load().RecordHistory);

        var config = AppConfig.Empty();
        config.RecordHistory = false;
        store.Save(config);

        Assert.False(store.Load().RecordHistory);
    }
}
