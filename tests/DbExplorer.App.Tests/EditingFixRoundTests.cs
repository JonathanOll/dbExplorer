using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace DbExplorer.App.Tests;

/// <summary>Fix round 1: reload with pending edits after a schema change, and the generic window-close confirmation.</summary>
public class EditingFixRoundTests : IDisposable
{
    private readonly TableTabFixture _t = new();
    private readonly FakeEditDialogs _dialogs = new();
    private ConnectionTreeViewModel? _tree;

    public void Dispose() => _t.Dispose();

    private async Task<TableTabViewModel> Open(string table)
    {
        var config = new AppConfig([], [_t.Config], new());
        var tree = _tree = _t.Fx.CreateTree(config);
        var session = await _t.OpenSession();
        var hooks = new TableTabHooks(
            ct => _t.Sessions.GetOrOpenAsync(_t.Config, ct).ContinueWith(x => (Session?)x.Result, ct),
            (_, _, _) => Task.CompletedTask, _dialogs, () => tree.Config, tree.SetVirtualKey);
        var tab = new TableTabViewModel(session, new TableRef(null, table), hooks);
        await tab.LoadAsync();
        return tab;
    }

    private MainViewModel Main(params TabViewModel[] tabs)
    {
        var main = new MainViewModel(_tree!, _t.Fx.Status);
        foreach (var tab in tabs) main.Tabs.Add(tab);
        return main;
    }

    private void Exec(string sql)
    {
        using var c = new SqliteConnection($"Data Source={_t.DbFile};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private string? NoteOfOrder10()
    {
        using var c = new SqliteConnection($"Data Source={_t.DbFile};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT note FROM orders WHERE id = 10";
        return cmd.ExecuteScalar() as string;
    }

    // ---- reload with pending edits after a schema change ----

    [Theory]
    [InlineData("ALTER TABLE orders DROP COLUMN paid", 6)]            // a column in the middle disappears: indexes shift
    [InlineData("ALTER TABLE orders ADD COLUMN extra TEXT", 8)]       // a column is appended: rows are shorter than the schema
    public async Task Reload_WithPendingEdits_AfterSchemaChange_CancelChangesNothing_SaveIsRefused_DiscardReloads(string alter, int newColumnCount)
    {
        var tab = await Open("orders");
        var note = tab.FindColumn("note")!.Value;
        tab.SetCell(0, note, "edited");
        var namesBefore = tab.Columns.Select(c => c.Name).ToArray();
        var valuesBefore = tab.Rows[0].Values.ToArray();
        Exec(alter);
        _t.Sessions.Close(_t.Config.Id);   // the next resolve reopens the connection and reads the new schema

        // Cancel: nothing changes at all (columns, rows, edits, no error)
        _dialogs.Choice = SaveChoice.Cancel;
        await tab.ReloadAsync();
        Assert.Equal(1, _dialogs.AskCount);
        Assert.Equal(namesBefore, tab.Columns.Select(c => c.Name));
        Assert.Equal(namesBefore.Length, tab.GridColumns.Count);
        Assert.Equal(valuesBefore, tab.Rows[0].Values);
        Assert.True(tab.HasPendingEdits);
        Assert.Null(tab.ErrorMessage);

        // Save: refused with a clear message, no preview, nothing written, edits kept, columns untouched
        _dialogs.Choice = SaveChoice.Save;
        await tab.ReloadAsync();
        Assert.Equal(2, _dialogs.AskCount);
        Assert.Empty(_dialogs.Previews);
        Assert.Contains("columns changed", tab.ErrorMessage);
        Assert.Contains("Nothing was saved", tab.ErrorMessage);
        Assert.True(tab.HasPendingEdits);
        Assert.Equal(namesBefore, tab.Columns.Select(c => c.Name));
        Assert.Equal("first", NoteOfOrder10());
        // a direct save never pairs the old values with the new column list either
        await tab.SaveAsync();
        Assert.Equal("first", NoteOfOrder10());

        // Discard: edits dropped, reload works against the new schema
        _dialogs.Choice = SaveChoice.Discard;
        await tab.ReloadAsync();
        Assert.False(tab.HasPendingEdits);
        Assert.Null(tab.ErrorMessage);
        Assert.Equal(newColumnCount, tab.Columns.Count);
        Assert.Equal(3, tab.Rows.Count);
        Assert.Equal("first", tab.Rows[0].Values[tab.FindColumn("note")!.Value]);
    }

    // ---- closing the window: generic confirmation over every tab ----

    private sealed class VetoTab(ConnectionConfig connection, bool allow) : TabViewModel(connection)
    {
        public int Asked { get; private set; }
        public override string Title => "veto";
        public override Task<bool> ConfirmCloseAsync()
        {
            Asked++;
            return Task.FromResult(allow);
        }
    }

    [Fact]
    public async Task ConfirmExit_NothingPending_AnswersSynchronously_WithoutPrompt()
    {
        var tab = await Open("customers");
        var asking = Main(tab).ConfirmExitAsync();
        Assert.True(asking.IsCompletedSuccessfully);   // lets the window close without being cancelled and re-closed
        Assert.True(await asking);
        Assert.Equal(0, _dialogs.AskCount);
    }

    [Fact]
    public async Task ConfirmExit_Cancel_AbortsAtTheFirstTab()
    {
        var a = await Open("customers");
        var b = await Open("big");
        a.SetCell(0, 1, "x");
        b.SetCell(0, 1, "y");
        _dialogs.Choice = SaveChoice.Cancel;

        Assert.False(await Main(a, b).ConfirmExitAsync());

        Assert.Equal(1, _dialogs.AskCount);
        Assert.True(a.HasPendingEdits);
        Assert.True(b.HasPendingEdits);
    }

    [Fact]
    public async Task ConfirmExit_Discard_AsksEveryTabWithPendingEdits_Sequentially()
    {
        var a = await Open("customers");
        var b = await Open("big");
        var clean = await Open("orders");
        a.SetCell(0, 1, "x");
        b.SetCell(0, 1, "y");
        _dialogs.Choice = SaveChoice.Discard;

        Assert.True(await Main(a, clean, b).ConfirmExitAsync());

        Assert.Equal(2, _dialogs.AskCount);
        Assert.False(a.HasPendingEdits);
        Assert.False(b.HasPendingEdits);
    }

    [Fact]
    public async Task ConfirmExit_Save_GoesThroughPreview_AndPreviewCancelAbortsTheClose()
    {
        var a = await Open("customers");
        a.SetCell(0, 1, "Alicia");
        _dialogs.Choice = SaveChoice.Save;

        _dialogs.PreviewResult = false;
        Assert.False(await Main(a).ConfirmExitAsync());
        Assert.True(a.HasPendingEdits);

        _dialogs.PreviewResult = true;
        Assert.True(await Main(a).ConfirmExitAsync());
        Assert.False(a.HasPendingEdits);
        Assert.Equal(1, _t.Scalar("SELECT COUNT(*) FROM customers WHERE name = 'Alicia'"));
    }

    [Fact]
    public async Task ConfirmExit_WhenADialogThrows_KeepsTheWindowOpen_ReportsAndRecovers()
    {
        var a = await Open("customers");
        a.SetCell(0, 1, "x");
        var main = Main(a);
        _dialogs.ThrowOnAsk = true;

        Assert.False(await main.ConfirmExitAsync());   // no exception escapes
        Assert.True(_t.Fx.Status.IsError);
        Assert.Contains("Cannot close", _t.Fx.Status.Message);
        Assert.True(a.HasPendingEdits);

        _dialogs.ThrowOnAsk = false;
        _dialogs.Choice = SaveChoice.Discard;
        Assert.True(await main.ConfirmExitAsync());   // not stuck in "confirming"
    }

    [Fact]
    public async Task ConfirmExit_IsGeneric_AnyTabCanVeto()
    {
        var table = await Open("customers");
        var allow = new VetoTab(_t.Config, allow: true);
        var veto = new VetoTab(_t.Config, allow: false);

        Assert.True(await Main(table, allow).ConfirmExitAsync());
        Assert.Equal(1, allow.Asked);
        Assert.False(await Main(table, allow, veto).ConfirmExitAsync());
        Assert.Equal(1, veto.Asked);
    }

    [Fact]
    public async Task ConfirmExit_WhilePromptsAreShowing_SecondRequestIsRefused()
    {
        var a = await Open("customers");
        a.SetCell(0, 1, "x");
        var main = Main(a);
        _dialogs.AskGate = new TaskCompletionSource();
        _dialogs.Choice = SaveChoice.Discard;

        var first = main.ConfirmExitAsync();
        Assert.False(first.IsCompleted);
        Assert.False(await main.ConfirmExitAsync());   // a second X click does not stack prompts
        Assert.Equal(1, _dialogs.AskCount);

        _dialogs.AskGate.SetResult();
        Assert.True(await first);
    }

    private static async Task PumpUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task MainWindow_Close_WithPendingEdits_PromptsFirst_ThenClosesOnlyWhenResolved()
    {
        var tab = await Open("customers");
        tab.SetCell(0, 1, "x");
        var main = Main(tab);
        var window = new MainWindow { DataContext = main };
        window.Show();
        _dialogs.AskGate = new TaskCompletionSource();

        _dialogs.Choice = SaveChoice.Cancel;
        window.Close();
        Assert.True(window.IsVisible);                 // close was cancelled while the prompt is open
        _dialogs.AskGate.SetResult();
        await PumpUntil(() => _dialogs.AskCount > 0);
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);                 // user answered Cancel: stays open
        Assert.True(tab.HasPendingEdits);

        _dialogs.AskGate = new TaskCompletionSource();
        _dialogs.Choice = SaveChoice.Discard;
        window.Close();
        Assert.True(window.IsVisible);
        _dialogs.AskGate.SetResult();
        await PumpUntil(() => !window.IsVisible);
        Assert.False(window.IsVisible);                // Discard: the window really closed
        Assert.False(tab.HasPendingEdits);
    }

    [AvaloniaFact]
    public async Task MainWindow_Close_WithNothingPending_JustCloses()
    {
        var tab = await Open("customers");
        var window = new MainWindow { DataContext = Main(tab) };
        window.Show();
        window.Close();
        Assert.False(window.IsVisible);
        Assert.Equal(0, _dialogs.AskCount);
    }
}
