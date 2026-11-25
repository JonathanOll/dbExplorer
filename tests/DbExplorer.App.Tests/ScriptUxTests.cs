using Avalonia.Input;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.Tests;

/// <summary>
/// What a script tab tells the user: result tab titles, the result line, the run summary, errors with a place, the read-only
/// refusal, the progress of a running script, the row-limit action, and the keys of the editor.
/// </summary>
public class ScriptUxTests : IDisposable
{
    private readonly ScriptTabFixture _t = new();
    private ScriptTabFixture? _readOnly;

    public void Dispose()
    {
        _t.Dispose();
        _readOnly?.Dispose();
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(10);
        }
    }

    // ---- result tab titles ----

    [Fact]
    public async Task Header_SkipsLeadingComments_AndNamesTheStatement()
    {
        var tab = await _t.NewTab("-- Best customers this year\nSELECT id, name, name, name, name FROM t WHERE id > 0;\n\n/* block */ UPDATE t SET name = 'x' WHERE id = 99;");

        await tab.RunScriptAsync();

        Assert.Equal(["1 · SELECT … FROM t", "2 · UPDATE t"], tab.ResultTabs.Select(r => r.Header).ToArray());
    }

    [Fact]
    public async Task Header_OfAShortStatement_IsTheStatement_AndAFailureIsNotMarkedWithABang()
    {
        var tab = await _t.NewTab("SELECT 1; SELECT * FROM nope");

        await tab.RunScriptAsync();

        Assert.Equal(["1 · SELECT 1", "2 · SELECT * FROM nope"], tab.ResultTabs.Select(r => r.Header).ToArray());
        var failed = tab.Results[1];
        Assert.True(failed.HasError);
        Assert.StartsWith("Failed: ", failed.AutomationName);   // said in words for a screen reader, the icon carries it for the eye
        Assert.StartsWith("Failed", failed.ToolTipText);
        Assert.Equal("Failed", failed.StatusMain);
    }

    // ---- the result line ----

    [Fact]
    public async Task ResultLine_SaysRowsAndTime_ForAQuery_AndRowsAffectedWithTime_ForAWrite()
    {
        var tab = await _t.NewTab("SELECT id FROM t ORDER BY id; UPDATE t SET name = 'x' WHERE id = 2; CREATE TEMP TABLE z (a INTEGER)");

        await tab.RunScriptAsync();

        Assert.Equal("3 rows", tab.Results[0].StatusMain);
        Assert.Matches(@"^\d+ ms$", tab.Results[0].StatusDetail);
        Assert.Equal("1 row affected", tab.Results[1].StatusMain);
        Assert.Matches(@"^1 row affected · \d+ ms$", tab.Results[1].StatusText);
        Assert.False(tab.Results[0].ShowNoResult);
        Assert.True(tab.Results[2].ShowNoResult);   // no result set: the area says so instead of staying blank
        Assert.StartsWith("Statement ran", tab.Results[2].NoResultText);
    }

    [Fact]
    public async Task RowCounts_UseThousandsSeparators_InTheTruncationNotice()
    {
        var tab = await _t.NewTab("SELECT id FROM big");
        tab.RowLimit = 10;

        await tab.RunScriptAsync();

        Assert.Equal("Showing first 10 rows", tab.Results[0].TruncatedNotice);
        Assert.Equal("10 rows", tab.RowLimitText);
        Assert.False(tab.IsDefaultRowLimit);
        tab.RowLimit = 5000;
        Assert.Equal("5,000 rows", tab.RowLimitText);
        tab.RowLimit = ScriptTabViewModel.DefaultRowLimit;
        Assert.Equal("1,000 rows", tab.RowLimitText);
        Assert.True(tab.IsDefaultRowLimit);
    }

    [Fact]
    public async Task RaiseRowLimit_MultipliesByTen_AndRunsTheLastStatementsAgain()
    {
        var tab = await _t.NewTab("SELECT id FROM big");
        tab.RowLimit = 5;
        await tab.RunScriptAsync();
        Assert.True(tab.Results[0].IsTruncated);

        await tab.RaiseRowLimitAndRerunAsync();

        Assert.Equal(50, tab.RowLimit);
        Assert.Equal(50, Assert.Single(tab.Results).Sets[0].Rows.Count);
        Assert.False(tab.Results[0].IsTruncated);
    }

    [Fact]
    public async Task RaiseRowLimit_AtTheMaximum_SaysSo_AndRunsNothing()
    {
        var tab = await _t.NewTab("SELECT id FROM big");
        tab.RowLimit = ScriptTabViewModel.MaxRowLimit;

        await tab.RaiseRowLimitAndRerunAsync();

        Assert.Empty(tab.Results);
        Assert.Contains("maximum", _t.Status.Message);
        Assert.Equal(ScriptTabViewModel.MaxRowLimit, tab.RowLimit);
    }

    // ---- the run summary ----

    [Fact]
    public async Task Summary_OfASuccessfulScript_SaysHowManyStatementsCompleted()
    {
        var tab = await _t.NewTab("SELECT 1; SELECT 2");

        await tab.RunScriptAsync();

        Assert.Matches(@"^2 statements completed · \d+ ms$", tab.RunSummary);
        Assert.True(tab.RunSummaryIsSuccess);
        Assert.False(tab.RunSummaryIsError);
    }

    [Fact]
    public async Task Summary_OfAFailedScript_SaysWhereItStoppedAndWhatWasNotRun()
    {
        var tab = await _t.NewTab("SELECT 1; SELECT * FROM nope; SELECT 3; SELECT 4");

        await tab.RunScriptAsync();

        Assert.StartsWith("Stopped at statement 2 of 4 · 2 not run", tab.RunSummary);
        Assert.True(tab.RunSummaryIsError);
        Assert.False(tab.RunSummaryIsSuccess);
    }

    [Fact]
    public async Task Summary_IsNotShown_ForASingleStatement()
    {
        var tab = await _t.NewTab("SELECT 1");

        await tab.RunStatementAsync();

        Assert.Null(tab.RunSummary);
        Assert.False(tab.HasRunSummary);
    }

    [Fact]
    public async Task Summary_OfACancelledRun_SaysSo()
    {
        var tab = await _t.NewTab(ScriptTabFixture.Endless);
        var run = tab.RunStatementAsync();
        await Eventually(() => tab.IsRunning, "the run started");

        tab.Cancel();
        await run;

        Assert.True(tab.RunSummaryIsCancelled);
        Assert.False(tab.RunSummaryIsSuccess);
        Assert.Contains("cancelled", tab.RunSummary);
    }

    [Fact]
    public async Task ARunClearsTheSummaryOfThePreviousOne()
    {
        var tab = await _t.NewTab("SELECT 1; SELECT 2");
        await tab.RunScriptAsync();
        Assert.NotNull(tab.RunSummary);

        tab.Text = "SELECT 3";
        await tab.RunScriptAsync();

        Assert.Null(tab.RunSummary);
    }

    // ---- what is running ----

    [Fact]
    public async Task WhileRunning_TheTabSaysWhatRuns_WhenItStarted_AndWhyTheRunButtonsAreOff()
    {
        var tab = await _t.NewTab(ScriptTabFixture.Endless);
        Assert.Null(tab.RunDisabledReason);
        Assert.Null(tab.RunStartedAt);

        var run = tab.RunStatementAsync();
        await Eventually(() => tab.IsRunning, "the run started");

        Assert.NotNull(tab.RunStartedAt);
        Assert.NotNull(tab.RunProgressText);
        Assert.Contains("Stop it first", tab.RunDisabledReason);
        Assert.False(tab.CanRun);

        tab.Cancel();
        await run;

        Assert.Null(tab.RunStartedAt);
        Assert.Null(tab.RunProgressText);
        Assert.Null(tab.RunDisabledReason);
    }

    [Fact]
    public async Task ProgressText_NamesTheStatementOfAScript()
    {
        var tab = await _t.NewTab("SELECT 1; " + ScriptTabFixture.Endless + "; SELECT 3");
        var run = tab.RunScriptAsync();
        await Eventually(() => tab.RunProgressText == "Running statement 2 of 3", "statement 2 running");

        tab.Cancel();
        await run;
    }

    // ---- errors ----

    [Fact]
    public async Task AFailedStatement_SaysWhichOneAndOnWhichLine()
    {
        var tab = await _t.NewTab("SELECT 1;\n\nSELECT * FROM nope;");

        await tab.RunScriptAsync();

        var failed = tab.Results[1];
        Assert.True(failed.IsFailure);
        Assert.False(failed.IsBlocked);
        Assert.Equal("Statement 2 failed · line 3", failed.ErrorTitle);
        Assert.Contains("nope", failed.ErrorDetail);
        Assert.True(failed.HasRange);
        Assert.Equal(tab.Text.IndexOf("SELECT * FROM nope", StringComparison.Ordinal), failed.StartOffset);
    }

    [Fact]
    public async Task GoToStatement_AsksTheEditorToSelectIt_WhileTheTextIsUnchanged()
    {
        var tab = await _t.NewTab("SELECT 1;\nSELECT * FROM nope;");
        await tab.RunScriptAsync();
        (int Start, int End)? revealed = null;
        tab.RevealRequested += (s, e) => revealed = (s, e);

        tab.GoToStatement(tab.Results[1]);

        Assert.Equal((tab.Text.IndexOf("SELECT * FROM", StringComparison.Ordinal), tab.Text.Length - 1), revealed);   // up to the ';'
    }

    [Fact]
    public async Task GoToStatement_SaysSo_WhenTheScriptWasEditedSinceTheRun()
    {
        var tab = await _t.NewTab("SELECT 1;\nSELECT * FROM nope;");
        await tab.RunScriptAsync();
        var raised = false;
        tab.RevealRequested += (_, _) => raised = true;
        Assert.True(tab.ResultsMatchText);

        tab.Text = "-- moved\n" + tab.Text;
        tab.GoToStatement(tab.Results[1]);

        Assert.False(tab.ResultsMatchText);
        Assert.False(raised);
        Assert.Contains("changed since this run", _t.Status.Message);
    }

    [Fact]
    public async Task ResultsOfASelection_PointIntoTheWholeScript()
    {
        var tab = await _t.NewTab("SELECT 1 AS a;\nSELECT 2 AS b;");
        var start = tab.Text.IndexOf("SELECT 2", StringComparison.Ordinal);
        tab.Selection = (start, tab.Text.Length);

        await tab.RunStatementAsync();

        var result = Assert.Single(tab.Results);
        Assert.Equal(start, result.StartOffset);
        Assert.Equal(2, result.StartLine);
    }

    [Fact]
    public async Task AStatementTheReadOnlyGuardRefuses_IsABlock_NotAnError()
    {
        _readOnly = new ScriptTabFixture(readOnly: true);
        var tab = await _readOnly.NewTab("UPDATE t SET name = 'x'");

        await tab.RunScriptAsync();

        var result = Assert.Single(tab.Results);
        Assert.True(tab.IsReadOnly);
        Assert.True(result.IsBlocked);
        Assert.False(result.IsFailure);
        Assert.Equal("Blocked", result.StatusText);
        Assert.StartsWith("Blocked", result.ErrorTitle);
        Assert.Contains("Turn read-only off", result.ErrorDetail);
        Assert.False(((IResultTab)result).ShowFailure);
        Assert.True(((IResultTab)result).IsBlocked);
    }

    [Fact]
    public void ErrorBanner_NamesALostConnection_AndExplainsReconnecting()
    {
        var tab = new ScriptTabViewModel(_t.Config, null, _t.Hooks, 1);
        Assert.Null(tab.ErrorTitle);

        tab.ErrorMessage = ScriptTabViewModel.ConnectionClosedMessage;
        tab.NeedsReconnect = true;

        Assert.Equal("Connection lost", tab.ErrorTitle);
        Assert.Contains("temp tables", tab.ErrorHint);

        tab.NeedsReconnect = false;
        tab.ErrorMessage = null;
        tab.ErrorIsWarning = true;
        Assert.Equal("Connection reset", tab.ErrorTitle);
        tab.ErrorMessage = "another failure";   // a new message is a failure again, not a caution
        Assert.False(tab.ErrorIsWarning);
        Assert.Null(tab.ErrorTitle);
    }

    [Fact]
    public async Task DismissingARunLevelError_ClearsTheCautionToo()
    {
        var tab = await _t.NewTab("SELECT 1");
        tab.ErrorMessage = "x";
        tab.ErrorIsWarning = true;

        tab.DismissErrorCommand.Execute(null);

        Assert.Null(tab.ErrorMessage);
        Assert.False(tab.ErrorIsWarning);
    }

    // ---- several result sets, grids ----

    [Fact]
    public void ResultSets_GetATitleWhenThereAreSeveral()
    {
        var statement = new ScriptStatement("SELECT 1; SELECT 2", 0, 18);
        ResultSet Set(int n) => new(["a"], Enumerable.Range(0, n).Select(i => new object?[] { i }).ToList(), false);
        var several = new StatementResultViewModel(1, new StatementResult(statement, [Set(2), Set(3)], null, TimeSpan.FromMilliseconds(5), null), 1000);
        var one = new StatementResultViewModel(1, new StatementResult(statement, [Set(2)], null, TimeSpan.FromMilliseconds(5), null), 1000);

        Assert.True(several.HasSeveralSets);
        Assert.Equal(["Result 1 · 2 rows", "Result 2 · 3 rows"], several.Sets.Select(s => s.Title).ToArray());
        Assert.All(several.Sets, s => Assert.True(s.ShowTitle));
        Assert.Equal("2 result sets", several.StatusMain);
        Assert.Equal("2 + 3 rows · 5 ms", several.StatusDetail);
        Assert.False(one.HasSeveralSets);
        Assert.False(one.Sets[0].ShowTitle);
    }

    // ---- connection badge ----

    [Fact]
    public async Task ConnectionDescription_NamesTheEngine_AndTheReadOnlyFlag()
    {
        var tab = await _t.NewTab("SELECT 1");
        Assert.Contains("Lite", tab.ConnectionDescription);
        Assert.DoesNotContain("read-only", tab.ConnectionDescription);

        _readOnly = new ScriptTabFixture(readOnly: true);
        var locked = await _readOnly.NewTab("SELECT 1");
        Assert.EndsWith("read-only", locked.ConnectionDescription);
        Assert.True(locked.IsReadOnly);
    }

    // ---- keys ----

    [Theory]
    [InlineData(Key.Escape, KeyModifiers.None, ScriptKeyAction.CancelIfBusy)]
    [InlineData(Key.OemQuestion, KeyModifiers.Control, ScriptKeyAction.ToggleComment)]
    [InlineData(Key.F6, KeyModifiers.None, ScriptKeyAction.SwitchPane)]
    [InlineData(Key.Enter, KeyModifiers.Control, ScriptKeyAction.RunStatement)]
    [InlineData(Key.Escape, KeyModifiers.Shift, ScriptKeyAction.None)]
    public void Keys_Resolve(Key key, KeyModifiers modifiers, ScriptKeyAction expected) =>
        Assert.Equal(expected, ScriptTabKeys.Resolve(key, modifiers));

    [Fact]
    public void CtrlDigit_SelectsTheNthResultTab()
    {
        Assert.Equal(0, ScriptTabKeys.ResultTabIndex(Key.D1, KeyModifiers.Control));
        Assert.Equal(8, ScriptTabKeys.ResultTabIndex(Key.D9, KeyModifiers.Control));
        Assert.Equal(2, ScriptTabKeys.ResultTabIndex(Key.NumPad3, KeyModifiers.Control));
        Assert.Null(ScriptTabKeys.ResultTabIndex(Key.D0, KeyModifiers.Control));
        Assert.Null(ScriptTabKeys.ResultTabIndex(Key.D1, KeyModifiers.None));
        Assert.Null(ScriptTabKeys.ResultTabIndex(Key.D1, KeyModifiers.Control | KeyModifiers.Shift));
    }

    [Fact]
    public void TheShortcutsFlyout_ListsTheRunChords()
    {
        var keys = ScriptTabKeys.Shortcuts.Select(s => s.Keys).ToList();
        Assert.Contains("Ctrl+Enter", keys);
        Assert.Contains("F5", keys);
        Assert.Contains("Esc", keys);
        Assert.Contains("Ctrl+F", keys);
    }
}
