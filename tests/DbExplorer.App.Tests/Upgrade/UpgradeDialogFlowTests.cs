using DbExplorer.App.ViewModels;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Sessions;
using DbExplorer.Core.Upgrade;

namespace DbExplorer.App.Tests.Upgrade;

/// <summary>The view-model state behind the restyled Upgrade dialog: steps, reasons, bulk ticks, stop confirmation, outcome.</summary>
public class UpgradeDialogFlowTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    private readonly UpgradeFixture _f = new();
    private readonly TimeSpan _grace = UpgradeViewModel.CancelGrace;

    public void Dispose()
    {
        UpgradeViewModel.CancelGrace = _grace;
        _f.Dispose();
    }

    private static async Task Load(UpgradeViewModel vm, string sprint = "143")
    {
        vm.SprintText = sprint;
        await vm.LoadCommand.ExecuteAsync(null);
    }

    private async Task<UpgradeViewModel> InConfirm(params UpgradeTarget[] targets)
    {
        var vm = _f.Vm(targets);
        await Load(vm);
        vm.RequestRunCommand.Execute(null);
        Assert.Equal(UpgradePhase.Confirm, vm.Phase);
        return vm;
    }

    // ---- steps ----

    [Fact]
    public async Task Steps_FollowThePhase()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);
        Assert.True(vm.InChoose);
        Assert.False(vm.ChooseDone);

        await Load(vm);
        Assert.True(vm.InChoose);

        vm.RequestRunCommand.Execute(null);
        Assert.True(vm.InConfirm);
        Assert.True(vm.ChooseDone);
        Assert.False(vm.ConfirmDone);
        Assert.True(vm.CanGoBack);

        await vm.ConfirmRunCommand.ExecuteAsync(null).WaitAsync(Bound);
        Assert.True(vm.InRun);
        Assert.True(vm.ConfirmDone);
        Assert.True(vm.RunDone);
        Assert.True(vm.ShowCopyLog);
        Assert.False(vm.CanGoBack);
    }

    // ---- why the primary button is off ----

    [Fact]
    public async Task RunDisabledReason_SaysWhy_InPreview_ThenInConfirm()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A")), UpgradeFixture.Group("G", _f.Db("B"), _f.Db("C"))]);
        Assert.Null(vm.RunDisabledReason);   // not in Preview yet

        await Load(vm);
        Assert.Null(vm.RunDisabledReason);
        Assert.False(vm.ShowRunReason);

        vm.SelectNoneCommand.Execute(null);
        Assert.Equal("Tick at least one script to continue.", vm.RunDisabledReason);
        Assert.True(vm.ShowRunReason);
        Assert.False(vm.RequestRunCommand.CanExecute(null));

        vm.SelectNonProductionCommand.Execute(null);
        Assert.Null(vm.RunDisabledReason);

        vm.SelectedTarget = vm.Targets[1];
        vm.RequestRunCommand.Execute(null);
        Assert.Equal("Type 143 in the box to enable the run.", vm.RunDisabledReason);
        vm.ConfirmInput = "143";
        Assert.Null(vm.RunDisabledReason);
        Assert.True(vm.ConfirmMatches);
    }

    // ---- bulk ticks and the list ----

    [Fact]
    public async Task BulkTicks_AllNoneNonProduction_AndTheCount()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);
        await Load(vm);
        Assert.Equal("9 of 10 scripts ticked", vm.TickedText);   // Production scripts start unticked

        vm.SelectNoneCommand.Execute(null);
        Assert.Equal(0, vm.SelectedCount);
        Assert.Equal("0 of 10 scripts ticked", vm.TickedText);

        vm.SelectAllCommand.Execute(null);
        Assert.Equal(10, vm.SelectedCount);
        Assert.Equal(1, vm.ProductionSelectedCount);

        vm.SelectNonProductionCommand.Execute(null);
        Assert.Equal(9, vm.SelectedCount);
        Assert.Equal(0, vm.ProductionSelectedCount);
    }

    [Fact]
    public async Task Groups_ListTheScriptsByFolder()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);
        await Load(vm);

        Assert.Equal(["v0860-sprint 143", "v0861-sprint 143", "v0862-sprint 143"], vm.Groups.Select(g => g.Folder));
        Assert.Equal(vm.Rows.Count, vm.Groups.Sum(g => g.Rows.Count));
        Assert.Equal("Sprint 143", vm.PlanLabel);
    }

    [Fact]
    public void SprintHint_IsTheNewestSprintOfTheCatalog()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);

        Assert.Equal("143", vm.SprintHint);
    }

    // ---- the run button says what it does ----

    [Fact]
    public async Task RunButtonText_NamesTheScriptsAndTheDatabases_AndTurnsDangerousForAGroup()
    {
        var single = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);
        await Load(single);
        Assert.Equal("Run 9 scripts on 1 database", single.RunButtonText);
        Assert.False(single.IsDangerRun);

        var group = await InConfirm(UpgradeFixture.Group("G", _f.Db("B"), _f.Db("C")));
        Assert.Equal("Run 9 scripts on 2 databases", group.RunButtonText);
        Assert.True(group.IsDangerRun);

        var withProduction = _f.Vm([UpgradeFixture.One(_f.Db("D"))]);
        await Load(withProduction);
        withProduction.SelectAllCommand.Execute(null);
        Assert.True(withProduction.IsDangerRun);   // a Production script is ticked
    }

    // ---- stopping a run asks first ----

    [Fact]
    public async Task RequestStop_Asks_KeepRunningResumes_ConfirmStopCancelsEverything()
    {
        UpgradeViewModel.CancelGrace = TimeSpan.FromMilliseconds(100);
        _f.Override = (_, _) => new TaskCompletionSource<Session?>().Task;   // never connects
        var vm = await InConfirm(UpgradeFixture.Group("G", _f.Db("A"), _f.Db("B")));
        vm.ConfirmInput = "143";
        var run = vm.ConfirmRunCommand.ExecuteAsync(null);
        Assert.Equal(UpgradePhase.Running, vm.Phase);
        Assert.True(vm.ShowStopButton);

        vm.RequestStopCommand.Execute(null);
        Assert.True(vm.ConfirmingStop);
        Assert.False(vm.ShowStopButton);
        Assert.False(run.IsCompleted);                          // asked, nothing interrupted

        vm.KeepRunningCommand.Execute(null);
        Assert.False(vm.ConfirmingStop);
        Assert.True(vm.ShowStopButton);
        Assert.False(run.IsCompleted);

        var sawStopping = false;
        vm.PropertyChanged += (_, e) => sawStopping |= e.PropertyName == nameof(UpgradeViewModel.IsStopping) && vm.IsStopping;
        vm.RequestStopCommand.Execute(null);
        vm.ConfirmStopCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.True(sawStopping);   // "Stopping" showed while the databases were being interrupted

        Assert.Equal(UpgradePhase.Finished, vm.Phase);
        Assert.False(vm.ConfirmingStop);
        Assert.False(vm.IsStopping);
        Assert.Equal(UpgradeOutcome.Cancelled, vm.Outcome);
        Assert.True(vm.OutcomeIsWarn);
    }

    // ---- outcome, progress, details ----

    [Fact]
    public async Task Outcome_SuccessIsGreen_APartialFailureIsAWarning_AllFailedIsRed()
    {
        var ok = await InConfirm(UpgradeFixture.One(_f.Db("A")));
        await ok.ConfirmRunCommand.ExecuteAsync(null).WaitAsync(Bound);
        Assert.Equal(UpgradeOutcome.Success, ok.Outcome);
        Assert.True(ok.OutcomeIsSuccess);
        Assert.False(ok.OutcomeIsWarn || ok.OutcomeIsError);

        var partial = await InConfirm(UpgradeFixture.Group("G", _f.Db("B"), _f.Db("Broken", withTable: false)));
        partial.ConfirmInput = "143";
        await partial.ConfirmRunCommand.ExecuteAsync(null).WaitAsync(Bound);
        Assert.Equal(UpgradeOutcome.Partial, partial.Outcome);
        Assert.True(partial.OutcomeIsWarn);
        Assert.False(partial.OutcomeIsSuccess);

        var failed = await InConfirm(UpgradeFixture.One(_f.Db("Broken2", withTable: false)));
        await failed.ConfirmRunCommand.ExecuteAsync(null).WaitAsync(Bound);
        Assert.Equal(UpgradeOutcome.Failed, failed.Outcome);
        Assert.True(failed.OutcomeIsError);
    }

    [Fact]
    public async Task FinishedMembers_KeepTheirScriptLines_AndTheOverallProgressIsFull()
    {
        var vm = await InConfirm(UpgradeFixture.One(_f.Db("Broken", withTable: false)));
        await vm.ConfirmRunCommand.ExecuteAsync(null).WaitAsync(Bound);

        var member = vm.Members.Single();
        Assert.True(member.HasError);
        Assert.StartsWith("failed at", member.ErrorText);
        Assert.True(member.HasDetails);
        Assert.Contains(member.ScriptLines, l => l.IsFailed);
        Assert.Contains(member.ScriptLines, l => l.IsOther);   // the scripts after the failure were not run
        Assert.Equal("1 of 1 database finished", vm.OverallText);
        Assert.Equal(1.0, vm.OverallProgress);
        Assert.Matches(@"^\d\d:\d\d$", vm.ElapsedText);
    }

    [Fact]
    public async Task DoneMember_ShowsItsSummary_AndTheDetailsAreClosedByDefault()
    {
        var vm = await InConfirm(UpgradeFixture.One(_f.Db("A")));
        await vm.ConfirmRunCommand.ExecuteAsync(null).WaitAsync(Bound);

        var member = vm.Members.Single();
        Assert.StartsWith("9 scripts · ", member.DoneSummary);
        Assert.Equal("Done", member.StateLabel);
        Assert.Equal(1.0, member.ProgressValue);
        Assert.False(member.IsDetailsOpen);
        Assert.Equal(9, member.ScriptLines.Count(l => l.IsDone));
    }

    [Fact]
    public void RunningMember_ReportsItsProgressThroughTheScripts()
    {
        var db = _f.Db("A");
        var member = new UpgradeMemberViewModel(db, "A", _ => { });

        member.Apply(new UpgradeProgress(db.Id, MemberState.Running, 3, 12, "adj - 003.sql", null));

        Assert.Equal("script 3 of 12", member.ProgressText);
        Assert.Equal(2.0 / 12, member.ProgressValue, 6);
        Assert.Equal("adj - 003.sql", member.CurrentScript);
        Assert.False(member.IsIndeterminate);

        var early = new UpgradeMemberViewModel(db, "A", _ => { });
        early.Apply(new UpgradeProgress(db.Id, MemberState.Running, 0, 0, null, null));
        Assert.True(early.IsIndeterminate);
        Assert.Equal("", early.ProgressText);
    }
}
