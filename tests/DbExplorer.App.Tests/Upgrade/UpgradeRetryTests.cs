using DbExplorer.App.ViewModels;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace DbExplorer.App.Tests.Upgrade;

/// <summary>"Retry failed" of the Upgrade dialog: only the failed databases, only what did not finish there, confirmed again.</summary>
public class UpgradeRetryTests : IDisposable
{
    private static readonly long[] Expected143 = [2, 3, 10, 37, 101, 102, 4];
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    private readonly UpgradeFixture _f = new();
    private readonly TimeSpan _grace = UpgradeViewModel.CancelGrace;

    public void Dispose()
    {
        UpgradeViewModel.CancelGrace = _grace;
        _f.Dispose();
    }

    private static void Exec(ConnectionConfig db, string sql)
    {
        using var c = new SqliteConnection($"Data Source={db.Database};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>A database whose script that inserts <paramref name="value"/> fails (a row with it is already there under a unique index).</summary>
    private static void Trap(ConnectionConfig db, long value) =>
        Exec(db, $"CREATE UNIQUE INDEX trap ON u(a); INSERT INTO u VALUES ({value})");

    private static void Untrap(ConnectionConfig db, long value) => Exec(db, $"DROP INDEX trap; DELETE FROM u WHERE a = {value} AND rowid = 1");

    private async Task<UpgradeViewModel> RunGroup(params ConnectionConfig[] members)
    {
        var vm = _f.Vm([UpgradeFixture.Group("G", members)]);
        vm.SprintText = "143";
        await vm.LoadCommand.ExecuteAsync(null);
        vm.RequestRunCommand.Execute(null);
        vm.ConfirmInput = "143";
        await vm.ConfirmRunCommand.ExecuteAsync(null).WaitAsync(Bound);
        Assert.Equal(UpgradePhase.Finished, vm.Phase);
        return vm;
    }

    [Fact]
    public async Task OnlyTheFailedDatabasesAreRetried_WithoutReplayingWhatRan_AndSuccessesAreUntouched()
    {
        var a = _f.Db("A");
        var b = _f.Db("B");
        var c = _f.Db("C");
        Trap(b, 10);   // B stops at the 3rd script
        Trap(c, 37);   // C stops at the 4th: a different list is left to run there
        var vm = await RunGroup(a, b, c);
        Assert.Equal([MemberState.Done, MemberState.Failed, MemberState.Failed], vm.Members.Select(m => m.State));
        Assert.True(vm.ShowRetry);
        Assert.Equal("Retry 2 failed databases…", vm.RetryButtonText);
        Assert.Equal(new long[] { 2, 3 }, UpgradeFixture.Rows(b).Where(r => r != 10).ToArray());

        var resolvedBefore = _f.ResolveCount;
        vm.RequestRetryCommand.Execute(null);

        // back in the confirmation: nothing has run yet
        Assert.Equal(UpgradePhase.Confirm, vm.Phase);
        Assert.True(vm.IsRetry);
        Assert.Equal("Retrying 2 failed databases", vm.ConfirmHeading);
        Assert.Equal(["B", "C"], vm.ConfirmDatabases);
        Assert.Equal("Retry 2 failed databases", vm.RunButtonText);
        Assert.Contains("B: skips 2 scripts that already ran; 7 left to run.", vm.RetryLines);
        Assert.Contains("C: skips 3 scripts that already ran; 6 left to run.", vm.RetryLines);
        Assert.Equal(resolvedBefore, _f.ResolveCount);
        Assert.False(vm.ConfirmRunCommand.CanExecute(null));   // the group's token is asked again
        Assert.Equal("Type 143 in the box to enable the run.", vm.RunDisabledReason);

        Untrap(b, 10);
        Untrap(c, 37);
        vm.ConfirmInput = "143";
        await vm.ConfirmRunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(UpgradePhase.Finished, vm.Phase);
        Assert.All(vm.Members, m => Assert.Equal(MemberState.Done, m.State));
        Assert.Equal(Expected143, UpgradeFixture.Rows(a));   // untouched: run once
        Assert.Equal(Expected143, UpgradeFixture.Rows(b));   // the scripts that ran before were not played again
        Assert.Equal(Expected143, UpgradeFixture.Rows(c));
        Assert.Equal(resolvedBefore + 2, _f.ResolveCount);   // only B and C were contacted
        Assert.Equal("3 of 3 databases done.", vm.Message);
        Assert.Equal(UpgradeOutcome.Success, vm.Outcome);
        Assert.False(vm.ShowRetry);
        Assert.Contains("== A: Done", vm.Log);
        Assert.Contains("== B: Done", vm.Log);
    }

    [Fact]
    public async Task NoRetry_WhenNothingFailed_OrWhileRunning_OrAfterACancelledRun()
    {
        var ok = await RunGroup(_f.Db("A"), _f.Db("B"));
        Assert.False(ok.ShowRetry);
        Assert.False(ok.RequestRetryCommand.CanExecute(null));

        UpgradeViewModel.CancelGrace = TimeSpan.FromMilliseconds(100);
        _f.Override = (_, _) => new TaskCompletionSource<Session?>().Task;   // never connects
        var vm = _f.Vm([UpgradeFixture.Group("G", _f.Db("C"), _f.Db("D"))]);
        vm.SprintText = "143";
        await vm.LoadCommand.ExecuteAsync(null);
        vm.RequestRunCommand.Execute(null);
        vm.ConfirmInput = "143";
        var run = vm.ConfirmRunCommand.ExecuteAsync(null);
        Assert.False(vm.ShowRetry);                       // running
        vm.CancelAllCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.All(vm.Members, m => Assert.Equal(MemberState.Cancelled, m.State));
        Assert.False(vm.ShowRetry);                       // cancelled databases are not "failed": no retry offered for them
    }

    [Fact]
    public async Task BackFromTheRetryConfirmation_RestoresTheFinishedRun_AndRunsNothing()
    {
        var b = _f.Db("B");
        Trap(b, 10);
        var vm = await RunGroup(_f.Db("A"), b);
        var message = vm.Message;
        var rowsBefore = UpgradeFixture.Rows(b);

        vm.RequestRetryCommand.Execute(null);
        Assert.Null(vm.Message);
        vm.BackCommand.Execute(null);

        Assert.Equal(UpgradePhase.Finished, vm.Phase);
        Assert.False(vm.IsRetry);
        Assert.Equal(message, vm.Message);
        Assert.True(vm.ShowRetry);
        Assert.Equal(rowsBefore, UpgradeFixture.Rows(b));
        Assert.Equal(MemberState.Failed, vm.Members[1].State);
    }

    [Fact]
    public async Task ASingleDatabase_IsStillConfirmed_ButATypedTokenIsOnlyAskedWhenAScriptRanInPart()
    {
        // fails at the 3rd script, cleanly: no typed token (a single database never needed one), but still a confirmation
        var clean = _f.Db("Clean");
        Trap(clean, 10);
        var vm = _f.Vm([UpgradeFixture.One(clean)]);
        vm.SprintText = "143";
        await vm.LoadCommand.ExecuteAsync(null);
        vm.RequestRunCommand.Execute(null);
        await vm.ConfirmRunCommand.ExecuteAsync(null).WaitAsync(Bound);
        Assert.True(vm.ShowRetry);

        vm.RequestRetryCommand.Execute(null);

        Assert.Equal(UpgradePhase.Confirm, vm.Phase);
        Assert.False(vm.HasRetryWarnings);
        Assert.False(vm.NeedsTypedConfirmation);
        Assert.Equal(["Clean"], vm.ConfirmDatabases);
        Assert.True(vm.ConfirmRunCommand.CanExecute(null));   // one more click, never automatic
        vm.BackCommand.Execute(null);

        // fails INSIDE the 2nd script (its second batch): that script ran in part and would run again from its start
        var partial = _f.Db("Partial");
        Trap(partial, 3);
        var vm2 = _f.Vm([UpgradeFixture.One(partial)]);
        vm2.SprintText = "143";
        await vm2.LoadCommand.ExecuteAsync(null);
        vm2.RequestRunCommand.Execute(null);
        await vm2.ConfirmRunCommand.ExecuteAsync(null).WaitAsync(Bound);
        Assert.True(vm2.ShowRetry);

        vm2.RequestRetryCommand.Execute(null);

        Assert.True(vm2.HasRetryWarnings);
        Assert.Contains("ran 1 batch before it failed. It will run again from the start.", vm2.RetryWarnings.Single());
        Assert.True(vm2.NeedsTypedConfirmation);              // the warning requires the typed confirmation
        Assert.False(vm2.ConfirmRunCommand.CanExecute(null));
        vm2.ConfirmInput = "143";
        Assert.True(vm2.ConfirmRunCommand.CanExecute(null));
    }

    [Fact]
    public async Task ACancelledRetry_EndsCancelled_AndLeavesTheSuccessesAlone()
    {
        UpgradeViewModel.CancelGrace = TimeSpan.FromMilliseconds(100);
        var a = _f.Db("A");
        var b = _f.Db("B");
        Trap(b, 10);
        var vm = await RunGroup(a, b);
        var rowsOfA = UpgradeFixture.Rows(a);
        Untrap(b, 10);

        _f.Override = (_, _) => new TaskCompletionSource<Session?>().Task;   // the retry never connects
        vm.RequestRetryCommand.Execute(null);
        vm.ConfirmInput = "143";
        var run = vm.ConfirmRunCommand.ExecuteAsync(null);
        Assert.Equal(UpgradePhase.Running, vm.Phase);
        Assert.Equal(MemberState.Done, vm.Members[0].State);   // A keeps its card
        vm.CancelAllCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(UpgradePhase.Finished, vm.Phase);
        Assert.Equal(MemberState.Done, vm.Members[0].State);
        Assert.Equal(MemberState.Cancelled, vm.Members[1].State);
        Assert.Equal(rowsOfA, UpgradeFixture.Rows(a));
        Assert.Equal("1 of 2 databases done, 1 cancelled.", vm.Message);
        Assert.False(vm.ShowRetry);
    }

    [Fact]
    public async Task ARetryThatFailsAgain_KeepsWhatEarlierAttemptsCompleted()
    {
        var b = _f.Db("B");
        Trap(b, 10);
        var vm = await RunGroup(_f.Db("A"), b);

        vm.RequestRetryCommand.Execute(null);   // the trap is still there: it fails again at the same script
        vm.ConfirmInput = "143";
        await vm.ConfirmRunCommand.ExecuteAsync(null).WaitAsync(Bound);
        Assert.Equal(MemberState.Failed, vm.Members[1].State);
        Assert.True(vm.ShowRetry);

        vm.RequestRetryCommand.Execute(null);

        Assert.Contains("B: skips 2 scripts that already ran; 7 left to run.", vm.RetryLines);   // not 0, not 9
        Assert.Equal(new long[] { 2, 3 }, UpgradeFixture.Rows(b).Where(r => r != 10).ToArray());                          // and never replayed
    }
}
