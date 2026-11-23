using DbExplorer.App.ViewModels;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.Tests;

/// <summary>A script tab keeps one connection between runs (BEGIN in one Ctrl+Enter, ROLLBACK in another).</summary>
public class ScriptTabConnectionTests : IDisposable
{
    private readonly ScriptTabFixture _t = new();

    public void Dispose() => _t.Dispose();

    private static async Task RunAsync(ScriptTabViewModel tab, string sql)
    {
        tab.Text = sql;
        tab.CaretOffset = 0;
        await tab.RunStatementAsync();
    }

    [Fact]
    public async Task ManualTransaction_SpansRuns_AndRollbackUndoesIt()
    {
        var tab = await _t.NewTab();

        await RunAsync(tab, "BEGIN");
        await RunAsync(tab, "INSERT INTO t VALUES (10, 'tx')");
        await RunAsync(tab, "ROLLBACK");

        Assert.Null(tab.ErrorMessage);
        Assert.False(tab.Results.Single().HasError);
        Assert.Equal(0, _t.Scalar("SELECT COUNT(*) FROM t WHERE id = 10"));
    }

    [Fact]
    public async Task ManualTransaction_Commit_Persists_AndTempTablesSurvive()
    {
        var tab = await _t.NewTab();

        await RunAsync(tab, "CREATE TEMP TABLE scratch (v INTEGER)");
        await RunAsync(tab, "BEGIN");
        await RunAsync(tab, "INSERT INTO scratch VALUES (5)");
        await RunAsync(tab, "INSERT INTO t VALUES (11, 'kept')");
        await RunAsync(tab, "COMMIT");
        await RunAsync(tab, "SELECT v FROM scratch");

        Assert.Equal(5L, tab.Results.Single().Sets[0].Rows[0][0]);
        Assert.Equal(1, _t.Scalar("SELECT COUNT(*) FROM t WHERE id = 11"));
    }

    [Fact]
    public async Task ClosingTheTab_RollsBackAnOpenTransaction_AndFreesTheDatabase()
    {
        var tab = await _t.NewTab();
        await RunAsync(tab, "BEGIN");
        await RunAsync(tab, "INSERT INTO t VALUES (12, 'lost')");

        tab.Close();

        Assert.Equal(0, _t.Scalar("SELECT COUNT(*) FROM t WHERE id = 12"));
        // Nobody holds the write lock any more.
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_t.DbFile};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO t VALUES (13, 'free')";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Disconnect_ReleasesTheConnection_AndTheNextRunReportsClosed()
    {
        var tab = await _t.NewTab();
        await RunAsync(tab, "BEGIN");
        await RunAsync(tab, "INSERT INTO t VALUES (14, 'lost')");

        _t.Sessions.Close(_t.Config.Id);   // Disconnect

        Assert.Equal(0, _t.Scalar("SELECT COUNT(*) FROM t WHERE id = 14"));
        await RunAsync(tab, "SELECT 1");
        Assert.True(tab.NeedsReconnect);
    }

    [Fact]
    public async Task RunningAgainRightAfterACancel_WaitsForTheStatementToStop_InsteadOfFailing()
    {
        for (var attempt = 0; attempt < 5; attempt++)   // timing dependent: the cancelled producer may still hold the connection
        {
            var tab = await _t.NewTab(ScriptTabFixture.Endless);
            await RunAsync(tab, "CREATE TEMP TABLE IF NOT EXISTS scratch (v INTEGER)");
            tab.Text = ScriptTabFixture.Endless;
            var slow = tab.RunScriptAsync();
            await Task.Delay(100);

            tab.Cancel();
            await slow;
            await RunAsync(tab, "SELECT COUNT(*) FROM scratch");

            Assert.Null(tab.ErrorMessage);
            Assert.False(_t.Status.IsError, _t.Status.Message);
            Assert.Equal(0L, tab.Results.Single().Sets[0].Rows[0][0]);   // and it is the same connection: the temp table is there
        }
    }

    [Fact]
    public async Task ClosingTheTabRightAfterStartingARun_ReportsNothing()
    {
        var tab = await _t.NewTab(ScriptTabFixture.Endless);
        var run = tab.RunScriptAsync();

        tab.Close();
        await run;

        Assert.False(tab.NeedsReconnect);
        Assert.False(_t.Status.IsError);
    }

    [Fact]
    public async Task NothingIsOpenedUntilTheFirstRun()
    {
        var tab = await _t.NewTab(withSession: false);

        Assert.Equal(0, _t.Resolved);
        Assert.False(tab.IsRunning);
    }
}

/// <summary>
/// A result cut at the row limit cancels its command; on PostgreSQL that can abort the user's open transaction. The run says so
/// itself (probe at the end of the run), instead of the next run silently reconnecting and a later COMMIT looking successful.
/// </summary>
public class ScriptTabAbortTests : IDisposable
{
    private readonly ScriptTabFixture _t = new(recording: true);

    public void Dispose() => _t.Dispose();

    private static async Task RunAsync(ScriptTabViewModel tab, string sql)
    {
        tab.Text = sql;
        tab.CaretOffset = 0;
        await tab.RunStatementAsync();
    }

    [Fact]
    public async Task TruncatedResult_WhoseProbeFails_IsReportedByThatRun_AndTheNextRunWorks()
    {
        var tab = await _t.NewTab();
        tab.RowLimit = 1;
        _t.Log.FailProbe = true;   // the aborted-transaction state after the cancel

        await RunAsync(tab, "SELECT * FROM t");

        Assert.Equal(ScriptConnection.ResetMessage, tab.ErrorMessage);
        Assert.True(_t.Status.IsError);
        Assert.Equal(ScriptConnection.ResetMessage, _t.Status.Message);
        Assert.Contains("aborted and rolled back", tab.ErrorMessage);

        _t.Log.FailProbe = false;
        await RunAsync(tab, "SELECT COUNT(*) FROM t");

        Assert.Null(tab.ErrorMessage);
        Assert.False(_t.Status.IsError);
        Assert.DoesNotContain("reopened", _t.Status.Message);   // the loud message was the one report: no second note
        Assert.Equal(3L, tab.Results.Single().Sets[0].Rows[0][0]);
    }

    [Fact]
    public async Task TruncatedResult_WithAHealthyConnection_IsUnchanged_AndKeepsTheSession()
    {
        var tab = await _t.NewTab();
        tab.RowLimit = 1;
        await RunAsync(tab, "CREATE TEMP TABLE scratch (v INTEGER)");

        await RunAsync(tab, "SELECT * FROM t");

        Assert.Null(tab.ErrorMessage);
        Assert.False(_t.Status.IsError);
        Assert.True(tab.Results.Single().IsTruncated);
        await RunAsync(tab, "SELECT COUNT(*) FROM scratch");   // same connection: the temp table survived
        Assert.Null(tab.ErrorMessage);
    }

    [Fact]
    public async Task ClosingTheTabWhileARunResolvesItsSession_NeverCreatesAConnection()
    {
        var gate = new TaskCompletionSource();
        var session = await _t.Sessions.GetOrOpenAsync(_t.Config, CancellationToken.None);
        var hooks = new ScriptTabHooks(async ct => { await gate.Task; return session; }, _t.Files, _t.Store, _t.Status);
        var tab = _t.Track(new ScriptTabViewModel(_t.Config, null, hooks, 1) { Text = "SELECT 1" });
        var run = tab.RunScriptAsync();

        tab.Close();
        gate.SetResult();
        await run;

        var dedicated = (System.Collections.ICollection)typeof(Session)
            .GetField("_dedicated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(session)!;
        Assert.Empty(dedicated);   // no ScriptConnection was created (and left registered) after the close
        Assert.Empty(tab.Results);
        Assert.False(_t.Status.IsError);
    }
}

public class ScriptTabBlankLineTests : IDisposable
{
    private readonly ScriptTabFixture _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task CtrlEnter_WithoutSelection_RunsOnlyTheBlankLineDelimitedStatement()
    {
        var tab = await _t.NewTab("update t set name = 'first' where id = 1\n\nupdate t set name = 'second' where id = 3");
        tab.CaretOffset = 5;

        await tab.RunStatementAsync();

        Assert.Single(tab.Results);
        Assert.Equal(1, _t.Scalar("SELECT COUNT(*) FROM t WHERE name = 'first'"));
        Assert.Equal(0, _t.Scalar("SELECT COUNT(*) FROM t WHERE name = 'second'"));
    }

    [Fact]
    public async Task CtrlEnter_WithASelection_RunsItAsSelected_AcrossBlankLines()
    {
        var text = "update t set name = 'first' where id = 1\n\nupdate t set name = 'second' where id = 3";
        var tab = await _t.NewTab(text);
        tab.Selection = (0, text.Length);

        await tab.RunStatementAsync();

        // One batch, as selected (the splitter only knows ';' and GO): on SQLite two statements without ';' are a syntax error.
        Assert.Equal(0, _t.Scalar("SELECT COUNT(*) FROM t WHERE name IN ('first', 'second')"));
    }

    [Fact]
    public async Task F5_RunsTheWholeScript()
    {
        var tab = await _t.NewTab("update t set name = 'first' where id = 1;\n\nupdate t set name = 'second' where id = 3;");

        await tab.RunScriptAsync();

        Assert.Equal(2, tab.Results.Count);
        Assert.Equal(2, _t.Scalar("SELECT COUNT(*) FROM t WHERE name IN ('first', 'second')"));
    }
}
