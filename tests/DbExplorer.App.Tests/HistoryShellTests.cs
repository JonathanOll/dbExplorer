using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.Tests;

public class HistoryShellTests : ShellTestBase
{
    private sealed class FakeHistoryDialogs : IHistoryDialogs
    {
        public HistoryViewModel? Shown { get; private set; }

        public Task ShowHistoryAsync(HistoryViewModel viewModel)
        {
            Shown = viewModel;
            return Task.CompletedTask;
        }
    }

    private static HistoryEntry Entry(Guid connection, string sql = "SELECT 1") =>
        new(Guid.NewGuid(), DateTime.UtcNow, connection, "Old", DbKind.Sqlite, "x.db", sql, 5, 1, null);

    private (MainViewModel Main, QueryHistory History, FakeHistoryDialogs Dialogs) NewShell()
    {
        var main = NewMain();
        var history = new QueryHistory(Path.Combine(T.Fx.Dir, "history.jsonl"));
        var dialogs = new FakeHistoryDialogs();
        main.UseHistory(history, dialogs, T.Fx.Dialogs);
        return (main, history, dialogs);
    }

    [Fact]
    public async Task OpenEntry_OpensANewScriptTabOnItsConnection_WithTheSql()
    {
        var (main, _, _) = NewShell();

        await main.OpenHistoryEntryAsync(Entry(T.Config.Id, "SELECT 42"), run: false);

        var tab = Assert.IsType<ScriptTabViewModel>(main.ActiveTab);
        Assert.Equal("SELECT 42", tab.Text);
        Assert.Equal(T.Config.Id, tab.Connection.Id);
    }

    [Fact]
    public async Task OpenEntry_OfADeletedConnection_UsesTheActiveOne()
    {
        var (main, _, _) = NewShell();

        await main.OpenHistoryEntryAsync(Entry(Guid.NewGuid()), run: false);

        Assert.Equal(T.Config.Id, main.ActiveTab!.Connection.Id);
    }

    [Fact]
    public async Task RunAgain_RunsTheStatementInTheNewTab()
    {
        var (main, _, _) = NewShell();

        await main.OpenHistoryEntryAsync(Entry(T.Config.Id, "SELECT 7"), run: true);

        var tab = (ScriptTabViewModel)main.ActiveTab!;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (tab.Results.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(7L, tab.Results[0].Sets[0].Rows[0][0]);
    }

    [Fact]
    public async Task OpenHistory_ShowsTheWindow_WithTheActiveConnection()
    {
        var (main, history, dialogs) = NewShell();
        history.Append(Entry(T.Config.Id));

        await main.OpenHistoryAsync();

        Assert.NotNull(dialogs.Shown);
        Assert.True(dialogs.Shown!.CanFilterByConnection);
        await dialogs.Shown.LoadAsync();
        Assert.Single(dialogs.Shown.Items);
    }

    [Fact]
    public void CtrlShiftH_OpensTheHistory()
    {
        var (main, _, dialogs) = NewShell();

        Assert.True(Views.MainShortcuts.Handle(main, Avalonia.Input.Key.H, Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Shift));
        Assert.NotNull(dialogs.Shown);
    }
}
