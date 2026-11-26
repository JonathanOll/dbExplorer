using Avalonia.Headless.XUnit;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.Tests;

/// <summary>What the group run panel tells the user: the summary, the chips, the notices by severity, the differences chip.</summary>
public class GroupRunUxTests : IDisposable
{
    private readonly GroupSwitchFixture _g = new();

    public void Dispose() => _g.Dispose();

    private async Task<GroupRunViewModel> Run(string sql, bool whole = false)
    {
        var tab = await _g.NewScript(_g.A);
        tab.Text = sql;
        tab.CaretOffset = 0;
        await tab.RunOnGroupAsync(whole);
        return Assert.IsType<GroupRunViewModel>(tab.GroupRun);
    }

    private const string Three = "SELECT id, label FROM big WHERE id <= 3 ORDER BY id";

    // On the dispatcher, as in the app: members connect and report at once, and view models are not thread safe.
    [AvaloniaFact]
    public async Task Summary_SaysHowManyDatabasesAreDone_AndHowLongTheSlowestTook()
    {
        var run = await Run(Three);

        Assert.Matches(@"^3 of 3 databases done · \d+ ms$", run.SummaryText);
    }

    [AvaloniaFact]
    public void Summary_WhileRunning_CountsTheMembersThatEnded()
    {
        var run = new GroupRunViewModel([_g.A, _g.B, _g.Prod], 1000);
        Assert.Equal("0 of 3 databases finished", run.SummaryText);

        run.Apply(new MemberProgress(_g.A.Id, MemberState.Done, 3, TimeSpan.FromMilliseconds(4), null));

        Assert.Equal("1 of 3 databases finished", run.SummaryText);
    }

    [AvaloniaFact]
    public async Task ChipText_IsShort_AndTheFullStatusStaysForTheTooltip()
    {
        var run = await Run(Three);
        var chip = run.Members[0];

        Assert.Matches(@"^3 rows · \d+ ms$", chip.ShortStatusText);
        Assert.Equal("done", chip.StateName);

        var pending = new GroupRunViewModel([_g.A], 1000).Members[0];
        Assert.Equal("Waiting…", pending.ShortStatusText);
        pending.Set(MemberState.Failed, null, null, "connection refused: a very long message that does not belong on a chip");
        Assert.Equal("Failed", pending.ShortStatusText);
        Assert.Contains("connection refused", pending.StatusText);
    }

    [AvaloniaFact]
    public async Task AChip_OpensItsMembersTab_OnceItEnded()
    {
        var run = await Run(Three);
        Assert.Equal("All", run.SelectedTab!.Title);

        var chip = run.Members.Single(m => m.Name == "B");
        Assert.True(chip.SelectCommand.CanExecute(null));
        chip.SelectCommand.Execute(null);

        Assert.Equal("B", run.SelectedTab!.Title);
        Assert.True(run.SelectedTab.IsMember);

        var waiting = new GroupRunViewModel([_g.A], 1000).Members[0];
        Assert.False(waiting.SelectCommand.CanExecute(null));   // nothing to open yet
    }

    [AvaloniaFact]
    public async Task Notices_AreSplitByKind_TheComparisonIsInfo_WhatWasLeftOutIsAWarning()
    {
        var run = await Run("SELECT 1 AS x; " + Three, whole: true);

        Assert.True(run.HasInfoNotices);
        Assert.Contains(run.InfoNotices, n => n.Contains("compare statement"));
        Assert.DoesNotContain(run.WarnNotices, n => n.Contains("compare statement"));

        var refused = GroupRunViewModel.CreateRefused(GroupRunViewModel.ReadOnlyNotice);
        Assert.False(refused.HasInfoNotices);
        Assert.Equal([GroupRunViewModel.ReadOnlyNotice], refused.WarnNotices);
    }

    [AvaloniaFact]
    public async Task TheDifferencesChip_CountsTheRowsThatDiffer_AndSaysWhenThereAreNone()
    {
        var run = await Run(Three);
        Assert.True(run.HasDifferenceChip);
        Assert.Matches(@"^\d+ differ$", run.DifferenceChipText);
        Assert.False(run.IsIdentical);

        var same = await Run("SELECT id FROM big WHERE id <= 3 ORDER BY id");
        Assert.True(same.IsIdentical);
        Assert.Equal("Identical", same.DifferenceChipText);
        Assert.Equal(0, same.DifferenceCount);
    }

    [AvaloniaFact]
    public async Task TheSegmentedControl_AllRowsIsTheOppositeOfDifferences()
    {
        var run = await Run(Three);
        Assert.True(run.ShowAllRows);
        Assert.False(run.ShowDifferences);

        run.ShowAllRows = false;
        Assert.True(run.ShowDifferences);
        Assert.Contains("differ", run.AllStatus);

        run.ShowDifferences = false;
        Assert.True(run.ShowAllRows);
        Assert.Contains("from 3 databases", run.AllStatus);
    }

    [AvaloniaFact]
    public async Task ClosingThePanel_ShowsTheOrdinaryResultsAgain_ButNeverWhileItRuns()
    {
        var tab = await _g.NewScript(_g.A);
        tab.Text = Three;
        tab.CaretOffset = 0;
        await tab.RunOnGroupAsync(false);
        Assert.NotNull(tab.GroupRun);

        tab.CloseGroupRunPanelCommand.Execute(null);

        Assert.Null(tab.GroupRun);
    }

    [AvaloniaFact]
    public async Task TheRunButtons_ExplainWhyTheyAreOff_DuringAGroupRun()
    {
        var tab = await _g.NewScript(_g.A);
        Assert.Null(tab.RunDisabledReason);

        var hang = _g.Gate(_g.B.Id);
        try
        {
            tab.Text = Three;
            tab.CaretOffset = 0;
            var run = tab.RunOnGroupAsync(false);
            await Task.Run(async () =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!tab.IsGroupRunning && DateTime.UtcNow < deadline) await Task.Delay(10);
            });

            Assert.Contains("group run", tab.RunDisabledReason);
            Assert.NotNull(tab.RunStartedAt);
            Assert.Equal("Running on 3 databases", tab.RunProgressText);
            Assert.False(tab.CloseGroupRunPanelCommand is null);
            tab.CloseGroupRunPanel();
            Assert.NotNull(tab.GroupRun);   // still running: not closed

            hang.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            hang.TrySetResult();
        }
        Assert.Null(tab.RunDisabledReason);
        Assert.Null(tab.RunStartedAt);
    }
}
