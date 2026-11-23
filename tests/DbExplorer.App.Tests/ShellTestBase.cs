using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.Tests;

/// <summary>A shell (MainViewModel on a real tree) over the synthetic SQLite fixture, with fake dialogs and file pickers.</summary>
public abstract class ShellTestBase : IDisposable
{
    internal readonly TableTabFixture T = new();
    internal readonly FakeEditDialogs Dialogs = new();
    internal readonly FakeScriptFiles Files = new();
    internal readonly ScriptSessionStore Store;
    private readonly List<MainViewModel> _mains = [];

    protected ShellTestBase() => Store = new ScriptSessionStore(Path.Combine(T.Fx.Dir, "scripts"));

    public void Dispose()
    {
        foreach (var script in _mains.SelectMany(m => m.Tabs.OfType<ScriptTabViewModel>())) script.Close();   // no autosave timer may outlive the temp folder
        T.Dispose();
    }

    /// <param name="scriptDialogs">Whether script tabs get the Save / Discard / Cancel dialog.</param>
    /// <param name="useStore">False: no autosave store at all.</param>
    /// <param name="store">A store to use instead of the default one.</param>
    internal MainViewModel NewMain(bool scriptDialogs = true, bool useStore = true, ScriptSessionStore? store = null)
    {
        var tree = T.Fx.CreateTree(new AppConfig([], [T.Config], new()));
        var main = new MainViewModel(tree, T.Fx.Status);
        main.UseScriptTabs(Files, useStore ? store ?? Store : null, scriptDialogs ? Dialogs : null);
        tree.SelectedNode = tree.Nodes.OfType<ConnectionNodeViewModel>().Single();
        _mains.Add(main);
        return main;
    }

    internal async Task<TableTabViewModel> AddTable(MainViewModel main, string table, bool activate = true)
    {
        var session = await T.OpenSession();
        var hooks = new TableTabHooks(
            ct => T.Sessions.GetOrOpenAsync(T.Config, ct).ContinueWith(x => (Session?)x.Result, ct),
            (_, _, _) => Task.CompletedTask, Dialogs, () => main.Tree.Config, main.Tree.SetVirtualKey);
        var tab = new TableTabViewModel(session, new TableRef(null, table), hooks);
        await tab.LoadAsync();
        main.Tabs.Add(tab);
        if (activate) main.ActiveTab = tab;
        return tab;
    }

    internal static ScriptTabViewModel AddScript(MainViewModel main, string text, bool persisted = true)
    {
        var script = main.NewScript()!;
        script.PersistDelay = TimeSpan.FromMinutes(5);   // only explicit flushes write
        script.Text = text;
        if (persisted) script.FlushPersist();
        return script;
    }
}
