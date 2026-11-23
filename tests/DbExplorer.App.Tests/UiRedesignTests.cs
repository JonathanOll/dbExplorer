using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests;

/// <summary>The small view-model surface the redesigned views bind to (icons, dirty marker, empty state, status bar, toolbar).</summary>
public class UiRedesignTests : ShellTestBase
{
    // ---- tab header: icon, dirty marker ----

    [Fact]
    public async Task TabIconAndDirtyMarker_FollowTheTab()
    {
        var main = NewMain();
        var table = await AddTable(main, "orders");
        var view = await AddTable(main, "v_big");
        var script = AddScript(main, "select 1", persisted: false);

        Assert.Equal("IconTable", table.IconKey);
        Assert.Equal("IconView", view.IconKey);
        Assert.Equal("IconScript", script.IconKey);

        var changed = new List<string?>();
        table.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.False(table.HasUnsavedWork);
        table.SetCell(0, table.FindColumn("note")!.Value, "x");
        Assert.True(table.HasUnsavedWork);
        Assert.Contains(nameof(TabViewModel.HasUnsavedWork), changed);
        table.Revert();
        Assert.False(table.HasUnsavedWork);

        Assert.True(script.HasUnsavedWork);   // typed text that no file holds yet
    }

    // ---- shell: empty state and the connection named in the status bar ----

    [Fact]
    public async Task IsEmptyState_FollowsTheTabs_AndCurrentConnectionTheActiveTabOrTheSelection()
    {
        var main = NewMain();
        var raised = new List<string?>();
        main.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.True(main.IsEmptyState);
        Assert.Equal(T.Config.Id, main.CurrentConnection!.Id);   // no tab: the connection selected in the navigator

        await AddTable(main, "orders");
        Assert.False(main.IsEmptyState);
        Assert.Contains(nameof(MainViewModel.IsEmptyState), raised);
        Assert.Equal(T.Config.Id, main.CurrentConnection!.Id);   // the tab's connection
        Assert.Contains(nameof(MainViewModel.CurrentConnection), raised);

        main.Tree.SelectedNode = null;
        Assert.Equal(T.Config.Id, main.CurrentConnection!.Id);   // the active tab still names it

        main.Tabs.Clear();
        main.ActiveTab = null;
        Assert.True(main.IsEmptyState);
        Assert.Null(main.CurrentConnection);   // nothing selected, nothing open
    }

    // ---- navigator toolbar ----

    [Fact]
    public async Task RefreshSelected_NeedsAConnectedSelection_AndCollapseAllFoldsTheTree()
    {
        var main = NewMain();
        var tree = main.Tree;
        var node = tree.Nodes.OfType<ConnectionNodeViewModel>().Single();

        await tree.RefreshSelectedCommand.ExecuteAsync(null);
        Assert.Contains("connected", T.Fx.Status.Message);   // not connected yet: says what to do, nothing else

        await tree.ConnectAsync(node);
        Assert.True(node.IsExpanded);
        var table = node.Children.OfType<TableNodeViewModel>().First();
        tree.SelectedNode = table;   // a table of the connection counts
        await tree.RefreshSelectedCommand.ExecuteAsync(null);
        Assert.StartsWith("Refreshed", T.Fx.Status.Message);

        tree.CollapseAllCommand.Execute(null);
        Assert.False(node.IsExpanded);
        Assert.True(node.IsConnected);   // folding is not disconnecting
    }

    // ---- quick open and converters ----

    [Fact]
    public async Task QuickOpenItems_CarryTheirIcon()
    {
        var session = await T.OpenSession();
        var tables = QuickOpenViewModel.ForTables(session, (_, _) => Task.CompletedTask);
        Assert.Equal("IconView", tables.Results.Single(i => i.Label == "v_big").Icon);
        Assert.Equal("IconTable", tables.Results.First(i => i.Label == "orders").Icon);
        var columns = QuickOpenViewModel.ForColumns(["id", "name"], _ => { });
        Assert.All(columns.Results, i => Assert.Equal("IconColumn", i.Icon));
    }

    [Fact]
    public void Converters_StripTheDirtyBullet_AndNameTheEngines()
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal("Script 1 · Lite", Icons.WithoutBullet.Convert("• Script 1 · Lite", typeof(string), null, c));
        Assert.Equal("Script 1 · Lite", Icons.WithoutBullet.Convert("Script 1 · Lite", typeof(string), null, c));
        Assert.Equal("PostgreSQL", Icons.EngineName.Convert(DbKind.Postgres, typeof(string), null, c));
        Assert.Equal("SQL Server", Icons.EngineName.Convert(DbKind.SqlServer, typeof(string), null, c));
        Assert.Equal("SQLite", Icons.EngineName.Convert(DbKind.Sqlite, typeof(string), null, c));
        Assert.Equal("(3)", Icons.CountInParens.Convert(3, typeof(string), null, c));
    }
}
