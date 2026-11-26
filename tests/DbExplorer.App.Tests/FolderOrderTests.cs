using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;
using static DbExplorer.App.Tests.TreeFixture;

namespace DbExplorer.App.Tests;

public class FolderOrderTests : IDisposable
{
    private readonly TreeFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private ConnectionTreeViewModel Tree(string[] folders, params ConnectionConfig[] connections) =>
        _fx.CreateTree(new AppConfig(folders.ToList(), connections.ToList(), new()));

    private static IEnumerable<string> Names(ConnectionTreeViewModel tree) =>
        tree.Nodes.OfType<FolderNodeViewModel>().Select(f => f.Name);

    private static FolderNodeViewModel Folder(ConnectionTreeViewModel tree, string name) =>
        tree.Nodes.OfType<FolderNodeViewModel>().Single(f => f.Name == name);

    [Theory]
    [InlineData("C", "A", false, "C,A,B")]
    [InlineData("C", "A", true, "A,C,B")]
    [InlineData("A", "C", false, "B,A,C")]
    [InlineData("A", "C", true, "B,C,A")]
    public void MoveFolder_PlacesBeforeOrAfter_AndPersists(string name, string target, bool after, string expected)
    {
        var tree = Tree(["A", "B", "C"]);

        Assert.True(tree.MoveFolder(name, target, after));

        Assert.Equal(expected.Split(','), Names(tree));
        Assert.Equal(expected.Split(','), _fx.Store.Load().Folders);
    }

    [Theory]
    [InlineData("A", "A", true)]
    [InlineData("A", "B", false)]   // already there
    [InlineData("B", "A", true)]    // already there
    [InlineData("A", "Nope", true)]
    [InlineData("Nope", "A", true)]
    public void MoveFolder_WithoutChange_IsRefused(string name, string target, bool after)
    {
        var tree = Tree(["A", "B"]);

        Assert.False(tree.CanMoveFolder(name, target, after));
        Assert.False(tree.MoveFolder(name, target, after));
        Assert.Equal(["A", "B"], Names(tree));
    }

    [Fact]
    public void MoveFolder_ReordersFoldersKnownOnlyThroughConnections()
    {
        var tree = Tree(["A"], Conn("one", "Ghost"), Conn("two", "Other"));

        Assert.True(tree.MoveFolder("Other", "A", after: false));

        Assert.Equal(["Other", "A", "Ghost"], Names(tree));
        Assert.Equal(["Other", "A", "Ghost"], _fx.Store.Load().Folders);
        Assert.Equal("two", Assert.Single(Folder(tree, "Other").Children).Title);
    }

    [Fact]
    public void RenameFolder_KnownOnlyThroughConnections_KeepsItsPlace()
    {
        var tree = Tree([], Conn("one", "First"), Conn("two", "Second"), Conn("three", "Third"));

        Assert.True(tree.RenameFolder("Second", "Renamed"));

        Assert.Equal(["First", "Renamed", "Third"], Names(tree));
    }

    [Fact]
    public void MoveUpAndDown_MoveOneStep_AndAreDisabledAtTheEnds()
    {
        var tree = Tree(["A", "B", "C"]);
        var first = Folder(tree, "A");
        Assert.False(first.CanMoveUp);
        Assert.NotNull(first.MoveUpTip);
        Assert.True(first.CanMoveDown);
        Assert.Null(first.MoveDownTip);
        Assert.False(Folder(tree, "C").CanMoveDown);
        Assert.NotNull(Folder(tree, "C").MoveDownTip);

        tree.MoveFolderDownCommand.Execute(first);

        Assert.Equal(["B", "A", "C"], Names(tree));
        Assert.True(first.CanMoveUp);
        Assert.Null(first.MoveUpTip);
        Assert.False(Folder(tree, "B").CanMoveUp);

        tree.MoveFolderUpCommand.Execute(Folder(tree, "C"));
        Assert.Equal(["B", "C", "A"], Names(tree));
        Assert.False(first.CanMoveDown);

        tree.MoveFolderDownCommand.Execute(first);   // already last: nothing happens
        Assert.Equal(["B", "C", "A"], _fx.Store.Load().Folders);
    }

    [Fact]
    public void SingleFolder_CannotMove()
    {
        var folder = Folder(Tree(["Only"]), "Only");

        Assert.False(folder.CanMoveUp);
        Assert.False(folder.CanMoveDown);
    }

    [AvaloniaFact]
    public void MoveFolder_InABoundTreeView_KeepsSelectionAndExpansion()
    {
        var tree = Tree(["A", "B"], Conn("one", "B"));
        var folder = Folder(tree, "B");
        folder.IsExpanded = true;
        var selected = folder.Children[0];
        var view = new TreeView { ItemsSource = tree.Nodes };
        view.Bind(TreeView.SelectedItemProperty,
            new Avalonia.Data.Binding(nameof(tree.SelectedNode)) { Source = tree, Mode = Avalonia.Data.BindingMode.TwoWay });
        var window = new Window { Content = view };
        window.Show();
        tree.SelectedNode = selected;
        Dispatcher.UIThread.RunJobs();

        tree.MoveFolder("B", "A", after: false);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(folder, tree.Nodes[0]);
        Assert.True(folder.IsExpanded);
        Assert.Same(selected, tree.SelectedNode);
        Assert.Same(selected, view.SelectedItem);
        window.Close();
    }
}
