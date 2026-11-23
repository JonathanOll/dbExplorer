using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace DbExplorer.App.Tests;

internal sealed class FakeScriptFiles : IScriptFiles
{
    public string? SavePath { get; set; }
    public string? OpenPath { get; set; }
    public List<string> SuggestedNames { get; } = [];

    public Task<string?> PickSaveAsync(string suggestedName)
    {
        SuggestedNames.Add(suggestedName);
        return Task.FromResult(SavePath);
    }

    public Task<string?> PickOpenAsync() => Task.FromResult(OpenPath);
}

/// <summary>A temp SQLite database (table t with 3 rows), a real session manager and the pieces a script tab needs.</summary>
internal sealed class ScriptTabFixture : IDisposable
{
    public const string Endless = "WITH RECURSIVE c(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM c) SELECT n FROM c WHERE n < 0";

    public TreeFixture Fx { get; } = new();
    public SessionManager Sessions { get; }
    public ConnectionConfig Config { get; }
    public FakeScriptFiles Files { get; } = new();
    public ScriptSessionStore Store { get; }
    public string DbFile { get; }
    public StatusModel Status => Fx.Status;
    public int Resolved { get; private set; }

    public SqlLog Log { get; } = new();

    public ScriptTabFixture(bool readOnly = false, bool recording = false)
    {
        DbFile = Path.Combine(Fx.Dir, "data.db");
        using (var c = new SqliteConnection($"Data Source={DbFile};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT);
                INSERT INTO t VALUES (1, 'alpha'), (2, NULL), (3, 'gamma');
                CREATE TABLE big (id INTEGER PRIMARY KEY);
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 50)
                INSERT INTO big SELECT i FROM n;
                """;
            cmd.ExecuteNonQuery();
        }

        Config = TreeFixture.Conn("Lite", database: DbFile) with { ReadOnly = readOnly };
        // The recording driver wraps the commands, which hides the SQLite connection from the runner's interrupt-on-cancel:
        // only tests that need to observe or fail the health probe use it (and never cancel a running statement).
        Sessions = recording
            ? new SessionManager(Fx.Store, Fx.Cache, Fx.Protector, _ => new RecordingDriver(new DbExplorer.Core.Drivers.SqliteDriver(), Log))
            : new SessionManager(Fx.Store, Fx.Cache, Fx.Protector);
        Store = new ScriptSessionStore(Path.Combine(Fx.Dir, "scripts"));
    }

    private readonly List<ScriptTabViewModel> _tabs = [];

    public ScriptTabViewModel Track(ScriptTabViewModel tab)
    {
        _tabs.Add(tab);
        return tab;
    }

    public string TempFile(string name) => Path.Combine(Fx.Dir, name);

    public ScriptTabHooks Hooks => new(
        async ct =>
        {
            Resolved++;
            return await Sessions.GetOrOpenAsync(Config, ct);
        },
        Files, Store, Fx.Status);

    public async Task<ScriptTabViewModel> NewTab(string text = "", bool withSession = true)
    {
        var session = withSession ? await Sessions.GetOrOpenAsync(Config, CancellationToken.None) : null;
        return Track(new ScriptTabViewModel(Config, session, Hooks, 1) { PersistDelay = TimeSpan.FromMilliseconds(40), Text = text });
    }

    public long Scalar(string sql)
    {
        using var c = new SqliteConnection($"Data Source={DbFile};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Dispose()
    {
        foreach (var tab in _tabs) tab.Close();   // no autosave timer may outlive the temp folder
        Sessions.Close(Config.Id);
        Fx.Dispose();
    }
}

public class ScriptTabViewModelTests : IDisposable
{
    private readonly ScriptTabFixture _t = new();

    public void Dispose() => _t.Dispose();

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(10);
        }
    }

    private static object? Cell(StatementResultViewModel r, int row = 0, int col = 0) => r.Sets[0].Rows[row][col];

    // ---- running ----

    [Fact]
    public async Task RunStatement_UsesStatementUnderCaret()
    {
        var tab = await _t.NewTab("SELECT 1 AS a;\nSELECT 2 AS b;\nSELECT 3 AS c;");
        tab.CaretOffset = tab.Text.IndexOf("SELECT 2", StringComparison.Ordinal) + 4;

        await tab.RunStatementAsync();

        var result = Assert.Single(tab.Results);
        Assert.Equal("b", result.Sets[0].Columns[0].Name);
        Assert.Equal(2L, Cell(result));
    }

    [Fact]
    public async Task RunStatement_UsesSelection()
    {
        var tab = await _t.NewTab("SELECT 1 AS a;\nSELECT 2 AS b; SELECT 3 AS c;");
        var start = tab.Text.IndexOf("SELECT 2", StringComparison.Ordinal);
        tab.CaretOffset = 0;
        tab.Selection = (start, tab.Text.Length);   // two statements selected: both run, nothing else

        await tab.RunStatementAsync();

        Assert.Equal(["b", "c"], tab.Results.Select(r => r.Sets[0].Columns[0].Name).ToArray());
    }

    [Fact]
    public async Task RunStatement_EmptySelection_FallsBackToCaret()
    {
        var tab = await _t.NewTab("SELECT 1 AS a; SELECT 2 AS b");
        tab.CaretOffset = 3;
        tab.Selection = (5, 5);

        await tab.RunStatementAsync();

        Assert.Equal("a", Assert.Single(tab.Results).Sets[0].Columns[0].Name);
    }

    [Fact]
    public async Task RunScript_RunsAllAndCollectsResults()
    {
        var tab = await _t.NewTab("SELECT id FROM t ORDER BY id; UPDATE t SET name = 'x' WHERE id = 2; SELECT name FROM t WHERE id = 2");

        await tab.RunScriptAsync();

        Assert.Equal(3, tab.Results.Count);
        Assert.Matches(@"^3 rows · \d+ ms$", tab.Results[0].StatusText);
        Assert.Equal("1 row affected", tab.Results[1].StatusText);
        Assert.Equal("x", Cell(tab.Results[2]));
        Assert.All(tab.Results, r => Assert.Null(r.Error));
        Assert.False(tab.IsRunning);
    }

    [Fact]
    public async Task RunScript_StopsAtFirstError_ShowsMessage()
    {
        var tab = await _t.NewTab("SELECT 1; SELECT * FROM no_such_table; SELECT 3");

        await tab.RunScriptAsync();

        Assert.Equal(2, tab.Results.Count);
        Assert.Null(tab.Results[0].Error);
        Assert.Contains("no_such_table", tab.Results[1].Error);
        Assert.True(tab.Results[1].HasError);
        Assert.True(_t.Status.IsError);
        Assert.Contains("no_such_table", _t.Status.Message);
        Assert.False(tab.IsRunning);
    }

    [Fact]
    public async Task RunScript_NothingToRun_SaysSo()
    {
        var tab = await _t.NewTab("  -- only a comment\n");

        await tab.RunScriptAsync();

        Assert.Empty(tab.Results);
        Assert.Contains("Nothing to run", _t.Status.Message);
    }

    [Fact]
    public async Task ReadOnlyConnection_RefusesWrite()
    {
        using var ro = new ScriptTabFixture(readOnly: true);
        var tab = await ro.NewTab("SELECT 1; DELETE FROM t; SELECT 3");

        await tab.RunScriptAsync();

        Assert.Equal(2, tab.Results.Count);   // the script stopped at the refused write
        Assert.Contains("read-only", tab.Results[1].Error);
        Assert.Equal(3, ro.Scalar("SELECT count(*) FROM t"));
    }

    [Fact]
    public async Task ReadOnlyConnection_FollowsTheLiveFlagOfTheSession()
    {
        var tab = await _t.NewTab("DELETE FROM t WHERE id = 1");
        var session = await _t.Sessions.GetOrOpenAsync(_t.Config, CancellationToken.None);

        session.UpdateConfig(_t.Config with { ReadOnly = true });
        await tab.RunScriptAsync();
        Assert.Contains("read-only", Assert.Single(tab.Results).Error);

        session.UpdateConfig(_t.Config with { ReadOnly = false });
        await tab.RunScriptAsync();
        Assert.Null(Assert.Single(tab.Results).Error);
        Assert.Equal(2, _t.Scalar("SELECT count(*) FROM t"));
    }

    [Fact]
    public async Task Cancel_StopsRunning_IsRunningFalse()
    {
        var tab = await _t.NewTab(ScriptTabFixture.Endless + "; SELECT 2");

        var run = tab.RunScriptAsync();
        Assert.True(tab.IsRunning);
        await Task.Delay(150);
        tab.Cancel();

        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20))));
        await run;   // must not throw
        Assert.False(tab.IsRunning);
        Assert.Empty(tab.Results);
        Assert.Null(tab.ErrorMessage);
        Assert.Contains("cancel", _t.Status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(_t.Status.IsError);
    }

    [Fact]
    public async Task Cancel_WhenNothingRuns_DoesNothing()
    {
        var tab = await _t.NewTab("SELECT 1");

        tab.Cancel();

        Assert.False(tab.IsRunning);
        await tab.RunScriptAsync();
        Assert.Single(tab.Results);
    }

    [Fact]
    public async Task Run_DoesNotBlockUiThread()
    {
        // Microsoft.Data.Sqlite executes synchronously: if the query ran on the calling thread this call would never
        // return (the query is endless). It returns at once with IsRunning true.
        var tab = await _t.NewTab(ScriptTabFixture.Endless);

        var run = tab.RunScriptAsync();

        Assert.False(run.IsCompleted);
        Assert.True(tab.IsRunning);
        tab.Cancel();
        await run;
        Assert.False(tab.IsRunning);
    }

    [Fact]
    public async Task Run_WhileRunning_IsIgnored()
    {
        var tab = await _t.NewTab(ScriptTabFixture.Endless);
        var first = tab.RunScriptAsync();

        await tab.RunScriptAsync();   // returns at once, does not start a second run

        Assert.True(tab.IsRunning);
        tab.Cancel();
        await first;
        Assert.False(tab.IsRunning);
    }

    [Fact]
    public async Task RowLimit_DefaultsTo1000_IsClamped_AndFeedsTheRunner()
    {
        var tab = await _t.NewTab();
        Assert.Equal(1000, tab.RowLimit);

        tab.RowLimit = 0;
        Assert.Equal(1, tab.RowLimit);
        tab.RowLimit = 5_000_000;
        Assert.Equal(100_000, tab.RowLimit);

        tab.RowLimit = 10;
        tab.Text = "SELECT id FROM big";
        await tab.RunScriptAsync();

        var result = Assert.Single(tab.Results);
        Assert.Equal(10, result.Sets[0].Rows.Count);
        Assert.True(result.IsTruncated);
        Assert.Equal("showing first 10 rows — raise the limit and re-run", result.TruncatedNotice);
    }

    [Fact]
    public async Task Results_NotTruncated_HaveNoNotice()
    {
        var tab = await _t.NewTab("SELECT id FROM t");

        await tab.RunScriptAsync();

        var result = Assert.Single(tab.Results);
        Assert.False(result.IsTruncated);
        Assert.Null(result.TruncatedNotice);
    }

    [Fact]
    public async Task NewRun_ReplacesPreviousResults()
    {
        var tab = await _t.NewTab("SELECT 1; SELECT 2");
        await tab.RunScriptAsync();
        Assert.Equal(2, tab.Results.Count);

        tab.Text = "SELECT 9";
        await tab.RunScriptAsync();

        Assert.Equal(9L, Cell(Assert.Single(tab.Results)));
    }

    // ---- closed / missing connection ----

    [Fact]
    public async Task ClosedConnection_ShowsMessage_AndReconnectRecovers()
    {
        var tab = await _t.NewTab("SELECT 1");
        _t.Sessions.Close(_t.Config.Id);   // Disconnect disposes the session

        await tab.RunScriptAsync();

        Assert.Equal(ScriptTabViewModel.ConnectionClosedMessage, tab.ErrorMessage);
        Assert.Equal("Connection closed — reconnect", tab.ErrorMessage);
        Assert.True(tab.NeedsReconnect);
        Assert.False(tab.IsRunning);
        Assert.Empty(tab.Results);

        await tab.ReconnectAsync();

        Assert.Null(tab.ErrorMessage);
        Assert.False(tab.NeedsReconnect);
        await tab.RunScriptAsync();
        Assert.Equal(1L, Cell(Assert.Single(tab.Results)));
    }

    [Fact]
    public async Task RestoredTab_ResolvesItsSessionOnFirstRun()
    {
        var tab = await _t.NewTab("SELECT 1", withSession: false);

        await tab.RunScriptAsync();

        Assert.Equal(1, _t.Resolved);
        Assert.Equal(1L, Cell(Assert.Single(tab.Results)));
        await tab.RunScriptAsync();
        Assert.Equal(1, _t.Resolved);   // kept
    }

    [Fact]
    public async Task ConnectionGone_ShowsMessage_NeverThrows()
    {
        var hooks = _t.Hooks with { ResolveSession = _ => Task.FromResult<Session?>(null) };
        var tab = _t.Track(new ScriptTabViewModel(_t.Config, null, hooks, 1) { Text = "SELECT 1" });

        await tab.RunScriptAsync();

        Assert.False(tab.IsRunning);
        Assert.Contains("no longer", tab.ErrorMessage);
    }

    [Fact]
    public async Task ResolveThrows_ShowsMessage_NeverThrows()
    {
        var hooks = _t.Hooks with { ResolveSession = _ => throw new InvalidOperationException("host unreachable") };
        var tab = _t.Track(new ScriptTabViewModel(_t.Config, null, hooks, 1) { Text = "SELECT 1" });

        await tab.RunScriptAsync();

        Assert.False(tab.IsRunning);
        Assert.Contains("host unreachable", tab.ErrorMessage);
    }

    // ---- title and files ----

    [Fact]
    public async Task Title_IsScriptNumberAndConnection_ThenFileName_WithDirtyMarker()
    {
        var tab = _t.Track(new ScriptTabViewModel(_t.Config, null, _t.Hooks, 3));
        Assert.Equal("Script 3 · Lite", tab.Title);
        Assert.False(tab.IsDirty);

        var titles = new List<string>();
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScriptTabViewModel.Title)) titles.Add(tab.Title);
        };
        tab.Text = "SELECT 1";
        Assert.True(tab.IsDirty);
        Assert.Equal("• Script 3 · Lite", tab.Title);
        Assert.NotEmpty(titles);

        var path = _t.TempFile("report.sql");
        await tab.SaveAsync(path);
        Assert.Equal("report.sql", tab.Title);

        tab.Text = "SELECT 2";
        Assert.Equal("• report.sql", tab.Title);
    }

    [Fact]
    public async Task Save_WritesFile_AndOpen_ReadsIt()
    {
        var path = _t.TempFile("a.sql");
        var tab = await _t.NewTab("SELECT 'é' AS x;\nSELECT 2");

        await tab.SaveAsync(path);

        Assert.Equal("SELECT 'é' AS x;\nSELECT 2", await File.ReadAllTextAsync(path));
        Assert.Equal(path, tab.FilePath);
        Assert.False(tab.IsDirty);

        var other = await _t.NewTab();
        await other.OpenAsync(path);
        Assert.Equal("SELECT 'é' AS x;\nSELECT 2", other.Text);
        Assert.Equal(path, other.FilePath);
        Assert.False(other.IsDirty);
        Assert.Equal(0, other.CaretOffset);
    }

    [Fact]
    public async Task Save_WithoutPath_UsesKnownFilePath_ElseAsksThePicker()
    {
        var tab = await _t.NewTab("SELECT 1");
        _t.Files.SavePath = _t.TempFile("picked.sql");

        await tab.SaveAsync(null);

        Assert.Equal(_t.Files.SavePath, tab.FilePath);
        Assert.True(File.Exists(_t.Files.SavePath));
        Assert.Equal(["Script 1.sql"], _t.Files.SuggestedNames);

        tab.Text = "SELECT 2";
        _t.Files.SavePath = null;   // a second save must not ask again
        await tab.SaveAsync(null);
        Assert.Equal("SELECT 2", await File.ReadAllTextAsync(tab.FilePath!));
        Assert.Single(_t.Files.SuggestedNames);
    }

    [Fact]
    public async Task Save_PickerCancelled_WritesNothing()
    {
        var tab = await _t.NewTab("SELECT 1");
        _t.Files.SavePath = null;

        await tab.SaveAsync(null);

        Assert.Null(tab.FilePath);
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public async Task Save_IoFailure_ReportsInsteadOfThrowing()
    {
        var tab = await _t.NewTab("SELECT 1");

        await tab.SaveAsync(Path.Combine(_t.Fx.Dir, "no-such-folder", "a.sql"));

        Assert.True(_t.Status.IsError);
        Assert.Null(tab.FilePath);
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public async Task Open_MissingFile_ReportsInsteadOfThrowing()
    {
        var tab = await _t.NewTab("keep me");

        await tab.OpenAsync(_t.TempFile("missing.sql"));

        Assert.True(_t.Status.IsError);
        Assert.Equal("keep me", tab.Text);
    }

    // ---- persistence of unsaved scripts ----

    [Fact]
    public async Task TextChange_IsPersistedAfterTheDebounce_AndRestoredIntoANewTab()
    {
        var tab = await _t.NewTab();

        tab.Text = "SELECT 1";
        tab.Text = "SELECT 12";   // the burst saves once, with the last text

        await Eventually(() => _t.Store.LoadAll().Count == 1, "script persisted");
        var saved = Assert.Single(_t.Store.LoadAll());
        Assert.Equal(tab.Id, saved.TabId);
        Assert.Equal(_t.Config.Id, saved.ConnectionId);
        Assert.Equal("SELECT 12", saved.Text);

        var restored = _t.Track(new ScriptTabViewModel(_t.Config, null, _t.Hooks, 5, saved));
        Assert.Equal(tab.Id, restored.Id);
        Assert.Equal("SELECT 12", restored.Text);
        Assert.True(restored.IsDirty);
    }

    [Fact]
    public async Task FlushPersist_SavesImmediately_WithoutWaitingForTheDebounce()
    {
        var tab = await _t.NewTab();
        tab.PersistDelay = TimeSpan.FromMinutes(5);
        tab.Text = "SELECT 1";

        tab.FlushPersist();

        Assert.Equal("SELECT 1", Assert.Single(_t.Store.LoadAll()).Text);
    }

    [Fact]
    public async Task SaveToFile_DeletesThePersistedCopy()
    {
        var tab = await _t.NewTab("SELECT 1");
        tab.FlushPersist();
        Assert.Single(_t.Store.LoadAll());

        await tab.SaveAsync(_t.TempFile("s.sql"));

        Assert.Empty(_t.Store.LoadAll());
        await Task.Delay(150);   // a debounce still pending must not bring it back
        Assert.Empty(_t.Store.LoadAll());
    }

    [Fact]
    public async Task EditingAFileBackedScript_PersistsItWithItsFilePath()
    {
        var path = _t.TempFile("f.sql");
        var tab = await _t.NewTab("SELECT 1");
        await tab.SaveAsync(path);

        tab.Text = "SELECT 1 -- edited";
        tab.FlushPersist();

        var saved = Assert.Single(_t.Store.LoadAll());
        Assert.Equal(path, saved.FilePath);
        Assert.Equal("SELECT 1 -- edited", saved.Text);
    }

    [Fact]
    public async Task EmptyScript_IsNotPersisted()
    {
        var tab = await _t.NewTab("SELECT 1");
        tab.FlushPersist();
        Assert.Single(_t.Store.LoadAll());

        tab.Text = "  ";
        tab.FlushPersist();

        Assert.Empty(_t.Store.LoadAll());
    }

    [Fact]
    public async Task Close_DeletesThePersistedCopy_AndStopsRunning()
    {
        var tab = await _t.NewTab(ScriptTabFixture.Endless);
        tab.FlushPersist();
        Assert.Single(_t.Store.LoadAll());
        var run = tab.RunScriptAsync();

        tab.Close();
        await run;

        Assert.Empty(_t.Store.LoadAll());
        Assert.False(tab.IsRunning);
        tab.Text = "SELECT 2";
        tab.FlushPersist();   // a closed tab never writes again
        Assert.Empty(_t.Store.LoadAll());
    }
}
