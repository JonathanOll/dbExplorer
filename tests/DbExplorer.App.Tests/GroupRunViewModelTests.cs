using Avalonia.Input;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.Tests;

/// <summary>
/// Run on group: the status panel, per-member results, All and Differences, and above all the safety rules (a group run is
/// read-only for every member, nothing runs for a refused statement, gates against switching and ordinary runs).
/// Members are SQLite files resolved through a real tree (see <see cref="GroupSwitchFixture"/>): "Items" = [A, B, Prod].
/// </summary>
public class GroupRunViewModelTests : IDisposable
{
    private readonly GroupSwitchFixture _g = new();

    public void Dispose() => _g.Dispose();

    private async Task<ScriptTabViewModel> Tab(ConnectionConfig? on = null, ConnectionGroup? group = null)
    {
        var tab = await _g.NewScript(on ?? _g.A);
        if (group is not null) tab.ActiveGroup = group;
        return tab;
    }

    private static async Task RunGroup(ScriptTabViewModel tab, string sql, bool whole = false)
    {
        tab.Text = sql;
        tab.CaretOffset = 0;
        await tab.RunOnGroupAsync(whole);
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(10);
        }
    }

    private static MemberStatusViewModel Member(GroupRunViewModel run, string name) => run.Members.Single(m => m.Name == name);

    /// <summary>A gate that is missing must fail a test, not hang it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private const string Three ="SELECT id, label FROM big WHERE id <= 3 ORDER BY id";

    // ---- status panel, per-member results, All, Differences ----

    [Fact]
    public async Task RunOnGroup_ShowsOneStatusPerMember_AndPerMemberResults()
    {
        var tab = await Tab();
        await RunGroup(tab, Three);

        var run = Assert.IsType<GroupRunViewModel>(tab.GroupRun);
        Assert.Equal(["A", "B", "Prod"], run.Members.Select(m => m.Name));
        Assert.All(run.Members, m =>
        {
            Assert.Equal(MemberState.Done, m.State);
            Assert.Equal(3, m.Rows);
            Assert.NotNull(m.Duration);
        });
        Assert.Equal(["A", "B", "Prod"], run.PerMemberResults.Select(r => r.Header));
        Assert.All(run.PerMemberResults, r => Assert.Equal(3, r.Sets[0].Rows.Count));
        Assert.StartsWith("b", (string)run.PerMemberResults[1].Sets[0].Rows[0][1]!);   // each member's own data
        Assert.False(tab.IsGroupRunning);
        Assert.False(run.IsRunning);
        Assert.Equal(["All", "A", "B", "Prod"], run.Tabs.Select(t => t.Title));
        Assert.Equal(0, _g.Fx.Sessions.Open.Count(s => s.Config.Id == _g.NoBig.Id));   // only the group's members were touched
    }

    [Fact]
    public async Task RunOnGroup_GroupListingAConnectionTwice_RunsItOnce_AndDoesNotThrow()
    {
        // A hand-edited config can repeat a connection id in a group: the run must treat it as one member.
        var dup = new ConnectionGroup(Guid.NewGuid(), "Dup", [_g.A.Id, _g.B.Id, _g.A.Id]);
        _g.Tree.Config.Groups.Add(dup);
        var tab = await Tab(group: dup);

        await RunGroup(tab, Three);

        var run = Assert.IsType<GroupRunViewModel>(tab.GroupRun);
        Assert.Equal(["A", "B"], run.Members.Select(m => m.Name));
        Assert.All(run.Members, m => Assert.Equal(MemberState.Done, m.State));
    }

    [Fact]
    public async Task RunOnGroup_AllTab_HasDatabaseColumn_FirstColumn()
    {
        var tab = await Tab();
        await RunGroup(tab, Three);

        var run = tab.GroupRun!;
        Assert.Equal(["Database", "id", "label"], run.AllColumns.Select(c => c.Name));
        Assert.Equal(["Database", "id", "label"], run.All!.Columns);
        Assert.Equal(9, run.AllRows.Count);
        Assert.Equal("A", run.AllRows[0][0]);
        Assert.Equal("Prod", run.AllRows[^1][0]);
    }

    [Fact]
    public async Task RunOnGroup_DifferencesToggle_ShowsOnlyNonIdenticalRows()
    {
        var tab = await Tab();
        // id 1 and 3 are identical in every member, id 2 differs.
        await RunGroup(tab, "SELECT id, CASE WHEN id = 2 THEN label ELSE 'same' END AS v FROM big WHERE id <= 3 ORDER BY id");
        var run = tab.GroupRun!;
        Assert.Equal(9, run.AllRows.Count);

        run.ShowDifferences = true;

        Assert.Equal(["Present in", "id", "v"], run.AllColumns.Select(c => c.Name));
        Assert.Equal(3, run.AllRows.Count);
        Assert.All(run.AllRows, r => Assert.Equal(2L, r[1]));
        Assert.Equal(["A", "B", "Prod"], run.AllRows.Select(r => (string)r[0]!).OrderBy(x => x));

        run.ShowDifferences = false;
        Assert.Equal("Database", run.AllColumns[0].Name);
        Assert.Equal(9, run.AllRows.Count);
    }

    [Fact]
    public async Task RunOnGroup_Differences_WhenAllMembersAreIdentical_SaysSo()
    {
        var tab = await Tab();
        await RunGroup(tab, "SELECT id FROM big WHERE id <= 3 ORDER BY id");
        var run = tab.GroupRun!;

        run.ShowDifferences = true;

        Assert.Empty(run.AllRows);
        Assert.Contains("identical", run.Notice);
    }

    // ---- read-only by construction ----

    [Fact]
    public async Task RunOnGroup_WriteStatement_IsRefused_NothingRuns_NoticeShown()
    {
        var tab = await Tab();
        var before = new[] { _g.ResolveCount(_g.A.Id), _g.ResolveCount(_g.B.Id), _g.ResolveCount(_g.Prod.Id) };

        await RunGroup(tab, "DELETE FROM big");

        var run = Assert.IsType<GroupRunViewModel>(tab.GroupRun);
        Assert.Equal("Group runs are read-only; run writes on one database at a time", run.Notice);
        Assert.Empty(run.Members);
        Assert.Empty(run.Tabs);
        Assert.False(tab.IsGroupRunning);
        // nobody was even contacted: no session was asked for, no connection was opened
        Assert.Equal(before, new[] { _g.ResolveCount(_g.A.Id), _g.ResolveCount(_g.B.Id), _g.ResolveCount(_g.Prod.Id) });
        Assert.Empty(tab.GroupConnectionsOpened);
        Assert.Equal(450, _g.Scalar(_g.FileA, "SELECT COUNT(*) FROM big"));
    }

    [Fact]
    public async Task RunOnGroup_WriteInTheMiddleOfAScript_RefusesTheWholeRun()
    {
        var tab = await Tab();
        await RunGroup(tab, "SELECT 1; UPDATE big SET label = 'x'; SELECT 2", whole: true);

        Assert.Equal(GroupRunViewModel.ReadOnlyNotice, tab.GroupRun!.Notice);
        Assert.Empty(tab.GroupConnectionsOpened);
        Assert.Equal(0, _g.Scalar(_g.FileA, "SELECT COUNT(*) FROM big WHERE label = 'x'"));
    }

    [Fact]
    public async Task RunOnGroup_ReadOnlyForcedEvenIfMemberIsNotReadOnly()
    {
        var tab = await Tab();
        Assert.False(_g.A.ReadOnly);   // A (and B) carry no read-only flag of their own

        // control: the very same statement works in an ordinary run on A, whose flag is off
        await GroupSwitchFixture.Run(tab, "INSERT INTO only_a VALUES (2, 'y')");
        Assert.Equal(2, _g.Scalar(_g.FileA, "SELECT COUNT(*) FROM only_a"));

        // a group run refuses it for every member, A included
        await RunGroup(tab, "INSERT INTO only_a VALUES (3, 'z')");

        Assert.Equal(GroupRunViewModel.ReadOnlyNotice, tab.GroupRun!.Notice);
        Assert.Equal(2, _g.Scalar(_g.FileA, "SELECT COUNT(*) FROM only_a"));
    }

    // ---- failures stay on their member ----

    [Fact]
    public async Task RunOnGroup_UnreachableMember_ShowsErrorOnThatMemberOnly()
    {
        var tab = await Tab(group: _g.Broken);   // A, NoBig (no table big), Down (unreachable file)
        Assert.Equal(_g.Broken.Id, tab.ActiveGroup!.Id);

        await RunGroup(tab, "SELECT id FROM big WHERE id <= 2 ORDER BY id");

        var run = tab.GroupRun!;
        Assert.Equal(MemberState.Done, Member(run, "A").State);
        Assert.Equal(MemberState.Failed, Member(run, "NoBig").State);
        Assert.Contains("big", Member(run, "NoBig").Error);
        Assert.Equal(MemberState.Failed, Member(run, "Down").State);
        Assert.False(string.IsNullOrWhiteSpace(Member(run, "Down").Error));
        Assert.True(run.PerMemberResults.Single(r => r.Header == "Down").HasError);
        Assert.Equal(2, run.AllRows.Count);   // only A feeds All
        Assert.All(run.AllRows, r => Assert.Equal("A", r[0]));
        Assert.Contains("NoBig failed and is left out", run.Notice);
        Assert.Contains("Down failed and is left out", run.Notice);
    }

    // ---- cancel: never freeze the panel ----

    [Fact]
    public async Task RunOnGroup_CancelAll_StopsEverything_IsRunningFalse()
    {
        var hang = _g.Gate(_g.B.Id);   // B's connect hangs
        try
        {
            var tab = await Tab();
            tab.Text = Three;
            tab.CaretOffset = 0;
            var running = tab.RunOnGroupAsync(false);
            await Eventually(() => tab.GroupRun is { } r && r.Members.Any(m => m.IsRunning), "a member running");
            Assert.True(tab.IsGroupRunning);

            tab.CancelGroupCommand.Execute(null);
            await running.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(tab.IsGroupRunning);
            Assert.False(tab.GroupRun!.IsRunning);
            Assert.Equal(MemberState.Cancelled, Member(tab.GroupRun, "B").State);
            Assert.All(tab.GroupRun.Members, m => Assert.False(m.CanCancel));
        }
        finally
        {
            hang.TrySetResult();
        }
    }

    [Fact]
    public async Task RunOnGroup_CancelOneMember()
    {
        var hang = _g.Gate(_g.B.Id);
        try
        {
            var tab = await Tab();
            tab.Text = Three;
            tab.CaretOffset = 0;
            var running = tab.RunOnGroupAsync(false);
            await Eventually(
                () => tab.GroupRun is { } r && r.Members.Where(m => m.Name != "B").All(m => m.IsDone) && Member(r, "B").IsRunning,
                "A and Prod done, B hanging");
            Assert.False(running.IsCompleted);   // B holds the run

            Member(tab.GroupRun!, "B").CancelCommand.Execute(null);
            await running.WaitAsync(TimeSpan.FromSeconds(15));

            var run = tab.GroupRun!;
            Assert.Equal(MemberState.Cancelled, Member(run, "B").State);
            Assert.Equal(MemberState.Done, Member(run, "A").State);
            Assert.Equal(MemberState.Done, Member(run, "Prod").State);
            Assert.Equal(6, run.AllRows.Count);   // B is left out, said so
            Assert.Contains("B was cancelled and is left out", run.Notice);
            Assert.False(tab.IsGroupRunning);
        }
        finally
        {
            hang.TrySetResult();
        }
    }

    // ---- statement selection: the rules of Ctrl+Enter ----

    [Fact]
    public async Task RunOnGroup_UsesSelectionRulesOfCtrlEnter()
    {
        var tab = await Tab();
        const string script = "SELECT 1 AS x;\n\nSELECT 2 AS x;\n\nSELECT 3 AS x";
        tab.Text = script;

        // the blank-line statement under the caret
        tab.CaretOffset = script.IndexOf("SELECT 2", StringComparison.Ordinal) + 3;
        await tab.RunOnGroupAsync(false);
        Assert.Equal([2L, 2L, 2L], tab.GroupRun!.AllRows.Select(r => r[1]));

        // a selection runs as selected, not the statement around the caret
        var start = script.IndexOf("SELECT 1", StringComparison.Ordinal);
        tab.Selection = (start, start + "SELECT 1 AS x".Length);
        await tab.RunOnGroupAsync(false);
        Assert.Equal([1L, 1L, 1L], tab.GroupRun!.AllRows.Select(r => r[1]));
        tab.Selection = null;

        // the whole script: every statement runs, the last one is compared, and the notice says which
        await tab.RunOnGroupAsync(true);
        Assert.Equal([3L, 3L, 3L], tab.GroupRun!.AllRows.Select(r => r[1]));
        Assert.Contains("3 statements", tab.GroupRun.Notice);
        Assert.Contains("statement 3", tab.GroupRun.Notice);
    }

    [Fact]
    public async Task RunOnGroup_NothingToRun_RunsNothing()
    {
        var tab = await Tab();
        var before = _g.ResolveCount(_g.B.Id);
        await RunGroup(tab, "   ");

        Assert.Null(tab.GroupRun);
        Assert.Equal(before, _g.ResolveCount(_g.B.Id));
    }

    // ---- ownership of the member connections ----

    [Fact]
    public async Task RunOnGroup_EveryMemberGetsItsOwnFreshConnection_DisposedWhenItsMemberEnded()
    {
        var tab = await Tab();
        await tab.RunStatementAsync();   // make sure the tab has its own connection first
        await RunGroup(tab, Three);

        var opened = tab.GroupConnectionsOpened;
        Assert.Equal(3, opened.Count);
        Assert.Equal(3, opened.Distinct().Count());
        foreach (var connection in opened) await AssertDisposed(connection);

        await RunGroup(tab, Three);   // a second run opens new ones, nothing is kept between runs
        Assert.Equal(3, tab.GroupConnectionsOpened.Count);
        Assert.DoesNotContain(tab.GroupConnectionsOpened, c => opened.Contains(c));
    }

    [Fact]
    public async Task RunOnGroup_ClosingTheTab_CancelsAndDisposesMemberConnections()
    {
        var hang = _g.Gate(_g.B.Id);
        var tab = await Tab();
        try
        {
            tab.Text = Three;
            tab.CaretOffset = 0;
            var running = tab.RunOnGroupAsync(false);
            await Eventually(() => tab.GroupRun is { } r && Member(r, "B").IsRunning && tab.GroupConnectionsOpened.Count == 2, "A and Prod connected");

            tab.Close();
            await running.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(tab.IsGroupRunning);
            Assert.NotEmpty(tab.GroupConnectionsOpened);
            foreach (var connection in tab.GroupConnectionsOpened) await AssertDisposed(connection);
        }
        finally
        {
            hang.TrySetResult();
        }
        // B's connect finishes after the close: it must not leave a connection behind
        await Task.Delay(100);
        Assert.Equal(2, tab.GroupConnectionsOpened.Count);
    }

    private static async Task AssertDisposed(ScriptConnection connection) =>
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await foreach (var _ in connection.RunAsync(
                               [new ScriptStatement("SELECT 1", 0, 8)], new ScriptOptions(), CancellationToken.None)) { }
        });

    // ---- availability and gates ----

    [Fact]
    public async Task RunOnGroup_NotInAGroup_IsNotAvailable()
    {
        var tab = await Tab(_g.Solo);
        Assert.False(tab.HasGroupSwitch);
        var resolved = _g.ResolveCount(_g.Solo.Id);

        // a write would be "run on one database" if this fell back to an ordinary run: it must not
        await RunGroup(tab, "CREATE TABLE should_not_exist (id INTEGER)");

        Assert.Null(tab.GroupRun);
        Assert.Empty(tab.Results);
        Assert.False(tab.IsGroupRunning);
        Assert.Equal(resolved, _g.ResolveCount(_g.Solo.Id));
        Assert.Equal(0, _g.Scalar(_g.Fx.Dir + "/s.db", "SELECT COUNT(*) FROM sqlite_master WHERE name = 'should_not_exist'"));
        Assert.Contains("not in a group", _g.Fx.Status.Message);
    }

    [Fact]
    public async Task RunOnGroup_RowLimitAppliesPerMember_WithTruncationNotice()
    {
        var tab = await Tab();
        tab.RowLimit = 100;

        await RunGroup(tab, "SELECT id FROM big ORDER BY id");

        var run = tab.GroupRun!;
        Assert.All(run.PerMemberResults, r =>
        {
            Assert.Equal(100, r.Sets[0].Rows.Count);
            Assert.True(r.IsTruncated);
            Assert.Contains("100", r.TruncatedNotice);
        });
        Assert.Equal(300, run.AllRows.Count);
        Assert.Contains("A: showing only the first 100 rows", run.Notice);
        Assert.Contains("Prod: showing only the first 100 rows", run.Notice);

        run.ShowDifferences = true;
        Assert.Contains("A: rows were cut at the row limit (100); differences may be false", run.Notice);
    }

    [Fact]
    public async Task RunOnGroup_IsRefusedWhileTheTabIsSwitching()
    {
        var tab = await Tab();
        var hang = _g.Gate(_g.B.Id);
        try
        {
            var switching = tab.SwitchConnectionAsync(_g.B);
            await Eventually(() => tab.IsSwitching, "the switch started");
            var before = _g.ResolveCount(_g.Prod.Id);

            await RunGroup(tab, Three).WaitAsync(Bound);

            Assert.Null(tab.GroupRun);
            Assert.False(tab.IsGroupRunning);
            Assert.Equal(before, _g.ResolveCount(_g.Prod.Id));
            Assert.Contains("Switching", _g.Fx.Status.Message);

            hang.SetResult();
            Assert.True(await switching.WaitAsync(Bound));
        }
        finally
        {
            hang.TrySetResult();
        }
    }

    [Fact]
    public async Task SwitchingIsRefusedWhileAGroupRunIsInProgress_AndLeavesNothingDangling()
    {
        var tab = await Tab();
        var hang = _g.Gate(_g.B.Id);
        try
        {
            tab.Text = Three;
            tab.CaretOffset = 0;
            var running = tab.RunOnGroupAsync(false);
            await Eventually(() => tab.IsGroupRunning && tab.GroupRun is { } r && Member(r, "B").IsRunning, "run active");

            Assert.False(await tab.SwitchConnectionAsync(_g.Prod).WaitAsync(Bound));
            Assert.False(await tab.SwitchRelativeAsync(1).WaitAsync(Bound));   // would hang on B's gate if the switch went through

            Assert.Equal(_g.A.Id, tab.Connection.Id);   // the tab did not move
            Assert.False(tab.IsSwitching);
            Assert.Contains("group run is in progress", _g.Fx.Status.Message);

            tab.CancelGroup();
            await running.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(await tab.SwitchConnectionAsync(_g.Prod).WaitAsync(Bound));   // allowed again once it has ended
        }
        finally
        {
            hang.TrySetResult();
        }
    }

    [Fact]
    public async Task OrdinaryRunAndSecondGroupRun_AreRefusedWhileAGroupRunIsInProgress()
    {
        var tab = await Tab();
        var hang = _g.Gate(_g.B.Id);
        try
        {
            tab.Text = Three;
            tab.CaretOffset = 0;
            var running = tab.RunOnGroupAsync(false);
            await Eventually(() => tab.IsGroupRunning && tab.GroupRun is { } r && Member(r, "B").IsRunning, "run active");
            var run = tab.GroupRun;
            var opened = tab.GroupConnectionsOpened.Count;

            await tab.RunStatementAsync().WaitAsync(Bound);
            Assert.False(tab.IsRunning);
            Assert.Empty(tab.Results);
            Assert.Contains("group run is in progress", _g.Fx.Status.Message);

            await tab.RunOnGroupAsync(false).WaitAsync(Bound);
            Assert.Same(run, tab.GroupRun);   // no second panel, no second set of connections
            Assert.Equal(opened, tab.GroupConnectionsOpened.Count);
            Assert.False(tab.CanRun);

            tab.CancelGroup();
            await running.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(tab.CanRun);
        }
        finally
        {
            hang.TrySetResult();
        }
    }

    // ---- merge inputs and notices (pure) ----

    private static ConnectionConfig Conn(string name) => TreeFixture.Conn(name);

    private static StatementResult Stmt(string[] columns, object?[][] rows, bool truncated = false) =>
        new(new ScriptStatement("SELECT", 0, 6), [new ResultSet(columns, rows, truncated)], null, TimeSpan.Zero, null);

    private static MemberResult Done(string name, string[] columns, params object?[][] rows) =>
        new(Conn(name), MemberState.Done, [Stmt(columns, rows)], TimeSpan.FromMilliseconds(5), null);

    [Fact]
    public void Prepare_ExcludesFailedCancelledAndColumnlessMembers_WithANotice()
    {
        var empty = new StatementResult(new ScriptStatement("SELECT", 0, 6), [], 3, TimeSpan.Zero, null);
        var results = new List<MemberResult>
        {
            Done("one", ["id"], [1L], [2L]),
            Done("two", ["id"], [1L], [2L]),
            new(Conn("broken"), MemberState.Failed, [], TimeSpan.Zero, "boom"),
            new(Conn("stopped"), MemberState.Cancelled, [], TimeSpan.Zero, null),
            new(Conn("silent"), MemberState.Done, [empty], TimeSpan.Zero, null),   // returned no columns at all
            new(Conn("zero"), MemberState.Done, [Stmt([], [])], TimeSpan.Zero, null),   // a set with no columns
        };

        var prepared = GroupRunViewModel.Prepare(results, 1, 1000);

        // the members without columns did not wipe the common columns of the others
        Assert.Equal(["Database", "id"], prepared.Merged.Result.Columns);
        Assert.Equal(4, prepared.Merged.Result.Rows.Count);
        var notices = string.Join("\n", prepared.Merged.Notices);
        Assert.Contains("broken failed and is left out", notices);
        Assert.Contains("stopped was cancelled and is left out", notices);
        Assert.Contains("silent returned no columns", notices);
        Assert.Contains("zero returned no columns", notices);
        Assert.Empty(prepared.Differences.Result.Rows);
        Assert.Contains("identical", string.Join("\n", prepared.Differences.Notices));
        // every member still has its own tab, a failed one shows its error
        Assert.Equal(6, prepared.MemberResults.Count);
        Assert.Equal("boom", prepared.MemberResults[2].Result.Error);
        Assert.Equal("Cancelled.", prepared.MemberResults[3].Result.Error);
    }

    [Fact]
    public void Prepare_NoCommonColumns_ShowsADedicatedNoticeAndNoRows()
    {
        var results = new List<MemberResult> { Done("one", ["a"], [1L]), Done("two", ["b"], [2L]) };

        var prepared = GroupRunViewModel.Prepare(results, 1, 1000);

        Assert.Empty(prepared.Merged.Result.Rows);
        Assert.Empty(prepared.Differences.Result.Rows);
        Assert.Contains("no column in common", string.Join("\n", prepared.Merged.Notices));
        Assert.Contains("no column in common", string.Join("\n", prepared.Differences.Notices));
    }

    [Fact]
    public void Prepare_DifferentColumnSets_MergeOnTheCommonOnes_AndWarn()
    {
        var results = new List<MemberResult> { Done("one", ["id", "x"], [1L, "p"]), Done("two", ["id", "y"], [1L, "q"]) };

        var prepared = GroupRunViewModel.Prepare(results, 1, 1000);

        Assert.Equal(["Database", "id"], prepared.Merged.Result.Columns);
        Assert.Contains("columns not common to all members were left out", string.Join("\n", prepared.Merged.Notices));
    }

    [Fact]
    public void Prepare_TruncatedMember_InDifferences_WarnsThatDifferencesMayBeFalse()
    {
        var results = new List<MemberResult>
        {
            new(Conn("big"), MemberState.Done, [Stmt(["id"], [[1L], [2L]], truncated: true)], TimeSpan.Zero, null),
            Done("small", ["id"], [1L], [2L]),
        };

        var prepared = GroupRunViewModel.Prepare(results, 1, 2);

        var diff = string.Join("\n", prepared.Differences.Notices);
        Assert.Contains("big: rows were cut at the row limit (2); differences may be false", diff);
        Assert.DoesNotContain("identical", diff);   // identical-so-far is not a claim to make about cut rows
        Assert.Contains("big: showing only the first 2 rows", string.Join("\n", prepared.Merged.Notices));
    }

    [Fact]
    public void Prepare_SameNameTwice_GetsUniqueDisplayNames()
    {
        var results = new List<MemberResult> { Done("dup", ["id"], [1L]), Done("dup", ["id"], [2L]), Done("Dup", ["id"], [3L]) };

        var prepared = GroupRunViewModel.Prepare(results, 1, 1000);

        Assert.Equal(["dup", "dup (2)", "Dup (3)"], prepared.Merged.Result.Rows.Select(r => (string)r[0]!));
        Assert.Equal(["dup", "dup (2)", "Dup (3)"], prepared.MemberResults.Select(m => m.Title));
        Assert.Equal(["dup", "dup (2)", "Dup (3)"],
            new GroupRunViewModel([Conn("dup"), Conn("dup"), Conn("Dup")], 10).Members.Select(m => m.Name));
    }

    [Fact]
    public void UniqueNames_KeepsRealNames_AndSkipsTakenSuffixes()
    {
        Assert.Equal(["a", "a (3)", "a (2)", "b", "a (4)"],
            GroupRunViewModel.UniqueNames(["a", "a", "a (2)", "b", "a"]));
        Assert.Equal(["x"], GroupRunViewModel.UniqueNames(["x"]));
    }

    [Fact]
    public void Prepare_NoMemberReturnedRows_SaysSo()
    {
        var results = new List<MemberResult> { new(Conn("broken"), MemberState.Failed, [], TimeSpan.Zero, "boom") };

        var prepared = GroupRunViewModel.Prepare(results, 1, 1000);

        Assert.Empty(prepared.Merged.Result.Rows);
        Assert.Contains("No member returned rows", string.Join("\n", prepared.Merged.Notices));
    }

    // ---- progress is advisory, the result authoritative ----

    [Fact]
    public void Complete_OverwritesEveryStatus_AndLateProgressIsIgnored()
    {
        var one = Conn("one");
        var run = new GroupRunViewModel([one], 100);
        run.Apply(new MemberProgress(one.Id, MemberState.Running, null, null, null));
        Assert.Equal(MemberState.Running, run.Members[0].State);

        run.Complete([new MemberResult(one, MemberState.Done, [Stmt(["id"], [[1L]])], TimeSpan.FromMilliseconds(7), null)], 1);
        run.Apply(new MemberProgress(one.Id, MemberState.Running, null, null, null));   // a late, stale report

        Assert.Equal(MemberState.Done, run.Members[0].State);
        Assert.Equal(1, run.Members[0].Rows);
        Assert.False(run.IsRunning);
    }

    [Fact]
    public void Detach_MarksUnfinishedMembersCancelled_AndIgnoresLateReports()
    {
        var one = Conn("one");
        var two = Conn("two");
        var run = new GroupRunViewModel([one, two], 100);
        run.Apply(new MemberProgress(one.Id, MemberState.Done, 3, TimeSpan.FromMilliseconds(1), null));

        run.Detach("abandoned");
        run.Apply(new MemberProgress(two.Id, MemberState.Done, 3, TimeSpan.FromMilliseconds(1), null));

        Assert.Equal(MemberState.Done, run.Members[0].State);
        Assert.Equal(MemberState.Cancelled, run.Members[1].State);
        Assert.Equal("abandoned", run.Notice);
        Assert.False(run.IsRunning);
    }

    [Fact]
    public async Task SafeProgress_NeverThrows_WhateverTheHandlerDoes()
    {
        var progress = new SafeProgress<int>(_ => throw new InvalidOperationException("boom"));
        progress.Report(1);   // would escape the runner's RunAsync if it propagated

        // with a synchronization context (a test thread may have one) the handler runs later, on that context
        var seen = new List<int>();
        var ok = new SafeProgress<int>(v => { lock (seen) seen.Add(v); });
        ok.Report(7);
        await Eventually(() => { lock (seen) return seen.Count == 1; }, "the report to be handled");
        Assert.Equal([7], seen);
    }

    [Fact]
    public void RunViewModel_CancelMember_AndCancelAllCommand_CallTheRunner_AndMarkTheLines()
    {
        var one = Conn("one");
        var two = Conn("two");
        var cancelled = new List<Guid>();
        var cancelAll = 0;
        var run = new GroupRunViewModel([one, two], 100, () => cancelAll++, cancelled.Add);

        run.Members[0].CancelCommand.Execute(null);   // the line's button goes through CancelMember

        Assert.Equal([one.Id], cancelled);
        Assert.Equal(MemberState.Cancelled, run.Members[0].State);
        Assert.False(run.Members[0].CanCancel);
        Assert.Equal(MemberState.Pending, run.Members[1].State);

        run.CancelMember(two.Id);
        Assert.Equal([one.Id, two.Id], cancelled);

        run.CancelAllCommand.Execute(null);
        Assert.Equal(1, cancelAll);

        run.Apply(new MemberProgress(one.Id, MemberState.Done, 5, TimeSpan.FromMilliseconds(42), null));   // ended lines have a time
        Assert.Equal(42, run.Members[0].Ms);
    }

    [Fact]
    public async Task RunOnGroup_AResultColumnNamedDatabase_KeepsEveryValueInItsOwnColumn()
    {
        var tab = await Tab();
        await RunGroup(tab, "SELECT 1 AS Database");

        var run = tab.GroupRun!;
        Assert.Equal(["Database", "Database"], run.AllColumns.Select(c => c.Name));   // two same-named headers: a known limitation
        Assert.Equal([("A", 1L), ("B", 1L), ("Prod", 1L)], run.AllRows.Select(r => ((string)r[0]!, (long)r[1]!)));
    }

    // ---- chords ----

    [Fact]
    public void Chords_RunOnGroup_DoNotCollideWithTheOrdinaryRuns()
    {
        Assert.Equal(ScriptKeyAction.RunStatement, ScriptTabKeys.Resolve(Key.Enter, KeyModifiers.Control));
        Assert.Equal(ScriptKeyAction.RunScript, ScriptTabKeys.Resolve(Key.F5, KeyModifiers.None));
        Assert.Equal(ScriptKeyAction.RunStatementOnGroup, ScriptTabKeys.Resolve(Key.Enter, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal(ScriptKeyAction.RunScriptOnGroup, ScriptTabKeys.Resolve(Key.F5, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal(ScriptKeyAction.Save, ScriptTabKeys.Resolve(Key.S, KeyModifiers.Control));
        Assert.Equal(ScriptKeyAction.Cancel, ScriptTabKeys.Resolve(Key.Cancel, KeyModifiers.Control));

        Assert.Equal(ScriptKeyAction.None, ScriptTabKeys.Resolve(Key.Enter, KeyModifiers.None));
        Assert.Equal(ScriptKeyAction.None, ScriptTabKeys.Resolve(Key.Enter, KeyModifiers.Shift));
        Assert.Equal(ScriptKeyAction.None, ScriptTabKeys.Resolve(Key.Enter, KeyModifiers.Control | KeyModifiers.Alt));
        Assert.Equal(ScriptKeyAction.None, ScriptTabKeys.Resolve(Key.F5, KeyModifiers.Control));
        Assert.Equal(ScriptKeyAction.None, ScriptTabKeys.Resolve(Key.F5, KeyModifiers.Shift));
    }

    [Fact]
    public void Chords_AreNotWindowShortcuts()
    {
        // MainShortcuts runs first (tunnel, window level): it must leave Ctrl+Shift+Enter and Ctrl+Shift+F5 to the editor.
        var main = new MainViewModel(_g.Tree, _g.Fx.Status);
        Assert.False(MainShortcuts.Handle(main, Key.Enter, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.False(MainShortcuts.Handle(main, Key.F5, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.False(MainShortcuts.Handle(main, Key.Enter, KeyModifiers.Control));
    }
}
