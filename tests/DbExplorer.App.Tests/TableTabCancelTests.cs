using System.Diagnostics;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace DbExplorer.App.Tests;

/// <summary>
/// A save and a row count can take as long as the database wants (a lock, a slow trigger, a huge table): they run off the
/// calling thread and the status bar's Cancel stops them, also in the middle of a running SQLite statement. The slow work is
/// a cross join of the 500-row table (500 million rows, several seconds): long enough to cancel in the middle and to see
/// that the cancel cut it short, short enough that a test without the fix fails instead of hanging.
/// </summary>
public class TableTabCancelTests : IDisposable
{
    private const string Cross = "big a, big b, big c, (SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4)";

    /// <summary>A cancel must end the work well before the statement would have finished on its own.</summary>
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(1.5);

    private readonly TableTabFixture _t = new();
    private readonly SessionManager _sessions;

    public TableTabCancelTests()
    {
        using (var c = new SqliteConnection($"Data Source={_t.DbFile};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                CREATE TRIGGER slow_save AFTER UPDATE ON customers BEGIN SELECT count(*) FROM {Cross}; END;
                CREATE VIEW slow_v AS SELECT a.id FROM {Cross};
                """;
            cmd.ExecuteNonQuery();
        }
        // The plain driver: interrupting SQLite needs the real SqliteConnection, not the recording wrapper.
        _sessions = new SessionManager(_t.Fx.Store, _t.Fx.Cache, _t.Fx.Protector, _ => new SqliteDriver());
    }

    public void Dispose() => _t.Dispose();

    private async Task<TableTabViewModel> Open(string table)
    {
        var session = await _sessions.GetOrOpenAsync(_t.Config, CancellationToken.None);
        var hooks = new TableTabHooks(
            ct => _sessions.GetOrOpenAsync(_t.Config, ct).ContinueWith(t => (Session?)t.Result, ct),
            (_, _, _) => Task.CompletedTask,
            new FakeEditDialogs(),
            Status: _t.Fx.Status);
        var tab = new TableTabViewModel(session, new TableRef(null, table), hooks);
        await tab.LoadAsync();
        return tab;
    }

    /// <summary>Waits until the operation shows in the status bar, then a little more so the statement is running inside SQLite.</summary>
    private async Task WaitUntilRunning()
    {
        var watch = Stopwatch.StartNew();
        while (!_t.Fx.Status.CanCancel)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException();
            await Task.Delay(10);
        }
        await Task.Delay(300);
    }

    private string NameInFile(int id)
    {
        using var c = new SqliteConnection($"Data Source={_t.DbFile};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT name FROM customers WHERE id = {id}";
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task Save_RunsOffTheCallingThread_AndCancelRollsBackKeepingTheEdits()
    {
        var tab = await Open("customers");
        Assert.True(tab.SetCell(0, tab.FindColumn("name")!.Value, "Alicia"));

        var watch = Stopwatch.StartNew();
        var save = tab.SaveAsync();
        var returnedAfter = watch.Elapsed;

        Assert.False(save.IsCompleted, $"the save ran on the calling thread ({returnedAfter.TotalMilliseconds:N0} ms)");
        Assert.True(tab.IsSaving);
        await WaitUntilRunning();

        var cancelled = Stopwatch.StartNew();
        _t.Fx.Status.CancelBusy();
        await save.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(cancelled.Elapsed < Prompt, $"the save ended {cancelled.Elapsed.TotalMilliseconds:N0} ms after the cancel");

        Assert.False(tab.IsSaving);
        Assert.True(tab.HasPendingEdits);
        Assert.Equal("Alicia", tab.Rows[0].Values[tab.FindColumn("name")!.Value]);
        Assert.Contains("cancelled", tab.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Null(tab.ErrorMessage);
        Assert.Equal("Alice", NameInFile(1));   // rolled back
        Assert.False(_t.Fx.Status.IsBusy);
    }

    [Fact]
    public async Task Count_CanBeCancelledFromTheStatusBar()
    {
        var tab = await Open("slow_v");
        Assert.Null(tab.TotalCount);

        var count = tab.CountAsync();
        await WaitUntilRunning();
        var cancelled = Stopwatch.StartNew();
        _t.Fx.Status.CancelBusy();

        Assert.Equal(-1, await count.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(cancelled.Elapsed < Prompt, $"the count ended {cancelled.Elapsed.TotalMilliseconds:N0} ms after the cancel");
        Assert.Null(tab.TotalCount);
        Assert.Null(tab.ErrorMessage);   // a cancel is not a failure
        Assert.False(_t.Fx.Status.IsBusy);
    }

    [Fact]
    public async Task Count_IsCancelledWhenTheTabCloses()
    {
        var tab = await Open("slow_v");

        var count = tab.CountAsync();
        await WaitUntilRunning();
        var cancelled = Stopwatch.StartNew();
        tab.Close();

        Assert.Equal(-1, await count.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(cancelled.Elapsed < Prompt, $"the count ended {cancelled.Elapsed.TotalMilliseconds:N0} ms after the close");
        Assert.Null(tab.TotalCount);
    }

    [Fact]
    public async Task Count_IsCancelledByAReload()
    {
        var tab = await Open("slow_v");

        var count = tab.CountAsync();
        await WaitUntilRunning();
        await tab.ReloadAsync();

        Assert.Equal(-1, await count.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Null(tab.ErrorMessage);
    }
}
