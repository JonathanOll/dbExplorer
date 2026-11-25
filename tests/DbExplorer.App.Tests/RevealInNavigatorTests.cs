using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.Tests;

/// <summary>"Show active item in the navigator": folders, schemas and library folders open down to the active tab's node.</summary>
public class RevealInNavigatorTests : ShellTestBase
{
    private string LibDir => Path.Combine(T.Fx.Dir, "library");

    private static ConnectionNodeViewModel Node(MainViewModel main) => main.Tree.Nodes.OfType<ConnectionNodeViewModel>().First();

    [Fact]
    public async Task ATableTab_SelectsItsTable_AndOpensTheConnection()
    {
        var main = NewMain();
        var table = await AddTable(main, "orders");
        await main.Tree.ConnectAsync(Node(main));
        Node(main).IsExpanded = false;
        main.Tree.SelectedNode = null;
        var revealed = new List<TreeNodeViewModel>();
        main.Tree.NodeRevealed += revealed.Add;

        main.RevealActiveTabCommand.Execute(null);

        var selected = Assert.IsType<TableNodeViewModel>(main.Tree.SelectedNode);
        Assert.Equal("orders", selected.Table.Ref.Name);
        Assert.True(Node(main).IsExpanded);
        Assert.Same(selected, Assert.Single(revealed));
        Assert.Equal(table.Table, selected.Table.Ref);
    }

    [Fact]
    public async Task AConnectionInAFolder_OpensTheFolder()
    {
        var inFolder = TreeFixture.Conn("Other", "Prod");
        var config = new AppConfig(["Prod"], [T.Config, inFolder], new());
        var main = NewMain(config: config);
        var folder = main.Tree.Nodes.OfType<FolderNodeViewModel>().Single();
        main.Tree.SelectedNode = folder.Children[0];
        var script = AddScript(main, "SELECT 1");
        main.ActiveTab = script;
        folder.IsExpanded = false;
        main.Tree.SelectedNode = null;

        main.RevealActiveTabCommand.Execute(null);

        Assert.True(folder.IsExpanded);
        Assert.Equal("Other", Assert.IsType<ConnectionNodeViewModel>(main.Tree.SelectedNode).Title);   // an unsaved script: its connection
    }

    [Fact]
    public void ANotConnectedConnection_IsSelectedWithoutConnecting()
    {
        var main = NewMain();
        var script = AddScript(main, "SELECT 1");
        main.ActiveTab = script;
        main.Tree.SelectedNode = null;

        main.RevealActiveTabCommand.Execute(null);

        Assert.Same(Node(main), main.Tree.SelectedNode);
        Assert.False(Node(main).IsConnected);
    }

    [Fact]
    public void AScriptOfTheLibrary_OpensItsFolders_AndSelectsItsFile()
    {
        var main = NewMain();
        var lib = new ScriptLibrary(LibDir);
        main.UseScriptLibrary(lib, new RecentScripts(Path.Combine(T.Fx.Dir, "recent.json")));
        var deep = lib.CreateFolder(lib.CreateFolder(null, "Reports"), "2025");
        var file = lib.SaveNew(deep, "monthly", "select 1");
        main.Tree.RefreshScripts();
        var script = AddScript(main, "select 1");
        script.FilePath = file;
        main.ActiveTab = script;

        main.RevealActiveTabCommand.Execute(null);

        var selected = Assert.IsType<ScriptFileNodeViewModel>(main.Tree.SelectedNode);
        Assert.Equal(file, selected.Path);
        Assert.True(main.Tree.Nodes.OfType<ScriptsSectionNodeViewModel>().Single().IsExpanded);
    }

    [Fact]
    public async Task AFilterThatHidesTheNode_IsCleared()
    {
        var main = NewMain();
        await AddTable(main, "orders");
        await main.Tree.ConnectAsync(Node(main));
        main.Tree.FilterText = "zzz-nothing";
        Assert.False(Node(main).IsShown);

        main.RevealActiveTabCommand.Execute(null);

        Assert.Equal("", main.Tree.FilterText);
        Assert.True(Node(main).IsShown);
    }

    [Fact]
    public void NoTab_SaysSo_AndAHiddenNavigatorComesBack()
    {
        var main = NewMain();
        main.IsNavigatorVisible = false;

        main.RevealActiveTabCommand.Execute(null);

        Assert.True(main.IsNavigatorVisible);
        Assert.Contains("Open a tab", T.Fx.Status.Message);
    }
}
