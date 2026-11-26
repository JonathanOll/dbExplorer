using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.Tests.Upgrade;

/// <summary>The search by file name and the "long scripts" toggle of the preview.</summary>
public class UpgradeScriptListTests : IDisposable
{
    private const string Loop = "zzz - 001 - Loop.sql";
    private const string ProductionLoop = "zzz - 002 - Loop - Production2.sql";

    private readonly UpgradeFixture _f = new();
    private readonly TimeSpan _grace = UpgradeViewModel.CancelGrace;

    public void Dispose()
    {
        UpgradeViewModel.CancelGrace = _grace;
        _f.Dispose();
    }

    private void AddLongScripts()
    {
        _f.Tree.Add(UpgradeFixture.V0860, Loop, "WHILE 1 = 0 SELECT 1");
        _f.Tree.Add("v0861-sprint 143", ProductionLoop, "WHILE 1 = 0 SELECT 1");
    }

    private async Task<UpgradeViewModel> Loaded(bool withLong = true)
    {
        if (withLong) AddLongScripts();
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);
        vm.SprintText = "143";
        await vm.LoadCommand.ExecuteAsync(null);
        return vm;
    }

    private static IEnumerable<string> Shown(UpgradeViewModel vm) => vm.VisibleGroups.SelectMany(g => g.Rows).Select(r => r.Name);

    private static UpgradeScriptRow Row(UpgradeViewModel vm, string name) => vm.Rows.Single(r => r.Name == name);

    [Fact]
    public async Task Search_MatchesPartOfTheFileName_IgnoringCase_AndDropsEmptyFolders()
    {
        var vm = await Loaded();

        vm.FilterText = "lOOp";

        Assert.Equal([Loop, ProductionLoop], Shown(vm));
        Assert.Equal(["v0860-sprint 143", "v0861-sprint 143"], vm.VisibleGroups.Select(g => g.Folder));
        Assert.Equal($"2 of {vm.Rows.Count} shown", vm.ShownText);
        Assert.False(vm.HasNoMatch);
    }

    [Fact]
    public async Task Search_DoesNotLookAtTheFolderName()
    {
        var vm = await Loaded();

        vm.FilterText = "sprint";

        Assert.Empty(Shown(vm));
        Assert.True(vm.HasNoMatch);
    }

    [Fact]
    public async Task Search_ChangesNeitherTheTicksNorWhatRuns()
    {
        var vm = await Loaded();
        var ticked = vm.Rows.Where(r => r.IsSelected).ToList();
        var text = vm.TickedText;
        var all = vm.Rows.Count;

        vm.FilterText = "no such file";

        Assert.True(vm.HasNoMatch);
        Assert.Equal(all, vm.Rows.Count);
        Assert.Equal(ticked, vm.Rows.Where(r => r.IsSelected));
        Assert.Equal(text, vm.TickedText);
        Assert.True(vm.RequestRunCommand.CanExecute(null));
        vm.RequestRunCommand.Execute(null);
        Assert.Equal($"Upgrade {ticked.Count} scripts on:", vm.ConfirmHeading);
    }

    [Fact]
    public async Task ClearingTheSearch_ShowsEveryScriptAgain()
    {
        var vm = await Loaded();
        Assert.False(vm.ClearFilterCommand.CanExecute(null));
        vm.FilterText = "loop";

        vm.ClearFilterCommand.Execute(null);

        Assert.Equal("", vm.FilterText);
        Assert.Equal(vm.Rows.Select(r => r.Name), Shown(vm));
        Assert.False(vm.IsFiltering);
    }

    [Fact]
    public async Task LoadingAgain_ResetsTheSearch()
    {
        var vm = await Loaded();
        vm.FilterText = "loop";

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal("", vm.FilterText);
        Assert.Equal(vm.Rows.Count, Shown(vm).Count());
    }

    [Fact]
    public async Task LongToggle_UnticksEveryLongScript_ThenTicksThemAll_HiddenOnesIncluded()
    {
        var vm = await Loaded();
        var others = vm.Rows.Where(r => !r.IsLongRunning).Select(r => r.IsSelected).ToList();
        vm.FilterText = "adj";   // both long scripts are hidden
        Assert.True(Row(vm, Loop).IsSelected);
        Assert.False(Row(vm, ProductionLoop).IsSelected);   // Production starts unticked
        Assert.Equal("Untick long (2)", vm.LongToggleText);

        vm.ToggleLongCommand.Execute(null);

        Assert.False(Row(vm, Loop).IsSelected);
        Assert.False(Row(vm, ProductionLoop).IsSelected);
        Assert.Equal("Tick long (2)", vm.LongToggleText);

        vm.ToggleLongCommand.Execute(null);

        Assert.True(Row(vm, Loop).IsSelected);
        Assert.True(Row(vm, ProductionLoop).IsSelected);
        Assert.Equal("Untick long (2)", vm.LongToggleText);
        Assert.Contains("hidden by the search", vm.LongToggleTip);
        Assert.Equal(others, vm.Rows.Where(r => !r.IsLongRunning).Select(r => r.IsSelected));   // an index script is not "long"
    }

    [Fact]
    public async Task LongToggle_IsOffWithoutALongScript_AndSaysWhy()
    {
        var vm = await Loaded(withLong: false);

        Assert.False(vm.ToggleLongCommand.CanExecute(null));
        Assert.Equal("Tick long", vm.LongToggleText);
        Assert.Equal("No long-running script in this list.", vm.LongToggleTip);
    }

    [Fact]
    public async Task LongToggle_IsOffWhileRunning_AndSaysWhy()
    {
        UpgradeViewModel.CancelGrace = TimeSpan.FromMilliseconds(100);
        _f.Override = (_, _) => new TaskCompletionSource<Session?>().Task;   // never connects
        var vm = await Loaded();
        Assert.True(vm.ToggleLongCommand.CanExecute(null));
        vm.RequestRunCommand.Execute(null);
        var run = vm.ConfirmRunCommand.ExecuteAsync(null);
        Assert.Equal(UpgradePhase.Running, vm.Phase);

        Assert.False(vm.ToggleLongCommand.CanExecute(null));
        Assert.Equal("The scripts cannot be changed while a run is in progress.", vm.LongToggleTip);

        vm.CancelAllCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [AvaloniaFact]
    public async Task Dialog_SearchHidesRows_ShowsNoMatch_AndEscClearsBeforeItCloses()
    {
        var vm = await Loaded();
        var window = new UpgradeDialog { DataContext = vm };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        bool Shows(string text) => window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);
        var box = window.FindControl<TextBox>("FilterBox")!;
        Assert.True(Shows(Loop));

        box.Text = "adj - 2";
        Dispatcher.UIThread.RunJobs();
        Assert.True(Shows("adj - 2 - x.sql"));
        Assert.False(Shows(Loop));

        box.Text = "nothing like this";
        Dispatcher.UIThread.RunJobs();
        Assert.True(Shows("No match"));

        box.Focus();
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", vm.FilterText);
        Assert.False(closed);
        Assert.True(Shows(Loop));
        Assert.False(Shows("No match"));

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(closed);
    }
}
