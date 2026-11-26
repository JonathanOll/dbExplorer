using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.Settings;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Export;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests;

public class CommandPaletteTests
{
    private static AppCommand Command(string id, string title, string category = "File", string? disabled = null,
        string? keywords = null, Func<Task>? run = null) =>
        new(id, category, title) { DisabledReason = disabled, Keywords = keywords, Run = run ?? (() => Task.CompletedTask) };

    private static readonly AppCommand[] Sample =
    [
        Command("script.run", "Run statement", "Script", disabled: "Open a script tab first"),
        Command("file.new", "New SQL script"),
        Command("tab.close", "Close tab", "Tabs"),
        Command("appearance.theme", "Change theme…", "Appearance", keywords: "dark light"),
        Command("table.count", "Count rows", "Table"),
    ];

    private static string[] Ids(CommandPaletteViewModel vm) => vm.Rows.Select(r => r.Command.Id).ToArray();

    [Fact]
    public void NoQuery_ListsRecentFirst_ThenContext_ThenUnavailable()
    {
        var vm = new CommandPaletteViewModel(Sample, ["tab.close", "gone", "script.run"], ["Table"]);

        Assert.Equal(["tab.close", "table.count", "file.new", "appearance.theme", "script.run"], Ids(vm));
        Assert.Equal(CommandPaletteViewModel.RecentHeader, vm.Rows[0].Header);
        Assert.Equal(CommandPaletteViewModel.AvailableHeader, vm.Rows[1].Header);
        Assert.Null(vm.Rows[2].Header);
        Assert.Equal(CommandPaletteViewModel.UnavailableHeader, vm.Rows[4].Header);
        Assert.Equal("Open a script tab first", vm.Rows[4].Note);
        Assert.Equal(0, vm.SelectedIndex);
    }

    [Theory]
    [InlineData("close", "tab.close")]
    [InlineData("nsq", "file.new")]
    [InlineData("clsoe tab", "tab.close")]      // two letters swapped
    [InlineData("statment", "script.run")]      // a letter missing
    [InlineData("tab close", "tab.close")]      // words in any order
    [InlineData("dark", "appearance.theme")]    // keyword
    [InlineData("table", "table.count")]        // category
    public void Query_FindsTheCommand(string query, string expected)
    {
        var vm = new CommandPaletteViewModel(Sample) { Query = query };

        Assert.Equal(expected, Ids(vm).FirstOrDefault());
    }

    [Fact]
    public void WhatCanRun_ComesBeforeAGreyedScatteredMatch_ButNotBeforeAGreyedExactOne()
    {
        AppCommand[] commands = [Command("other", "Close other tabs", "Tabs", "There is no other tab"), Command("close", "Close tab", "Tabs")];

        Assert.Equal(["close", "other"], Ids(new CommandPaletteViewModel(commands) { Query = "clsoe" }));
        Assert.Equal("other", Ids(new CommandPaletteViewModel(commands) { Query = "other" })[0]);
    }

    [Fact]
    public void Query_WithoutMatch_SaysSo()
    {
        var vm = new CommandPaletteViewModel(Sample) { Query = "zzzzqq" };

        Assert.True(vm.IsEmpty);
        Assert.Equal(-1, vm.SelectedIndex);
        Assert.Contains("zzzzqq", vm.EmptyText);
    }

    [Fact]
    public void Query_RanksARecentCommandAboveAnEqualOne()
    {
        AppCommand[] twins = [Command("a", "Close one"), Command("b", "Close two")];

        Assert.Equal(["b", "a"], Ids(new CommandPaletteViewModel(twins, ["b"]) { Query = "close" }));
    }

    [Theory]
    [InlineData("Close tab", "tab", new[] { 6, 7, 8 })]
    [InlineData("New SQL script", "nss", new[] { 0, 4, 8 })]
    [InlineData("Close tab", "tab close", new[] { 0, 1, 2, 3, 4, 6, 7, 8 })]
    public void Positions_MarkWhatWasTyped(string title, string query, int[] expected) =>
        Assert.Equal(expected, CommandMatcher.Positions(title, query));

    [Fact]
    public void Positions_MarkMostLettersOfATypo() =>
        Assert.True(CommandMatcher.Positions("Close tab", "clsoe").Count >= 3);

    [Fact]
    public async Task DisabledCommand_DoesNothing_AndKeepsThePaletteOpen()
    {
        var ran = false;
        var vm = new CommandPaletteViewModel([Command("x", "Nope", disabled: "Because", run: () => { ran = true; return Task.CompletedTask; })]);
        var closed = false;
        vm.CloseRequested += () => closed = true;

        await vm.ConfirmAsync();

        Assert.False(ran);
        Assert.False(closed);
    }

    [Fact]
    public async Task Run_ClosesFirst_RemembersTheCommand_AndReportsFailures()
    {
        var order = new List<string>();
        var status = new StatusModel();
        var vm = new CommandPaletteViewModel(
            [Command("x", "Explode", run: () => { order.Add("run"); throw new InvalidOperationException("boom"); })],
            used: id => order.Add("used " + id), status: status);
        vm.CloseRequested += () => order.Add("close");

        await vm.ConfirmAsync();

        Assert.Equal(["close", "used x", "run"], order);
        Assert.Contains("boom", status.Message);
    }

    [Fact]
    public async Task Choices_OpenASecondStep_BackRestores_AndTheParentIsRemembered()
    {
        var chosen = "";
        var used = new List<string>();
        var parent = new AppCommand("connect", "Connection", "Connect to…")
        {
            Choices = () => [Command("connect.a", "Alpha", run: () => { chosen = "a"; return Task.CompletedTask; }), Command("connect.b", "Beta")],
        };
        var vm = new CommandPaletteViewModel([Command("other", "Other"), parent], used: used.Add) { Query = "conn" };

        await vm.ConfirmAsync();

        Assert.True(vm.InStep);
        Assert.Equal("Connect to", vm.StepTitle);
        Assert.Equal("", vm.Query);
        Assert.Equal(["connect.a", "connect.b"], Ids(vm));

        Assert.True(vm.Back());
        Assert.False(vm.InStep);
        Assert.Equal("conn", vm.Query);
        Assert.Equal("connect", vm.SelectedRow!.Command.Id);
        Assert.False(vm.Back());

        await vm.ConfirmAsync();
        vm.Query = "alp";
        await vm.ConfirmAsync();

        Assert.Equal("a", chosen);
        Assert.Equal(["connect"], used);
    }

    [Fact]
    public void Selection_WrapsWithArrows_AndStopsAtTheEndsByPage()
    {
        var vm = new CommandPaletteViewModel(Sample);

        vm.MoveUp();
        Assert.Equal(4, vm.SelectedIndex);
        vm.MoveDown();
        Assert.Equal(0, vm.SelectedIndex);
        vm.MoveBy(99);
        Assert.Equal(4, vm.SelectedIndex);
        vm.MoveFirst();
        Assert.Equal(0, vm.SelectedIndex);
        vm.MoveLast();
        Assert.Equal(4, vm.SelectedIndex);
    }

    [Fact]
    public void Registry_KeepsTheFirstOfARepeatedId_AndAsksProvidersEveryTime()
    {
        var calls = 0;
        var registry = new CommandRegistry();
        registry.Add(() => { calls++; return [Command("a", "First")]; });
        registry.Add(() => [Command("a", "Second"), Command("b", "Third")]);

        Assert.Equal(["First", "Third"], registry.Snapshot().Select(c => c.Title));
        registry.Snapshot();
        Assert.Equal(2, calls);
    }

    [Fact]
    public void RecentCommands_AreSavedAndLoaded_AndOldFilesHaveNone()
    {
        using var fx = new TreeFixture();
        var tree = fx.CreateTree(AppConfig.Empty());

        tree.AddRecentCommand("a");
        tree.AddRecentCommand("b");
        tree.AddRecentCommand("a");

        Assert.Equal(["a", "b"], fx.Store.Load().RecentCommands);

        for (var i = 0; i < AppConfig.MaxRecentCommands + 5; i++) tree.AddRecentCommand("c" + i);
        Assert.Equal(AppConfig.MaxRecentCommands, fx.Store.Load().RecentCommands.Count);

        File.WriteAllText(fx.Store.FilePath, """{ "Folders": [], "Connections": [], "VirtualKeys": {} }""");
        Assert.Empty(fx.Store.Load().RecentCommands);
    }
}

public class CommandPaletteShellTests : ShellTestBase
{
    private static AppCommand Find(MainViewModel main, string id) => main.Registry.Snapshot().Single(c => c.Id == id);

    [Fact]
    public async Task Commands_FollowTheCurrentTab()
    {
        var main = NewMain();
        Assert.Equal("Open a script tab first", Find(main, "script.run-all").DisabledReason);
        Assert.Equal("Open a table first", Find(main, "table.count").DisabledReason);

        AddScript(main, "select 1");
        Assert.True(Find(main, "script.run-all").IsEnabled);
        Assert.Equal("Show an execution plan first", Find(main, "plan.zoom-in").DisabledReason);

        await AddTable(main, "users");
        Assert.True(Find(main, "table.count").IsEnabled);
        Assert.False(Find(main, "table.revert").IsEnabled);
        main.ShowCommandPalette();
        Assert.Equal("Table", main.CommandPalette!.Rows[0].Category);
    }

    private sealed class FakeGrid : IGridCommands
    {
        public List<string> Calls { get; } = [];
        public bool HasCurrentCellOrSelection { get; set; } = true;
        public bool HasExportDialogs { get; set; } = true;
        public bool HasKeyColumns { get; set; } = true;

        public Task CopyAsync(bool header) => Add(header ? "copy+headers" : "copy");
        public Task CopyAsAsync(ExportKind kind) => Add("as " + kind);
        public Task AdvancedCopyAsync() => Add("advanced");
        public Task ExportAsync() => Add("export");

        private Task Add(string call)
        {
            Calls.Add(call);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task GridCommands_RunOnTheActiveGrid_WithTheirShortcuts()
    {
        var main = NewMain();
        var grid = new FakeGrid();
        main.FindActiveGrid = () => grid;
        await AddTable(main, "users");

        foreach (var (id, shortcut) in new[]
                 {
                     ("grid.copy", "Ctrl+C"), ("grid.copy-headers", "Ctrl+Shift+C"), ("grid.advanced-copy", "Ctrl+Alt+C"),
                     ("grid.export", "Ctrl+E"),
                 })
        {
            var command = Find(main, id);
            Assert.Equal(shortcut, command.Shortcut);
            Assert.True(command.IsEnabled, id);
            await command.Run!();
        }
        var formats = Find(main, "grid.copy-as").Choices!();
        Assert.Equal(ResultFormats.CopyAs.Count, formats.Count);
        await formats.Single(c => c.Id == "grid.copy-as.Json").Run!();

        Assert.Equal(["copy", "copy+headers", "advanced", "export", "as Json"], grid.Calls);
    }

    [Fact]
    public async Task GridCommands_SayWhyTheyAreDisabled()
    {
        var main = NewMain();
        Assert.Equal("Not available here", Find(main, "grid.copy").DisabledReason);

        var grid = new FakeGrid { HasCurrentCellOrSelection = false, HasExportDialogs = false, HasKeyColumns = false };
        main.FindActiveGrid = () => grid;
        Assert.Equal("Show a table or a query result first", Find(main, "grid.export").DisabledReason);   // no tab

        await AddTable(main, "users");
        Assert.Equal("Select cells or rows first", Find(main, "grid.copy").DisabledReason);
        Assert.True(Find(main, "grid.copy-headers").IsEnabled);
        Assert.Equal("Not available for this grid", Find(main, "grid.advanced-copy").DisabledReason);
        Assert.Equal("Not available for this grid", Find(main, "grid.export").DisabledReason);
        var update = Find(main, "grid.copy-as").Choices!().Single(c => c.Id == "grid.copy-as.SqlUpdate");
        Assert.Equal(ResultFormats.NoKeyReason, update.DisabledReason);

        main.FindActiveGrid = () => null;
        Assert.Equal("Show a table or a query result first", Find(main, "grid.copy-as").DisabledReason);
    }

    [AvaloniaFact]
    public async Task TheWindow_GivesThePaletteTheGridOnScreen()
    {
        var main = NewMain();
        var window = new MainWindow { DataContext = main, Width = 1200, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(Find(main, "grid.export").IsEnabled);

        await AddTable(main, "customers");
        for (var i = 0; i < 100 && !Find(main, "grid.copy-headers").IsEnabled; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }

        Assert.True(Find(main, "grid.copy-headers").IsEnabled);
        window.Close();
    }

    [Fact]
    public void EveryCommand_HasAnId_ATitle_ACategory_AndItsChoicesBuild()
    {
        var main = NewMain();
        AddScript(main, "select 1");
        var commands = main.Registry.Snapshot();

        Assert.All(commands, c =>
        {
            Assert.NotEmpty(c.Id);
            Assert.NotEmpty(c.Title);
            Assert.NotEmpty(c.Category);
            Assert.True(c.Run is not null || c.Choices is not null, c.Id);
        });
        foreach (var c in commands.Where(c => c is { HasChoices: true, IsEnabled: true })) Assert.NotEmpty(c.Choices!());
    }

    [Fact]
    public void SettingsPageChoices_MatchTheSettingsDialog()
    {
        var context = new SettingsContext(new SettingsService(new AppSettings()))
        {
            Upgrade = new OptionsViewModel(null, _ => null),
            AutoUpdateCheck = new(() => true, _ => { }),
            RecordHistory = new(() => true, _ => { }),
            GetFormat = () => new(),
            SetFormat = _ => { },
            ShowFolders = new(() => true, _ => { }),
            MonospaceFonts = ["Consolas"],
        };

        Assert.Equal(SettingsPages.Create(context).Select(p => (p.Id, p.Title)),
            MainViewModel.SettingsPageChoices.Select(p => (p.Id, p.Title)));
    }

    [Fact]
    public async Task ConnectTo_ListsTheConnections_AndConnects()
    {
        var main = NewMain();
        main.ShowCommandPalette();
        var palette = main.CommandPalette!;
        palette.Query = "connect to";
        Assert.Equal("connection.connect", palette.SelectedRow!.Command.Id);

        await palette.ConfirmAsync();
        Assert.Equal(T.Config.Name, Assert.Single(palette.Rows).Title);
        await palette.ConfirmAsync();

        Assert.Null(main.CommandPalette);
        Assert.NotNull(main.Tree.FindSession(T.Config.Id));
        Assert.Equal(["connection.connect"], main.Tree.Config.RecentCommands);
    }

    [Fact]
    public void Shortcut_TogglesThePalette_AndAnotherOverlayIsReplaced()
    {
        var main = NewMain();
        main.ShowShortcuts();

        Assert.True(MainShortcuts.Handle(main, Key.K, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.NotNull(main.CommandPalette);
        Assert.False(main.IsShortcutsVisible);
        Assert.True(MainShortcuts.Handle(main, Key.K, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Null(main.CommandPalette);

        // a refused cell edit keeps the palette closed
        Assert.True(MainShortcuts.Handle(main, Key.K, KeyModifiers.Control | KeyModifiers.Shift, () => false));
        Assert.Null(main.CommandPalette);
    }

    [Fact]
    public void Shortcut_IsListedInTheHelp_AndIsFreeInTheEditor()
    {
        Assert.Contains(ShortcutCatalog.Sections.SelectMany(s => s.Items), i => i.Keys == "Ctrl+Shift+K");
        Assert.Equal(ScriptKeyAction.None, ScriptTabKeys.Resolve(Key.K, KeyModifiers.Control | KeyModifiers.Shift));
    }

    [AvaloniaFact]
    public async Task Overlay_IsDrivenByTheKeyboard_AndGivesTheFocusBack()
    {
        var main = NewMain();
        var window = new MainWindow { DataContext = main };
        window.Show();
        var filter = window.FindControl<TextBox>("FilterBox")!;
        filter.Focus();
        Settle();

        window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Control | RawInputModifiers.Shift);
        Settle();
        var overlay = Assert.Single(window.GetVisualDescendants().OfType<CommandPaletteOverlay>());
        var box = overlay.FindControl<TextBox>("QueryBox")!;
        Assert.True(box.IsFocused);

        window.KeyTextInput("change theme");
        Settle();
        var palette = main.CommandPalette!;
        Assert.Equal("appearance.theme", palette.SelectedRow!.Command.Id);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Settle();
        Assert.True(palette.InStep);
        Assert.Equal("", box.Text);
        window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
        Assert.Equal(palette.Rows.Count - 1, palette.SelectedIndex);
        window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
        Assert.Equal(0, palette.SelectedIndex);
        window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        Settle();
        Assert.False(palette.InStep);
        Assert.Equal("change theme", box.Text);
        Assert.True(box.IsFocused);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await Task.Yield();
        Settle();
        Assert.Null(main.CommandPalette);
        Assert.True(filter.IsFocused);
        window.Close();

        static void Settle()
        {
            for (var i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();
        }
    }
}
