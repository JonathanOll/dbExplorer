using DbExplorer.App.ViewModels;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.Tests;

/// <summary>Script tabs inside the shell: Ctrl+N, Ctrl+O, restore on start, persistence on tab change and close.</summary>
public class ScriptShellTests : IDisposable
{
    private readonly TreeFixture _fx = new();
    private readonly FakeScriptFiles _files = new();
    private readonly ScriptSessionStore _store;

    public ScriptShellTests() => _store = new ScriptSessionStore(Path.Combine(_fx.Dir, "scripts"));

    public void Dispose() => _fx.Dispose();

    private readonly FakeEditDialogs _dialogs = new() { Choice = SaveChoice.Discard };

    private MainViewModel NewMain(params ConnectionConfig[] connections)
    {
        var tree = _fx.CreateTree(new AppConfig([], connections.ToList(), new()));
        var main = new MainViewModel(tree, _fx.Status);
        main.UseScriptTabs(_files, _store, _dialogs);
        return main;
    }

    private ConnectionConfig SqliteConn(string name)
    {
        var file = _fx.CreateSqliteFile(name + ".db");
        return TreeFixture.Conn(name, database: file);
    }

    private static Session SessionFor(ConnectionConfig c) => new(c, DriverFactory.For(DbKind.Sqlite), [], null);

    [Fact]
    public async Task NewScript_UsesTheConnectionOfTheActiveTab()
    {
        var a = SqliteConn("A");
        var b = SqliteConn("B");
        var main = NewMain(a, b);
        await main.OpenTable(SessionFor(b), new TableRef(null, "users"));

        var script = main.NewScript();

        Assert.NotNull(script);
        Assert.Equal(b.Id, script.Connection.Id);
        Assert.Equal("Script 1 · B", script.Title);
        Assert.Same(script, main.ActiveTab);
        Assert.Contains(script, main.Tabs);
    }

    [Fact]
    public void NewScript_WithNoTab_UsesTheTreeSelection_AndNumbersScripts()
    {
        var a = SqliteConn("A");
        var main = NewMain(a);
        main.Tree.SelectedNode = main.Tree.Nodes.OfType<ConnectionNodeViewModel>().Single();

        var first = main.NewScript();
        var second = main.NewScript();

        Assert.Equal("Script 1 · A", first!.Title);
        Assert.Equal("Script 2 · A", second!.Title);
    }

    [Fact]
    public void NewScript_WithNoConnection_SetsAStatusMessage()
    {
        var main = NewMain(SqliteConn("A"));

        var script = main.NewScript();

        Assert.Null(script);
        Assert.Empty(main.Tabs);
        Assert.Contains("Ctrl+N", _fx.Status.Message);
    }

    [Fact]
    public async Task ScriptOnADisconnectedConnection_ConnectsOnFirstRun()
    {
        var a = SqliteConn("A");
        var main = NewMain(a);
        main.Tree.SelectedNode = main.Tree.Nodes.OfType<ConnectionNodeViewModel>().Single();
        var script = main.NewScript()!;
        script.Text = "SELECT count(*) FROM users";
        Assert.Null(main.Tree.FindSession(a.Id));

        await script.RunScriptAsync();

        Assert.NotNull(main.Tree.FindSession(a.Id));
        Assert.Equal(0L, Assert.Single(script.Results).Sets[0].Rows[0][0]);
        _fx.Sessions.Close(a.Id);
    }

    [Fact]
    public void RestoreScripts_RestoresScriptsOfExistingConnections_AndKeepsTheOthers()
    {
        var a = SqliteConn("A");
        var main = NewMain(a);
        var keepTab = Guid.NewGuid();
        var orphanTab = Guid.NewGuid();
        _store.Save(keepTab, a.Id, "SELECT 1", null);
        _store.Save(orphanTab, Guid.NewGuid(), "SELECT 2", null);

        var restored = main.RestoreScripts();

        Assert.Equal(1, restored);
        var tab = Assert.IsType<ScriptTabViewModel>(Assert.Single(main.Tabs));
        Assert.Equal(keepTab, tab.Id);
        Assert.Equal("SELECT 1", tab.Text);
        Assert.Equal(a.Id, tab.Connection.Id);
        Assert.Same(tab, main.ActiveTab);
        Assert.Contains(_store.LoadAll(), s => s.TabId == orphanTab);   // its connection is gone: file kept
    }

    [Fact]
    public async Task ClosingAScriptTab_DiscardingItsText_DeletesItsPersistedCopy()
    {
        var a = SqliteConn("A");
        var main = NewMain(a);
        await main.OpenTable(SessionFor(a), new TableRef(null, "users"));
        var script = main.NewScript()!;
        script.Text = "SELECT 1";
        script.FlushPersist();
        Assert.Single(_store.LoadAll());

        main.CloseTabCommand.Execute(script);

        Assert.Empty(_store.LoadAll());
        Assert.DoesNotContain(script, main.Tabs);
    }

    [Fact]
    public async Task SwitchingTabs_FlushesTheOutgoingScript()
    {
        var a = SqliteConn("A");
        var main = NewMain(a);
        await main.OpenTable(SessionFor(a), new TableRef(null, "users"));
        var table = main.ActiveTab!;
        var script = main.NewScript()!;
        script.PersistDelay = TimeSpan.FromMinutes(5);
        script.Text = "SELECT 1";
        Assert.Empty(_store.LoadAll());

        main.ActiveTab = table;

        Assert.Equal("SELECT 1", Assert.Single(_store.LoadAll()).Text);
        script.Close();
    }

    [Fact]
    public void PersistScripts_FlushesEveryScriptTab()
    {
        var a = SqliteConn("A");
        var main = NewMain(a);
        main.Tree.SelectedNode = main.Tree.Nodes.OfType<ConnectionNodeViewModel>().Single();
        foreach (var text in new[] { "SELECT 1", "SELECT 2" })
        {
            var s = main.NewScript()!;
            s.PersistDelay = TimeSpan.FromMinutes(5);
            s.Text = text;
        }

        main.PersistScripts();

        Assert.Equal(["SELECT 1", "SELECT 2"], _store.LoadAll().Select(s => s.Text).Order().ToArray());
        foreach (var s in main.Tabs.OfType<ScriptTabViewModel>()) s.Close();
    }

    [Fact]
    public async Task OpenScriptFile_OpensItInANewTab_ThenReusesAnEmptyScriptTab()
    {
        var a = SqliteConn("A");
        var main = NewMain(a);
        main.Tree.SelectedNode = main.Tree.Nodes.OfType<ConnectionNodeViewModel>().Single();
        var path = Path.Combine(_fx.Dir, "x.sql");
        await File.WriteAllTextAsync(path, "SELECT 42");
        _files.OpenPath = path;

        await main.OpenScriptFileAsync();
        var tab = Assert.IsType<ScriptTabViewModel>(main.ActiveTab);
        Assert.Equal("SELECT 42", tab.Text);
        Assert.Equal("x.sql", tab.Title);

        var empty = main.NewScript()!;
        await main.OpenScriptFileAsync();   // the empty scratch tab is reused instead of piling up tabs
        Assert.Equal(2, main.Tabs.Count);
        Assert.Equal("SELECT 42", empty.Text);
    }

    [Fact]
    public async Task OpenScriptFile_Cancelled_DoesNothing()
    {
        var a = SqliteConn("A");
        var main = NewMain(a);
        main.Tree.SelectedNode = main.Tree.Nodes.OfType<ConnectionNodeViewModel>().Single();
        _files.OpenPath = null;

        await main.OpenScriptFileAsync();

        Assert.Empty(main.Tabs);
    }

    [Fact]
    public void ScriptsLiveUnderTheDataDir()
    {
        var paths = AppPaths.Resolve(["--data-dir", @"C:\tmp\x"], _ => null);

        Assert.Equal(@"C:\tmp\x\scripts", paths.ScriptsDir);
    }
}
