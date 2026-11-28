using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.Tests;

/// <summary>A statement the server answers with nothing (DECLARE, SET, USE...) gets no result tab.</summary>
public class QuietStatementTests : IDisposable
{
    private readonly ScriptTabFixture _t = new();

    public void Dispose() => _t.Dispose();

    // No rows and no count for these, like DECLARE / SET / USE on SQL Server (DDL included, whatever SQLite's change counter says).
    private const string Quiet = "PRAGMA foreign_keys = ON;\nCREATE TEMP TABLE q (a);\nDROP TABLE q";

    [Fact]
    public async Task QuietStatements_GetNoTab_ButStillCount()
    {
        var tab = await _t.NewTab(Quiet + ";\nSELECT 1 AS a;\nPRAGMA foreign_keys = OFF");

        await tab.RunScriptAsync();

        Assert.Equal(5, tab.Results.Count);   // marks, numbering and durations still cover every statement
        var shown = Assert.Single(tab.ResultTabs);
        Assert.Same(tab.Results[3], shown);
        Assert.Same(shown, tab.SelectedResultTab);
        Assert.Same(shown, tab.SelectedResult);
        Assert.StartsWith("5 statements completed", tab.RunSummary);
        Assert.Null(tab.QuietRunText);
    }

    [Fact]
    public async Task RunWithNothingToShow_SaysSoCalmly()
    {
        var tab = await _t.NewTab(Quiet);

        await tab.RunScriptAsync();

        Assert.Empty(tab.ResultTabs);
        Assert.Null(tab.SelectedResultTab);
        Assert.StartsWith("3 statements completed", tab.QuietRunText);
    }

    [Fact]
    public async Task SingleQuietStatement_SaysSo_AndTheNextRunClearsIt()
    {
        var tab = await _t.NewTab("BEGIN");

        await tab.RunScriptAsync();
        Assert.Matches(@"^Statement completed · \d+ ms$", tab.QuietRunText);

        tab.Text = "SELECT 1";
        await tab.RunScriptAsync();
        Assert.Null(tab.QuietRunText);
        Assert.Single(tab.ResultTabs);
    }

    [Fact]
    public async Task RowsAffectedAndErrors_KeepTheirTab()
    {
        var tab = await _t.NewTab("BEGIN;\nUPDATE t SET name = 'z' WHERE id = 99;\nSELECT * FROM nope");

        await tab.RunScriptAsync();

        Assert.Equal([tab.Results[1], tab.Results[2]], tab.ResultTabs.Cast<StatementResultViewModel>().ToArray());
        Assert.Same(tab.Results[2], tab.SelectedResultTab);
        Assert.StartsWith("Statement 3 of 3 failed", tab.RunSummary);
    }

    [Fact]
    public async Task ServerMessages_WithoutStatistics_ShowInAMessagesTab()
    {
        var connection = new StatsConnection { Says = sql => sql == "SELECT 1" ? ["PRINT hello"] : [] };
        var config = TreeFixture.Conn("Sql", kind: DbKind.SqlServer);
        var session = new Session(config, new StatsDriver(DbKind.SqlServer, () => connection), [], null);
        var tab = new ScriptTabViewModel(config, session, new ScriptTabHooks(_ => Task.FromResult<Session?>(session)), 1) { Text = "SELECT 1" };

        await tab.RunScriptAsync();

        Assert.Equal(["1 · SELECT 1", "Messages"], tab.ResultTabs.Select(t => t.Header).ToArray());
        Assert.Same(tab.Results[0], tab.SelectedResultTab);
        tab.Close();
    }

    [AvaloniaFact]
    public async Task View_ShowsTheSuccessSummary_NotTheRunHint()
    {
        var tab = await _t.NewTab("BEGIN");
        var view = new ScriptTabView { DataContext = tab };
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();

        await tab.RunScriptAsync();
        Dispatcher.UIThread.RunJobs();

        var shown = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains(tab.QuietRunText, shown);
        Assert.DoesNotContain("Run a query to see its results", shown);
        window.Close();
    }
}
