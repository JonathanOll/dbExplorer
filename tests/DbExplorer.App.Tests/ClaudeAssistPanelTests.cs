using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Claude;
using DbExplorer.Core.Claude.Assist;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests;

public sealed class ClaudeAssistPanelTests : IDisposable
{
    /// <summary>Stands for the claude command: no process is started.</summary>
    private sealed class FakeClaude : IClaudeProcessRunner
    {
        public List<string> Inputs { get; } = [];
        public string Answer { get; set; } = "All fine.";
        public TaskCompletionSource? Hold { get; set; }
        public bool Cancelled { get; private set; }

        public async Task<ClaudeProcessExit> RunAsync(ClaudeProcessRequest request, Action<string> onLine, CancellationToken ct)
        {
            Inputs.Add(request.Input);
            if (Hold is { } hold)
            {
                try { await hold.Task.WaitAsync(ct); }
                catch (OperationCanceledException)
                {
                    Cancelled = true;
                    throw;
                }
            }
            onLine("""{"type":"system","subtype":"init","tools":[],"mcp_servers":[]}""");
            onLine(JsonSerializer.Serialize(new { type = "result", subtype = "success", is_error = false, result = Answer }));
            return new ClaudeProcessExit(0, "");
        }
    }

    private readonly ScriptTabFixture _fx = new();
    private readonly FakeClaude _claude = new();
    private readonly ConnectionTreeViewModel _tree;
    private readonly MainViewModel _main;
    private readonly ConnectionConfig _data, _schema, _prod, _hidden;

    public ClaudeAssistPanelTests()
    {
        ConnectionConfig Conn(string name) => TreeFixture.Conn(name, database: _fx.DbFile);
        _data = Conn("data");
        _schema = Conn("schema");
        _prod = Conn("prod") with { Environment = DbEnvironment.Production };
        _hidden = Conn("hidden");
        var config = AppConfig.Empty();
        config.Connections.AddRange([_data, _schema, _prod, _hidden]);
        config.Claude = new ClaudeSettings
        {
            Enabled = true,
            Connections =
            {
                [_data.Id] = ClaudeAccess.SchemaAndData, [_schema.Id] = ClaudeAccess.Schema, [_prod.Id] = ClaudeAccess.SchemaAndData,
            },
        };
        _tree = _fx.Fx.CreateTree(config);
        _main = new MainViewModel(_tree, _fx.Status);
        _main.UseScriptTabs(_fx.Files, _fx.Store);
        _main.UseClaude(new ClaudeHooks(
            @"C:\app\DbExplorer.exe", _fx.Fx.Dir, Path.Combine(_fx.Fx.Dir, "claude", "mcp.json"), Path.Combine(_fx.Fx.Dir, "library"),
            new ClaudeActivityLog(Path.Combine(_fx.Fx.Dir, "activity.jsonl")), () => "claude", _ => { }, _fx.Fx.Dialogs,
            Runner: _claude));
    }

    public void Dispose()
    {
        foreach (var session in _fx.Fx.Sessions.Open) _fx.Fx.Sessions.Close(session.Config.Id);   // releases the file
        _fx.Fx.Dispose();
    }

    private ScriptTabViewModel NewTab(ConnectionConfig connection, string text)
    {
        _main.ActiveTab = null;   // a new script takes the selected connection only without a tab in front
        _tree.SelectedNode = _tree.NodeOf(connection.Id);
        _main.NewScript();
        var tab = Assert.IsType<ScriptTabViewModel>(_main.ActiveTab);
        Assert.Equal(connection.Id, tab.Connection.Id);
        tab.Text = text;
        return tab;
    }

    private async Task<ClaudePanelViewModel> Sent(Task started, ScriptTabViewModel tab)
    {
        await started;
        var panel = tab.ClaudePanel!;
        if (panel.IsPreview) await panel.SendCommand.ExecuteAsync(null);
        return panel;
    }

    [Fact]
    public async Task TheFirstQuestionOfAConnection_IsShownBeforeItIsSent()
    {
        var tab = NewTab(_schema, "SELECT * FROM t WHERE id = 12345");

        await tab.OptimiseQueryAsync();

        var panel = tab.ClaudePanel!;
        Assert.True(panel.IsPreview);
        Assert.Empty(_claude.Inputs);
        Assert.Contains("FROM t WHERE id = __n1__", panel.SentText);
        Assert.DoesNotContain("12345", panel.SentText);
        Assert.Contains("Schema only", panel.AccessText);

        await panel.SendCommand.ExecuteAsync(null);

        Assert.True(panel.IsDone);
        Assert.Equal("All fine.", panel.Answer);
        Assert.EndsWith(Assert.Single(_claude.Inputs), panel.SentText);
        Assert.Equal(ClaudeAccess.Schema, _tree.Config.Claude.Previewed[_schema.Id]);

        await tab.OptimiseQueryAsync();   // the next one goes at once
        Assert.True(tab.ClaudePanel!.IsDone);
        Assert.Equal(2, _claude.Inputs.Count);
    }

    [Fact]
    public async Task Apply_RestoresTheValues_AsOneEdit_AndRunsNothing()
    {
        var tab = NewTab(_schema, "DELETE FROM t WHERE id = 2;");
        _claude.Answer = "Better:\n```sql replacement\nDELETE FROM t\nWHERE id = __n1__;\n```";
        var edits = new List<ScriptTextEdit>();
        tab.FormatRequested += e =>
        {
            edits.Add(e);
            tab.Text = tab.Text.Remove(e.Start, e.Length).Insert(e.Start, e.Text);
        };

        var panel = await Sent(tab.OptimiseQueryAsync(), tab);
        Assert.True(panel.CanApply);
        Assert.Contains(panel.Diff, r => r.IsAdded && r.Text == "WHERE id = 2");
        panel.ApplyCommand.Execute(null);

        Assert.Single(edits);
        Assert.Equal("DELETE FROM t\nWHERE id = 2;", tab.Text.ReplaceLineEndings("\n"));   // one ";", not two
        Assert.False(panel.HasProposal);
        Assert.Empty(tab.Results);
        Assert.Equal(3, _fx.Scalar("SELECT COUNT(*) FROM t"));
    }

    [Fact]
    public async Task Reject_LeavesTheScript_AndAChangedScriptIsNotOverwritten()
    {
        var tab = NewTab(_data, "SELECT 1");
        _claude.Answer = "```sql replacement\nSELECT 2\n```";

        var panel = await Sent(tab.OptimiseQueryAsync(), tab);
        panel.RejectCommand.Execute(null);
        Assert.Equal("SELECT 1", tab.Text);
        Assert.False(panel.HasProposal);

        panel = await Sent(tab.OptimiseQueryAsync(), tab);
        tab.Text = "SELECT 100";
        panel.ApplyCommand.Execute(null);
        Assert.Equal("SELECT 100", tab.Text);
        Assert.False(panel.CanApply);
        Assert.Contains("changed", panel.ApplyDisabledReason);
    }

    [Fact]
    public async Task ExplainError_SendsNoDataOfASchemaOnlyConnection()
    {
        var tab = NewTab(_schema, "SELECT nmae FROM t WHERE id = 4242");
        await tab.RunScriptAsync();

        var panel = await Sent(tab.ExplainErrorAsync(), tab);

        var input = Assert.Single(_claude.Inputs);
        Assert.Contains("no such column: nmae", input);
        Assert.DoesNotContain("4242", input);
        Assert.True(panel.IsDone);
    }

    [Fact]
    public async Task Generate_AsksForADescription_ThenInsertsWhenApplied()
    {
        var tab = NewTab(_data, "-- top\n");
        tab.CaretOffset = tab.Text.Length;
        _claude.Answer = "```sql replacement\nSELECT * FROM t;\n```";

        await tab.GenerateSqlAsync();
        var panel = tab.ClaudePanel!;
        Assert.True(panel.IsInput);
        Assert.False(panel.ContinueCommand.CanExecute(null));
        panel.Request = "all rows of t";
        await panel.ContinueCommand.ExecuteAsync(null);
        await panel.SendCommand.ExecuteAsync(null);
        panel.ApplyCommand.Execute(null);

        Assert.Equal("-- top\nSELECT * FROM t;", tab.Text);
        Assert.Contains("all rows of t", _claude.Inputs[0]);
    }

    [Fact]
    public async Task AnalysePlan_ShowsTheAnswerOnly()
    {
        var tab = NewTab(_data, "SELECT * FROM t");
        await tab.ShowEstimatedPlanAsync();
        _claude.Answer = "```sql replacement\nCREATE INDEX i ON t(id)\n```";
        var plan = Assert.IsType<PlanResultTab>(tab.SelectedResultTab);
        Assert.True(plan.ShowAnalyse);

        var panel = await Sent(plan.Analyse!(), tab);

        Assert.True(panel.IsDone);
        Assert.False(panel.HasProposal);
        Assert.Contains("name=\"plan\"", _claude.Inputs[0]);
    }

    [Fact]
    public async Task ProductionAndConnectionsNotAllowed_SendNothing()
    {
        foreach (var connection in new[] { _prod, _hidden })
        {
            var tab = NewTab(connection, "SELECT 1");
            Assert.False(tab.CanAskClaude);
            await tab.OptimiseQueryAsync();
            Assert.Null(tab.ClaudePanel);
        }
        Assert.Empty(_claude.Inputs);
    }

    [Fact]
    public async Task TurningTheIntegrationOff_HidesEverything_AndStopsAWaitingSend()
    {
        var tab = NewTab(_data, "SELECT 1");
        await tab.OptimiseQueryAsync();
        var panel = tab.ClaudePanel!;

        _tree.SetClaude(_tree.Config.Claude with { Enabled = false });
        await panel.SendCommand.ExecuteAsync(null);
        Assert.True(panel.IsFailed);
        Assert.Empty(_claude.Inputs);

        _main.SyncClaudeListener();
        Assert.Null(tab.ClaudePanel);
        Assert.False(tab.ClaudeEnabled);
    }

    [Fact]
    public async Task Stop_CancelsTheRun_AndClosingTheTabToo()
    {
        var tab = NewTab(_data, "SELECT 1");
        _claude.Hold = new TaskCompletionSource();
        await tab.OptimiseQueryAsync();
        var panel = tab.ClaudePanel!;
        var sending = panel.SendCommand.ExecuteAsync(null);
        await Wait(() => _claude.Inputs.Count == 1);
        Assert.True(panel.IsRunning);

        panel.StopCommand.Execute(null);
        await sending;

        Assert.True(panel.IsFailed);
        Assert.Equal("Stopped.", panel.Message);
        await Wait(() => _claude.Cancelled);

        var again = tab.OptimiseQueryAsync();
        await Wait(() => _claude.Inputs.Count == 2);
        tab.Close();
        await again;
        Assert.Null(tab.ClaudePanel);
    }

    private static async Task Wait(Func<bool> until)
    {
        for (var i = 0; i < 500 && !until(); i++) await Task.Delay(10);
        Assert.True(until());
    }

    [Fact]
    public async Task UpgradeReview_SendsTheTickedScriptsWithoutTheirValues_AndNeedsEveryDatabaseAllowed()
    {
        var folder = Path.Combine(_fx.Fx.Dir, "dbupdate", "v0860-sprint 143");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "adj - 001 - add.sql"), "ALTER TABLE t ADD ref nvarchar(50) NULL");
        File.WriteAllText(Path.Combine(folder, "adj - 002 - data.sql"), "UPDATE t SET name = 'John Doe' WHERE id = 777");
        var catalog = DbExplorer.Core.Upgrade.DbUpdateCatalog.Scan(Path.GetDirectoryName(folder)!);
        UpgradeViewModel Dialog(params ConnectionConfig[] members) => new(catalog,
            [new DbExplorer.Core.Upgrade.UpgradeTarget("target", members, null)], (_, _) => Task.FromResult<Core.Sessions.Session?>(null));

        var vm = Dialog(_schema, _data);
        vm.UseClaude(_main.Assistant);
        Assert.False(vm.ShowClaudeReview);   // nothing loaded yet
        vm.SprintText = "143";
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.True(vm.ShowClaudeReview);
        Assert.True(vm.CanReviewWithClaude);

        await vm.ReviewWithClaudeAsync();
        var panel = vm.ClaudePanel!;
        Assert.True(panel.IsPreview);
        await panel.SendCommand.ExecuteAsync(null);

        var input = Assert.Single(_claude.Inputs);
        Assert.Contains("nvarchar(50)", input);
        Assert.DoesNotContain("John Doe", input);   // the lowest access of the target applies
        Assert.DoesNotContain("777", input);
        Assert.False(panel.HasProposal);

        var excluded = Dialog(_schema, _prod);
        excluded.UseClaude(_main.Assistant);
        excluded.SprintText = "143";
        await excluded.LoadCommand.ExecuteAsync(null);
        Assert.False(excluded.CanReviewWithClaude);
        Assert.Contains("prod", excluded.ClaudeReviewDisabledReason);
        await excluded.ReviewWithClaudeAsync();
        Assert.Null(excluded.ClaudePanel);
    }

    [Fact]
    public void ThePaletteOffersTheQuestions()
    {
        NewTab(_prod, "SELECT 1");

        var command = _main.Registry.Snapshot().Single(c => c.Id == "script.claude-optimise");

        Assert.NotNull(command.DisabledReason);
    }

    [AvaloniaFact]
    public async Task CtrlEnterInTheRequestBox_Continues_AndRunsNoSql()
    {
        var tab = NewTab(_data, "DELETE FROM t");
        var view = new ScriptTabView { DataContext = tab };
        var window = new Window { Content = view, Width = 1200, Height = 700 };
        window.Show();
        try
        {
            await tab.GenerateSqlAsync();
            tab.ClaudePanel!.Request = "count the rows";
            Dispatcher.UIThread.RunJobs();
            var box = view.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "RequestBox");
            box.Focus();

            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Control });
            Dispatcher.UIThread.RunJobs();

            Assert.True(tab.ClaudePanel.IsPreview);
            Assert.False(tab.IsRunning);
            Assert.Empty(tab.Results);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheScriptTabShowsTheButton_AndThePanelBesideTheEditor()
    {
        var tab = NewTab(_schema, "SELECT 1");
        var view = new ScriptTabView { DataContext = tab };
        var window = new Window { Content = view, Width = 1200, Height = 700 };
        window.Show();
        try
        {
            Assert.True(view.FindControl<DropDownButton>("ClaudeButton")!.IsEffectivelyVisible);
            _ = tab.OptimiseQueryAsync();
            Dispatcher.UIThread.RunJobs();
            var panel = Assert.Single(view.GetVisualDescendants().OfType<ClaudePanelView>());
            Assert.Contains(panel.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Send to Claude") && b.IsEffectivelyVisible);

            tab.ClaudePanel!.CloseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(view.GetVisualDescendants().OfType<ClaudePanelView>());
        }
        finally
        {
            window.Close();
        }
    }
}
