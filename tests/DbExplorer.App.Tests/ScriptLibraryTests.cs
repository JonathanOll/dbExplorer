using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.Tests;

/// <summary>The script library: the navigator section, its commands, opening, recent scripts, the "Save script" flow.</summary>
public class ScriptLibraryTests : ShellTestBase
{
    private string LibDir => Path.Combine(T.Fx.Dir, "library");
    private string RecentFile => Path.Combine(T.Fx.Dir, "recent-scripts.json");

    private ScriptLibrary Lib() => new(LibDir);

    private MainViewModel LibraryMain(out ScriptLibrary library, out RecentScripts recent)
    {
        var main = NewMain();
        library = Lib();
        recent = new RecentScripts(RecentFile);
        main.UseScriptLibrary(library, recent);
        return main;
    }

    private static ScriptsSectionNodeViewModel Section(ConnectionTreeViewModel tree) => tree.Nodes.OfType<ScriptsSectionNodeViewModel>().Single();

    private static ScriptFileNodeViewModel File_(ConnectionTreeViewModel tree, string title) =>
        All(Section(tree)).OfType<ScriptFileNodeViewModel>().First(n => n.Title == title);

    private static ScriptFolderNodeViewModel Folder(ConnectionTreeViewModel tree, string title) =>
        All(Section(tree)).OfType<ScriptFolderNodeViewModel>().First(n => n.Title == title);

    private static IEnumerable<TreeNodeViewModel> All(TreeNodeViewModel node)
    {
        foreach (var child in node.Children)
        {
            yield return child;
            foreach (var deeper in All(child)) yield return deeper;
        }
    }

    // ---- the tree ----

    [Fact]
    public void Tree_ShowsTheLibrary_FoldersFirst_AndFolderChildrenAreLazy()
    {
        var main = LibraryMain(out var lib, out _);
        var f = lib.CreateFolder(null, "Reports");
        lib.SaveNew(f, "monthly", "select 1");
        lib.SaveNew(null, "adhoc", "select 2");
        main.Tree.RefreshScripts();

        var section = Section(main.Tree);

        Assert.Equal(["Reports", "adhoc"], section.Children.Select(c => c.Title).ToArray());
        var folder = Assert.IsType<ScriptFolderNodeViewModel>(section.Children[0]);
        Assert.True(folder.HasUnrealizedChildren);   // nothing listed until it is opened
        folder.IsExpanded = true;
        Assert.Equal(["monthly"], folder.Children.Select(c => c.Title).ToArray());
        Assert.False(section.IsLibraryEmpty);
    }

    [Fact]
    public void Tree_EmptyLibrary_IsFlagged_AndTheNavigatorStaysInItsEmptyState()
    {
        var main = LibraryMain(out _, out _);
        Assert.True(Section(main.Tree).IsLibraryEmpty);

        var bare = T.Fx.CreateTree(new Core.Model.AppConfig([], [], new()));
        bare.UseLibrary(Lib());
        Assert.True(bare.IsEmpty);   // no connection: the "No connections yet" state, the section does not hide it
    }

    [Fact]
    public void WithoutALibrary_ThereIsNoScriptsSection()
    {
        var main = NewMain();
        Assert.DoesNotContain(main.Tree.Nodes, n => n is ScriptsSectionNodeViewModel);
    }

    [Fact]
    public void ShowScripts_Toggle_RemovesTheSection_AndIsPersisted()
    {
        var main = LibraryMain(out _, out _);

        main.Tree.ToggleScriptsCommand.Execute(null);

        Assert.False(main.Tree.ShowScripts);
        Assert.DoesNotContain(main.Tree.Nodes, n => n is ScriptsSectionNodeViewModel);
        Assert.False(T.Fx.Store.Load().ShowScripts);
        main.Tree.ToggleScriptsCommand.Execute(null);
        Assert.Contains(main.Tree.Nodes, n => n is ScriptsSectionNodeViewModel);
        Assert.True(T.Fx.Store.Load().ShowScripts);
    }

    [Fact]
    public void ShowScripts_DefaultsToOn_ForAnOldConfigFile()
    {
        File.WriteAllText(T.Fx.ConfigPath, """{"Folders":[],"Connections":[],"VirtualKeys":{}}""");
        Assert.True(T.Fx.Store.Load().ShowScripts);
    }

    [Fact]
    public void Filter_FindsAScriptInACollapsedFolder_ByName()
    {
        var main = LibraryMain(out var lib, out _);
        lib.SaveNew(lib.CreateFolder(null, "Reports"), "quarterly sales", "x");
        lib.SaveNew(null, "other", "x");
        main.Tree.RefreshScripts();

        main.Tree.FilterText = "quarter";

        var folder = Folder(main.Tree, "Reports");
        Assert.True(folder.IsShown);
        Assert.True(File_(main.Tree, "quarterly sales").IsShown);
        Assert.False(File_(main.Tree, "other").IsShown);
    }

    [Fact]
    public void Refresh_KeepsExpandedFolders()
    {
        var main = LibraryMain(out var lib, out _);
        lib.SaveNew(lib.CreateFolder(null, "A"), "one", "x");
        main.Tree.RefreshScripts();
        Folder(main.Tree, "A").IsExpanded = true;

        lib.SaveNew(null, "added outside", "x");
        main.Tree.RefreshScripts();

        Assert.True(Folder(main.Tree, "A").IsExpanded);
        Assert.Contains(All(Section(main.Tree)), n => n.Title == "one");
        Assert.Contains(All(Section(main.Tree)), n => n.Title == "added outside");
    }

    // ---- commands ----

    [Fact]
    public async Task NewFolder_AndNewScriptHere_CreateThemAndOpenTheScript()
    {
        var main = LibraryMain(out var lib, out _);
        T.Fx.Dialogs.PromptResult = "Work";
        await main.Tree.NewScriptFolderCommand.ExecuteAsync(Section(main.Tree));
        Assert.True(Directory.Exists(Path.Combine(LibDir, "Work")));

        T.Fx.Dialogs.PromptResult = "first";
        await main.Tree.NewScriptHereCommand.ExecuteAsync(Folder(main.Tree, "Work"));

        var file = Path.Combine(LibDir, "Work", "first.sql");
        Assert.True(File.Exists(file));
        Assert.Equal(file, (main.ActiveTab as ScriptTabViewModel)?.FilePath);
        Assert.Contains(All(Section(main.Tree)), n => n.Title == "first");
    }

    [Fact]
    public async Task NewFolder_WithATraversalName_IsRefused_NothingIsCreated()
    {
        var main = LibraryMain(out _, out _);
        T.Fx.Dialogs.PromptResult = "..\\evil";

        await main.Tree.NewScriptFolderCommand.ExecuteAsync(Section(main.Tree));

        Assert.False(Directory.Exists(Path.Combine(T.Fx.Dir, "evil")));
        Assert.True(main.Status.IsError);
    }

    [Fact]
    public async Task Rename_RenamesTheFile_AndAnOpenTabFollows()
    {
        var main = LibraryMain(out var lib, out _);
        var path = lib.SaveNew(null, "old", "select 1");
        main.Tree.RefreshScripts();
        await main.Tree.OpenScriptCommand.ExecuteAsync(File_(main.Tree, "old"));
        var tab = (ScriptTabViewModel)main.ActiveTab!;
        T.Fx.Dialogs.PromptResult = "renamed";

        await main.Tree.RenameScriptNodeCommand.ExecuteAsync(File_(main.Tree, "old"));

        var renamed = Path.Combine(LibDir, "renamed.sql");
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(renamed));
        Assert.Equal(renamed, tab.FilePath);
        Assert.Equal(["renamed"], Section(main.Tree).Children.Select(c => c.Title).ToArray());
    }

    [Fact]
    public async Task Rename_ToAnExistingName_IsRefused_OnlyTheMessageChanges()
    {
        var main = LibraryMain(out var lib, out _);
        lib.SaveNew(null, "a", "1");
        lib.SaveNew(null, "b", "2");
        main.Tree.RefreshScripts();
        T.Fx.Dialogs.PromptResult = "b";

        await main.Tree.RenameScriptNodeCommand.ExecuteAsync(File_(main.Tree, "a"));

        Assert.True(main.Status.IsError);
        Assert.Equal("1", File.ReadAllText(Path.Combine(LibDir, "a.sql")));
        Assert.Equal("2", File.ReadAllText(Path.Combine(LibDir, "b.sql")));
    }

    [Fact]
    public async Task Delete_AsksFirst_AndKeepsTheFileWhenRefused()
    {
        var main = LibraryMain(out var lib, out _);
        var path = lib.SaveNew(null, "s", "x");
        main.Tree.RefreshScripts();
        T.Fx.Dialogs.ConfirmResult = false;

        await main.Tree.DeleteScriptNodeCommand.ExecuteAsync(File_(main.Tree, "s"));
        Assert.True(File.Exists(path));

        T.Fx.Dialogs.ConfirmResult = true;
        await main.Tree.DeleteScriptNodeCommand.ExecuteAsync(File_(main.Tree, "s"));
        Assert.False(File.Exists(path));
        Assert.Empty(Section(main.Tree).Children);
    }

    [Fact]
    public async Task DeleteFolder_RemovesItsScripts_AndAnOpenTabKeepsItsTextAsUnsavedWork()
    {
        var main = LibraryMain(out var lib, out _);
        var folder = lib.CreateFolder(null, "F");
        var path = lib.SaveNew(folder, "s", "select 1");
        main.Tree.RefreshScripts();
        await main.OpenScriptPathAsync(path);
        var tab = (ScriptTabViewModel)main.ActiveTab!;

        await main.Tree.DeleteScriptNodeCommand.ExecuteAsync(Folder(main.Tree, "F"));

        Assert.False(Directory.Exists(folder));
        Assert.Null(tab.FilePath);
        Assert.Equal("select 1", tab.Text);
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public async Task Duplicate_AddsACopy()
    {
        var main = LibraryMain(out var lib, out _);
        lib.SaveNew(null, "s", "select 1");
        main.Tree.RefreshScripts();

        main.Tree.DuplicateScriptCommand.Execute(File_(main.Tree, "s"));

        Assert.Equal("select 1", File.ReadAllText(Path.Combine(LibDir, "s (2).sql")));
        Assert.Equal(["s", "s (2)"], Section(main.Tree).Children.Select(c => c.Title).ToArray());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task MoveToFolder_AsksForTheFolder_AndMovesTheScript()
    {
        var main = LibraryMain(out var lib, out _);
        lib.CreateFolder(null, "Target");
        var path = lib.SaveNew(null, "s", "x");
        main.Tree.RefreshScripts();
        T.Fx.Dialogs.ChooseResult = 0;   // the only other place: Target (the script is already at the top level)

        await main.Tree.MoveScriptNodeCommand.ExecuteAsync(File_(main.Tree, "s"));

        Assert.Equal(["Target"], T.Fx.Dialogs.ChooseOptions.Single().ToArray());
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(LibDir, "Target", "s.sql")));
    }

    [Fact]
    public void Move_ByDrop_ToAFolder_AndBackToTheTopLevel_RefusingOverwriteAndSelfMoves()
    {
        var main = LibraryMain(out var lib, out _);
        var a = lib.CreateFolder(null, "a");
        var b = lib.CreateFolder(a, "b");
        var script = lib.SaveNew(null, "s", "root");
        main.Tree.RefreshScripts();

        Assert.True(main.Tree.MoveScriptNode(script, a));
        var moved = Path.Combine(a, "s.sql");
        Assert.True(File.Exists(moved));
        Assert.True(main.Tree.MoveScriptNode(moved, null));
        Assert.True(File.Exists(script));

        Assert.False(main.Tree.MoveScriptNode(a, b));   // into its own descendant
        Assert.True(main.Status.IsError);
        Assert.True(Directory.Exists(b));

        lib.SaveNew(a, "s", "inside");
        Assert.False(main.Tree.MoveScriptNode(script, a));   // would overwrite
        Assert.Equal("root", File.ReadAllText(script));
    }

    [Fact]
    public async Task CopyPath_AndShowInExplorer_UseTheHooks()
    {
        var main = LibraryMain(out var lib, out _);
        var path = lib.SaveNew(null, "s", "x");
        main.Tree.RefreshScripts();
        string? copied = null, revealed = null;
        main.Tree.CopyText = text => { copied = text; return Task.CompletedTask; };
        main.Tree.RevealInShell = p => revealed = p;

        await main.Tree.CopyScriptPathCommand.ExecuteAsync(File_(main.Tree, "s"));
        main.Tree.ShowScriptInExplorerCommand.Execute(File_(main.Tree, "s"));

        Assert.Equal(path, copied);
        Assert.Equal(path, revealed);
    }

    // ---- opening ----

    [Fact]
    public async Task DoubleClick_OpensTheScriptInATab_AndAgainOnlyActivatesIt()
    {
        var main = LibraryMain(out var lib, out var recent);
        var path = lib.SaveNew(null, "s", "select 42");
        main.Tree.RefreshScripts();

        await main.Tree.OpenScriptCommand.ExecuteAsync(File_(main.Tree, "s"));

        var tab = Assert.IsType<ScriptTabViewModel>(main.ActiveTab);
        Assert.Equal(path, tab.FilePath);
        Assert.Equal("select 42", tab.Text);
        Assert.Contains(path, recent.Items);

        var other = main.NewScript()!;
        Assert.Same(other, main.ActiveTab);
        var count = main.Tabs.Count;
        await main.Tree.OpenScriptCommand.ExecuteAsync(File_(main.Tree, "s"));

        Assert.Equal(count, main.Tabs.Count);
        Assert.Same(tab, main.ActiveTab);
    }

    [Fact]
    public async Task Open_ReusesTheEmptyScriptTabInFront()
    {
        var main = LibraryMain(out var lib, out _);
        var path = lib.SaveNew(null, "s", "select 1");
        var empty = main.NewScript()!;
        var before = main.Tabs.Count;

        await main.OpenScriptPathAsync(path);

        Assert.Equal(before, main.Tabs.Count);
        Assert.Equal(path, empty.FilePath);
    }

    // ---- recent scripts ----

    [Fact]
    public async Task Recent_ListsOpenedAndSavedScripts_AnywhereOnDisk_MostRecentFirst()
    {
        var main = LibraryMain(out _, out _);
        var outside = Path.Combine(T.Fx.Dir, "elsewhere.sql");
        File.WriteAllText(outside, "select 1");
        Files.OpenPath = outside;
        await main.OpenScriptFileCommand.ExecuteAsync(null);

        var script = main.NewScript()!;
        script.Text = "select 2";
        var saved = Path.Combine(T.Fx.Dir, "saved-elsewhere.sql");
        await script.SaveAsync(saved);

        main.RefreshRecentScripts();

        Assert.Equal([saved, outside], main.RecentItems.Select(r => r.Path).ToArray());
        Assert.True(main.HasRecent);
        Assert.Equal("saved-elsewhere.sql", main.RecentItems[0].Name);
    }

    [Fact]
    public async Task Recent_ClickOpens_AMissingFileIsRemovedWithAMessage_AndClearEmptiesTheList()
    {
        var main = LibraryMain(out var lib, out var recent);
        var path = lib.SaveNew(null, "s", "select 1");
        var gone = lib.SaveNew(null, "gone", "x");
        recent.Add(gone);
        recent.Add(path);
        main.RefreshRecentScripts();

        await main.OpenRecentScriptCommand.ExecuteAsync(path);
        Assert.Equal(path, (main.ActiveTab as ScriptTabViewModel)?.FilePath);

        File.Delete(gone);
        await main.OpenRecentScriptCommand.ExecuteAsync(gone);
        Assert.True(main.Status.IsError);
        Assert.DoesNotContain(gone, recent.Items);

        main.ClearRecentScriptsCommand.Execute(null);
        Assert.Empty(main.RecentItems);
        Assert.False(main.HasRecent);
    }

    // ---- "Save script" ----

    [Fact]
    public async Task Save_NeverSavedScript_GoesThroughTheDialog_IntoTheChosenLibraryFolder()
    {
        var main = LibraryMain(out var lib, out var recent);
        var folder = lib.CreateFolder(null, "Reports");
        var script = AddScript(main, "select 1");
        Dialogs.OnSaveScript = vm =>
        {
            Assert.Equal("Script 1", vm.Name);   // prefilled
            vm.Name = "monthly";
            vm.SelectedFolder = vm.Folders.Single(f => f.Path == folder);
            return vm.TryConfirm();
        };

        await script.SaveAsync();

        var file = Path.Combine(folder, "monthly.sql");
        Assert.Equal(file, script.FilePath);
        Assert.Equal("select 1", File.ReadAllText(file));
        Assert.False(script.IsDirty);
        Assert.Empty(Files.SuggestedNames);   // the platform picker was not needed
        Assert.Contains(file, recent.Items);
        Assert.Contains(All(Section(main.Tree)), n => n.Title == "Reports");
    }

    [Fact]
    public async Task Save_NameCollision_GetsANumberedName_NothingIsOverwritten()
    {
        var main = LibraryMain(out var lib, out _);
        var existing = lib.SaveNew(null, "report", "precious");
        var script = AddScript(main, "select 1");
        Dialogs.OnSaveScript = vm =>
        {
            vm.Name = "report";
            Assert.Contains("report (2).sql", vm.Hint);
            return vm.TryConfirm();
        };

        await script.SaveAsync();

        Assert.Equal(Path.Combine(LibDir, "report (2).sql"), script.FilePath);
        Assert.Equal("precious", File.ReadAllText(existing));
    }

    [Fact]
    public async Task Save_TraversalNameInTheDialog_IsRefused_NoFileIsWritten()
    {
        var main = LibraryMain(out _, out _);
        var script = AddScript(main, "select 1");
        SaveScriptViewModel? seen = null;
        Dialogs.OnSaveScript = vm =>
        {
            seen = vm;
            vm.Name = "..\\..\\evil";
            return vm.TryConfirm();   // refused: the real dialog stays open, here the user gives up
        };

        await script.SaveAsync();

        Assert.NotNull(seen!.Error);
        Assert.False(seen.CanSave);
        Assert.Null(script.FilePath);
        Assert.False(File.Exists(Path.Combine(T.Fx.Dir, "evil.sql")));
        Assert.Empty(Directory.Exists(LibDir) ? Directory.GetFiles(LibDir, "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task Save_CancelledDialog_LeavesNothingBehind()
    {
        var main = LibraryMain(out _, out _);
        var script = AddScript(main, "select 1");
        Dialogs.OnSaveScript = _ => false;

        await script.SaveAsync();

        Assert.Null(script.FilePath);
        Assert.True(script.IsDirty);
        Assert.Empty(Directory.Exists(LibDir) ? Directory.GetFiles(LibDir, "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task Save_ChooseAnotherLocation_FallsBackToTheFilePicker()
    {
        var main = LibraryMain(out _, out _);
        var script = AddScript(main, "select 1");
        var target = Path.Combine(T.Fx.Dir, "picked.sql");
        Files.SavePath = target;
        Dialogs.OnSaveScript = vm =>
        {
            vm.ChooseAnotherLocation();
            return true;
        };

        await script.SaveAsync();

        Assert.Equal(target, script.FilePath);
        Assert.True(File.Exists(target));
        Assert.Single(Files.SuggestedNames);
    }

    [Fact]
    public async Task Save_AlreadySavedScript_SavesSilently_AndSaveAsAlwaysAsksTheFilePicker()
    {
        var main = LibraryMain(out var lib, out _);
        var path = lib.SaveNew(null, "s", "old");
        await main.OpenScriptPathAsync(path);
        var script = (ScriptTabViewModel)main.ActiveTab!;
        script.Text = "new";
        Dialogs.OnSaveScript = _ => throw new InvalidOperationException("the dialog must not open");

        await script.SaveAsync();
        Assert.Equal("new", File.ReadAllText(path));

        var other = Path.Combine(T.Fx.Dir, "copy.sql");
        Files.SavePath = other;
        await main.SaveScriptAsCommand.ExecuteAsync(null);
        Assert.Equal(other, script.FilePath);
        Assert.Equal("new", File.ReadAllText(other));
    }

    [Fact]
    public void SaveDialog_NewFolder_IsCreatedUnderTheSelectedFolder_AndSelected()
    {
        var lib = Lib();
        var vm = new SaveScriptViewModel(lib, "Script 1");

        vm.BeginNewFolderCommand.Execute(null);
        vm.NewFolderName = "Fresh";
        Assert.True(vm.TryCreateFolder());

        Assert.Equal(Path.Combine(LibDir, "Fresh"), vm.SelectedFolder.Path);
        Assert.False(vm.IsCreatingFolder);
        vm.BeginNewFolderCommand.Execute(null);
        vm.NewFolderName = "..\\x";
        Assert.False(vm.TryCreateFolder());
        Assert.True(vm.HasFolderError);
        Assert.True(vm.TryConfirm());
        Assert.Equal(Path.Combine(LibDir, "Fresh", "Script 1.sql"), vm.SavedPath);
    }

    [Fact]
    public void SaveDialog_EmptyName_DisablesSave()
    {
        var vm = new SaveScriptViewModel(Lib(), "x");
        vm.Name = "  ";
        Assert.False(vm.CanSave);
        Assert.False(vm.TryConfirm());
        Assert.Null(vm.SavedPath);
    }

    // ---- the window ----

    [AvaloniaFact]
    public void Window_HasTheScriptsSection_ItsContextMenu_AndTheFileMenuEntries()
    {
        var main = LibraryMain(out var lib, out _);
        lib.SaveNew(lib.CreateFolder(null, "F"), "s", "x");
        main.Tree.RefreshScripts();
        var window = new MainWindow { DataContext = main, Width = 1200, Height = 700 };
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var tree = window.FindControl<TreeView>("ConnectionTree")!;
            Assert.Contains(main.Tree.Nodes, n => n is ScriptsSectionNodeViewModel);
            var items = tree.GetVisualDescendants().OfType<TreeViewItem>().ToList();
            var sectionItem = items.First(i => i.DataContext is ScriptsSectionNodeViewModel);
            var menu = sectionItem.GetVisualDescendants().OfType<StackPanel>().Select(p => p.ContextMenu).First(m => m is not null)!;
            var headers = menu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToArray();
            Assert.Contains("New folder…", headers);
            Assert.Contains("Refresh", headers);
            Assert.Contains("Open library folder", headers);

            var file = window.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == "_File");
            var fileHeaders = file.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToArray();
            Assert.Contains("Save script _as…", fileHeaders);
            Assert.Contains("Recent _scripts", fileHeaders);
            Assert.Contains("Open script _library folder", fileHeaders);
            var view = window.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == "_View");
            Assert.Contains(view.Items.OfType<MenuItem>(), m => m.Header?.ToString() == "Show _scripts");
        }
        finally
        {
            window.Close();
        }
    }
}
