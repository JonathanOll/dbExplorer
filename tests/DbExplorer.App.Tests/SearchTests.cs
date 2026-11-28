using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.Tests;

/// <summary>The search overlay: view model with fakes, the shell wiring (Ctrl+Alt+F, opening a hit) and the view.</summary>
public sealed class SearchTests : ShellTestBase
{
    private static SearchScriptSource Tab(string name, string text, bool unsaved = true, string? path = null) =>
        new(SearchViewModel.TabKeyPrefix + name, name, path, text, unsaved);

    private SearchViewModel Vm(
        IReadOnlyList<SearchScriptSource>? tabs = null, string? root = null, Func<ConnectionConfig, CancellationToken, Task<Session?>>? connect = null,
        List<ScriptMatch>? openedScripts = null, List<(Session, TableRef, IReadOnlyDictionary<string, string>)>? openedTables = null) =>
        new(new SearchHooks(
            () => tabs ?? [], () => root, () => [T.Config],
            connect ?? (async (_, ct) => await T.OpenSession()),
            m => { openedScripts?.Add(m); return Task.CompletedTask; },
            (s, t, f) => { openedTables?.Add((s, t, f)); return Task.CompletedTask; }), T.Fx.Status);

    private string Library(params (string Name, string Text)[] files)
    {
        var dir = Path.Combine(T.Fx.Dir, "library");
        Directory.CreateDirectory(dir);
        foreach (var (name, text) in files)
        {
            var path = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        return dir;
    }

    // ---- scripts scope ----

    [Fact]
    public async Task Scripts_OpenTabsFirst_MarkedUnsaved_ThenLibraryFiles_GroupedWithHeaders()
    {
        var root = Library(("a.sql", "select needle from a"), ("sub/b.sql", "-- x\nNEEDLE\nneedle again"));
        var vm = Vm([Tab("Script 1", "first\nneedle here", unsaved: true)], root);
        vm.Query = "needle";

        await vm.SearchAsync();

        var headers = vm.Rows.OfType<SearchHeaderRow>().ToList();
        Assert.Equal(["Script 1", "a", "b"], headers.Select(h => h.Title));
        Assert.True(headers[0].IsUnsaved);
        Assert.False(headers[1].IsUnsaved);
        Assert.Equal([1, 1, 2], headers.Select(h => h.Count));
        Assert.Equal(4, vm.HitCount);
        Assert.Equal("4 matches in 3 scripts", vm.StatusText);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task Scripts_ANewSearch_CountsItsScriptsFromZero()
    {
        var vm = Vm([Tab("one", "needle"), Tab("two", "needle")]);
        vm.Query = "needle";
        await vm.SearchAsync();
        Assert.Equal("2 matches in 2 scripts", vm.StatusText);

        await vm.SearchAsync();

        Assert.Equal("2 matches in 2 scripts", vm.StatusText);
    }

    [Fact]
    public async Task Scripts_TheTabTextReplacesItsFile_SoUnsavedEditsAreSearched()
    {
        var root = Library(("a.sql", "needle on disk"));
        var path = Path.Combine(root, "a.sql");
        var vm = Vm([Tab("a.sql", "edited, no more match", path: path), Tab("other", "needle in other")], root);
        vm.Query = "needle";

        await vm.SearchAsync();

        Assert.Equal(["other"], vm.Rows.OfType<SearchHeaderRow>().Select(h => h.Title));   // the file on disk was not read
    }

    [Fact]
    public async Task Scripts_StopAtTheCap_WithAMoreResultsNotice()
    {
        var text = string.Join("\n", Enumerable.Repeat("needle", SearchViewModel.MaxResults + 500));
        var vm = Vm([Tab("big", text)]);
        vm.Query = "needle";

        await vm.SearchAsync();

        Assert.Equal(SearchViewModel.MaxResults, vm.HitCount);
        Assert.Equal(SearchViewModel.MaxResults, vm.Rows.OfType<SearchHitRow>().Count());
        Assert.Contains("not shown", vm.Notice);
    }

    [Fact]
    public async Task Scripts_ExactlyTheCap_HasNoNotice()
    {
        var vm = Vm([Tab("t", string.Join("\n", Enumerable.Repeat("needle", SearchViewModel.MaxResults)))]);
        vm.Query = "needle";

        await vm.SearchAsync();

        Assert.Equal(SearchViewModel.MaxResults, vm.HitCount);
        Assert.Null(vm.Notice);
    }

    [Fact]
    public async Task Scripts_InvalidRegex_ShowsTheMessageWhileTyping_AndDoesNotSearch()
    {
        var vm = Vm([Tab("t", "text")]);
        vm.UseRegex = true;
        vm.Query = "(open";

        Assert.StartsWith("Invalid regular expression", vm.ErrorMessage);
        await vm.SearchAsync();

        Assert.StartsWith("Invalid regular expression", vm.ErrorMessage);
        Assert.Empty(vm.Rows);
        vm.Query = "o.e";
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Scripts_Options_CaseWholeWord()
    {
        var vm = Vm([Tab("t", "Order\norders\norder")]);
        vm.Query = "order";
        vm.WholeWord = true;
        await vm.SearchAsync();
        Assert.Equal(2, vm.HitCount);

        vm.CaseSensitive = true;
        await vm.SearchAsync();
        Assert.Equal(1, vm.HitCount);
    }

    [Fact]
    public async Task EmptyQuery_DoesNothing_AndSaysSo()
    {
        var vm = Vm([Tab("t", "x")]);

        await vm.SearchAsync();

        Assert.Empty(vm.Rows);
        Assert.Contains("Type something", vm.StatusText);
    }

    [Fact]
    public async Task Keyboard_ArrowsSkipHeaders_EnterOpensTheSelectedHit_AndCloses()
    {
        var opened = new List<ScriptMatch>();
        var vm = Vm([Tab("t1", "needle"), Tab("t2", "a\nneedle")], openedScripts: opened);
        var closed = 0;
        vm.CloseRequested += () => closed++;
        vm.Query = "needle";
        await vm.ConfirmAsync();   // no selection: Enter searches

        Assert.Equal(2, vm.HitCount);
        Assert.Empty(opened);
        vm.MoveDown();
        vm.MoveDown();
        Assert.Equal("t2", ((SearchHitRow)vm.SelectedRow!).Script!.DisplayName);
        vm.MoveDown();   // end of the list: stays
        vm.MoveUp();
        vm.MoveUp();
        await vm.ConfirmAsync();

        Assert.Equal(["t1"], opened.Select(m => m.DisplayName));
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task ChangingAnOption_DropsTheSelection_SoEnterSearchesAgain()
    {
        var opened = new List<ScriptMatch>();
        var vm = Vm([Tab("t", "needle")], openedScripts: opened);
        vm.Query = "needle";
        await vm.SearchAsync();
        vm.MoveDown();
        Assert.NotNull(vm.SelectedRow);

        vm.CaseSensitive = true;

        Assert.Null(vm.SelectedRow);
        await vm.ConfirmAsync();
        Assert.Empty(opened);
    }

    // ---- data scope ----

    [Fact]
    public async Task Data_FindsTheValue_GroupedByTable_AndOpeningFiltersOnThatColumnAndValue()
    {
        var opened = new List<(Session, TableRef, IReadOnlyDictionary<string, string>)>();
        var vm = Vm(openedTables: opened);
        vm.Scope = SearchScope.Data;
        vm.Query = "alice";

        await vm.SearchAsync();

        var header = Assert.Single(vm.Rows.OfType<SearchHeaderRow>());
        Assert.Equal("customers", header.Title);
        var hit = Assert.Single(vm.Rows.OfType<SearchHitRow>());
        Assert.Equal("name", hit.Badge);
        Assert.Contains("name: Alice", hit.Text);
        Assert.Equal("Alice", hit.Text.Substring(hit.MatchStart, hit.MatchLength));
        Assert.Contains("1 match in 1 table", vm.StatusText);

        vm.MoveDown();
        await vm.ConfirmAsync();

        var (_, table, filters) = Assert.Single(opened);
        Assert.Equal("customers", table.Name);
        Assert.Equal("=Alice", filters["name"]);
    }

    [Fact]
    public async Task Data_ModeAndTableFilter_AreHonoured()
    {
        var vm = Vm();
        vm.Scope = SearchScope.Data;
        vm.Query = "a";
        vm.Mode = DataMatchMode.Exact;
        await vm.SearchAsync();
        Assert.Equal(0, vm.HitCount);

        vm.Mode = DataMatchMode.StartsWith;
        vm.TableFilter = "custom";
        await vm.SearchAsync();
        Assert.Equal(1, vm.HitCount);   // Alice (Bob, Carol, Dave do not start with "a")
        Assert.DoesNotContain(vm.Rows.OfType<SearchHeaderRow>(), h => h.Title != "customers");
    }

    [Fact]
    public async Task Data_StopWhileConnecting_EndsCleanly()
    {
        var started = new TaskCompletionSource();
        var vm = Vm(connect: async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });
        vm.Scope = SearchScope.Data;
        vm.Query = "x";

        var search = vm.SearchAsync();
        await started.Task;
        Assert.True(vm.IsRunning);
        vm.StopSearch();
        await search;

        Assert.False(vm.IsRunning);
        Assert.Contains("stopped", vm.StatusText);
    }

    [Fact]
    public async Task Data_ConnectionUnavailable_SaysSo()
    {
        var vm = Vm(connect: (_, _) => Task.FromResult<Session?>(null));
        vm.Scope = SearchScope.Data;
        vm.Query = "x";

        await vm.SearchAsync();

        Assert.Contains("not available", vm.ErrorMessage);
        Assert.False(vm.IsRunning);
    }

    // ---- shell ----

    [Fact]
    public void CtrlAltF_OpensTheOverlay_AndOtherCtrlWChordsAreHeldWhileItShows()
    {
        var main = NewMain();
        AddScript(main, "x");

        Assert.True(MainShortcuts.Handle(main, Key.F, KeyModifiers.Control | KeyModifiers.Alt));
        Assert.NotNull(main.SearchOverlay);
        Assert.False(MainShortcuts.Handle(main, Key.F, KeyModifiers.Control | KeyModifiers.Shift));   // the formatter's chord is not ours
        var count = main.Tabs.Count;
        MainShortcuts.Handle(main, Key.W, KeyModifiers.Control);
        Assert.Equal(count, main.Tabs.Count);

        main.SearchOverlay!.Close();
        Assert.Null(main.SearchOverlay);
    }

    [Fact]
    public void Reopening_KeepsTheLastResults_AndPrefillsAOneLineSelection()
    {
        var main = NewMain();
        var script = AddScript(main, "select needle from t");
        main.ShowSearch(SearchScope.Data);
        var first = main.SearchOverlay!;
        Assert.Equal(SearchScope.Data, first.Scope);
        first.Close();

        script.Selection = (7, 13);
        main.ShowSearch();

        Assert.Same(first, main.SearchOverlay);
        Assert.Equal("needle", main.SearchOverlay!.Query);
        Assert.Equal(T.Config.Id, main.SearchOverlay.SelectedConnection!.Id);
    }

    [Fact]
    public async Task OpeningATabHit_ActivatesTheTab_AndSelectsTheMatch()
    {
        var main = NewMain();
        var script = AddScript(main, "alpha\r\nbeta needle\r\ngamma");
        var other = AddScript(main, "unrelated");
        Assert.Same(other, main.ActiveTab);
        main.ShowSearch(SearchScope.Scripts);
        var search = main.SearchOverlay!;
        search.Query = "needle";
        await search.SearchAsync();

        search.MoveDown();
        await search.ConfirmAsync();

        Assert.Same(script, main.ActiveTab);
        Assert.Equal((12, 18), script.TakePendingReveal());   // "alpha\r\n" = 7, "beta " = 5
        Assert.Null(main.SearchOverlay);
    }

    [Fact]
    public async Task OpeningALibraryFileHit_OpensItInATab_AndSelectsTheMatch()
    {
        var main = NewMain();
        var lib = new ScriptLibrary(Path.Combine(T.Fx.Dir, "lib"));
        lib.EnsureRoot();
        File.WriteAllText(Path.Combine(lib.Root, "report.sql"), "-- title\nselect needle from t\n");
        main.UseScriptLibrary(lib, new RecentScripts(Path.Combine(T.Fx.Dir, "recent.json")));
        main.ShowSearch(SearchScope.Scripts);
        var search = main.SearchOverlay!;
        search.Query = "needle";
        await search.SearchAsync();

        search.MoveDown();
        await search.ConfirmAsync();

        var tab = Assert.IsType<ScriptTabViewModel>(main.ActiveTab);
        Assert.Equal(Path.Combine(lib.Root, "report.sql"), tab.FilePath);
        Assert.Equal((16, 22), tab.TakePendingReveal());
    }

    [Fact]
    public void RevealLine_RaisesTheEvent_AndClampsToTheText()
    {
        var main = NewMain();
        var script = AddScript(main, "ab\ncd");
        var seen = new List<(int, int)>();
        script.RevealRequested += (s, e) => seen.Add((s, e));

        script.RevealLine(2, 1, 50);
        script.RevealLine(99, 0, 1);

        Assert.Equal([(4, 5), (5, 5)], seen);
    }

    // ---- view ----

    [AvaloniaFact]
    public async Task View_ShowsGroupedResults_AndTheKeysDriveIt()
    {
        var opened = new List<ScriptMatch>();
        var vm = Vm([Tab("Script 1", "one needle\ntwo needle")], openedScripts: opened);
        var window = new Window { Width = 900, Height = 700, Content = new SearchOverlay { DataContext = vm } };
        window.Show();
        Pump();

        vm.Query = "needle";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);   // goes to the focused search box
        Pump();
        await Task.Delay(100);
        Pump();

        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ResultList");
        Assert.Equal(3, list.ItemCount);   // header + two hits
        Assert.Equal(2, vm.HitCount);

        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Pump();
        Assert.Same(vm.SelectedRow, list.SelectedItem);
        Assert.IsType<SearchHitRow>(list.SelectedItem);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Pump();
        await Task.Delay(50);
        Assert.Single(opened);
        window.Close();
    }

    [AvaloniaFact]
    public void View_DataScope_ExplainsThatItCanTakeAWhile_AndEscapeCloses()
    {
        var vm = Vm();
        var window = new Window { Width = 900, Height = 700, Content = new SearchOverlay { DataContext = vm } };
        window.Show();
        vm.Scope = SearchScope.Data;
        Pump();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains(texts, t => t is not null && t.Contains("can take a while") && t.Contains("Stop"));

        var closed = false;
        vm.CloseRequested += () => closed = true;
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Pump();
        Assert.True(closed);
        window.Close();
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
