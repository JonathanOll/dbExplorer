using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.Tests.Upgrade;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using DbExplorer.Core.Upgrade;

namespace DbExplorer.App.Tests;

internal sealed class FakeProductionDialogs : IProductionDialogs
{
    public bool Confirm { get; set; }
    public bool DontAskAgain { get; set; }
    public List<ProductionWriteRequest> Requests { get; } = [];

    public Task<ProductionWriteAnswer> ConfirmProductionWriteAsync(ProductionWriteRequest request)
    {
        Requests.Add(request);
        return Task.FromResult(new ProductionWriteAnswer(Confirm, DontAskAgain));
    }
}

public class ProductionGuardTests
{
    private static ConnectionConfig Prod(string name = "Shop") =>
        TreeFixture.Conn(name) with { Environment = DbEnvironment.Production };

    private readonly FakeProductionDialogs _dialogs = new();

    [Fact]
    public async Task OtherEnvironments_AreNotAsked()
    {
        var guard = new ProductionGuard(_dialogs);

        Assert.True(await guard.AllowAsync([TreeFixture.Conn("a") with { Environment = DbEnvironment.PreProduction }], "x", ["y"]));
        Assert.Empty(_dialogs.Requests);
    }

    [Fact]
    public async Task WithoutADialog_ProductionIsRefused() =>
        Assert.False(await new ProductionGuard().AllowAsync([Prod()], "x", ["y"]));

    [Fact]
    public async Task TheLatestSettings_CanMakeAConnectionProduction()
    {
        var old = TreeFixture.Conn("a");
        var guard = new ProductionGuard(_dialogs, _ => old with { Environment = DbEnvironment.Production });

        Assert.False(await guard.AllowAsync([old], "x", ["y"]));
        Assert.Single(_dialogs.Requests);
    }

    [Fact]
    public async Task DontAskAgain_LastsFiveMinutes_ForThatTabAndConnectionOnly()
    {
        var now = new DateTime(2026, 1, 1);
        var guard = new ProductionGuard(_dialogs, now: () => now);
        var shop = Prod();
        var tab = new ProductionApproval();
        _dialogs.Confirm = _dialogs.DontAskAgain = true;

        Assert.True(await guard.AllowAsync([shop], "x", ["y"], tab));
        Assert.True(await guard.AllowAsync([shop], "x", ["y"], tab));
        Assert.Single(_dialogs.Requests);

        await guard.AllowAsync([Prod("Other")], "x", ["y"], tab);
        await guard.AllowAsync([shop], "x", ["y"], new ProductionApproval());
        Assert.Equal(3, _dialogs.Requests.Count);

        now += ProductionGuard.SnoozeDuration;
        await guard.AllowAsync([shop], "x", ["y"], tab);
        Assert.Equal(4, _dialogs.Requests.Count);
    }

    [Fact]
    public void Summary_ShortensLongLists()
    {
        var request = new ProductionWriteRequest([], "x", Enumerable.Range(1, 11).Select(i => $"DELETE\n  FROM t{i}").ToList(), false);

        Assert.Equal("DELETE FROM t1", request.Summary[0]);
        Assert.Equal("… and 3 more", request.Summary[^1]);
    }

    [AvaloniaFact]
    public void Dialog_DefaultsToCancel_AndEscapeCancels()
    {
        var window = ProductionDialog.Create(new ProductionWriteRequest(
            [ProductionTarget.Of(Prod())], "Run 1 statement that may write", ["DELETE FROM t"], true));
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var cancel = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Cancel");
        Assert.True(cancel.IsDefault);
        Assert.True(cancel.IsCancel);
        Assert.True(cancel.IsFocused);
        Assert.False(window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Write on production").IsDefault);

        cancel.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Dispatcher.UIThread.RunJobs();
        Assert.True(closed);
    }
}

public class ProductionScriptTests : IDisposable
{
    private const string Write = "UPDATE t SET name = 'x' WHERE id = 1";
    private readonly ScriptTabFixture _t = new();
    private readonly FakeProductionDialogs _dialogs = new();

    public void Dispose() => _t.Dispose();

    private async Task<ScriptTabViewModel> Tab(string text, bool withDialogs = true)
    {
        var guard = withDialogs
            ? new ProductionGuard(_dialogs, _ => _t.Config with { Environment = DbEnvironment.Production })
            : new ProductionGuard(latest: _ => _t.Config with { Environment = DbEnvironment.Production });
        var session = await _t.Sessions.GetOrOpenAsync(_t.Config, CancellationToken.None);
        return _t.Track(new ScriptTabViewModel(_t.Config, session, _t.Hooks with { Guard = guard }, 1) { Text = text });
    }

    private long Changed() => _t.Scalar("SELECT count(*) FROM t WHERE name = 'x'");

    [Fact]
    public async Task CancelledWrite_RunsNothing()
    {
        var tab = await Tab("SELECT 1; " + Write);

        await tab.RunScriptAsync();

        Assert.Empty(tab.Results);
        Assert.Equal(0, Changed());
        var request = Assert.Single(_dialogs.Requests);
        Assert.Equal("Run 1 statement that may write", request.Action);
        Assert.Equal("Lite", Assert.Single(request.Targets).Connection);
        Assert.False(tab.IsRunning);
    }

    [Fact]
    public async Task ConfirmedWrite_Runs()
    {
        _dialogs.Confirm = true;
        var tab = await Tab(Write);

        await tab.RunScriptAsync();

        Assert.Equal(1, Changed());
    }

    [Fact]
    public async Task PlainReads_AndEstimatedPlans_NeverAsk()
    {
        var read = await Tab("SELECT * FROM t");
        await read.RunScriptAsync();
        var plan = await Tab(Write);
        await plan.ShowEstimatedPlanAsync();

        Assert.Empty(_dialogs.Requests);
        Assert.Equal(0, Changed());
    }

    [Fact]
    public async Task WholeSqlServerBatch_IsConfirmed_AndListsTheStatementsInside()
    {
        var config = new ConnectionConfig(Guid.NewGuid(), "Shop", DbKind.SqlServer, null, "nowhere", null, "shop", null, null,
            false, false, false) { Environment = DbEnvironment.Production };
        var resolved = 0;
        var hooks = new ScriptTabHooks(_ => { resolved++; return Task.FromResult<Session?>(null); },
            Guard: new ProductionGuard(_dialogs));
        var text = "DECLARE @id int = 1;\nSELECT @id;\nDELETE FROM t WHERE id = @id";
        var tab = _t.Track(new ScriptTabViewModel(config, null, hooks, 1) { Text = text });

        await tab.RunScriptAsync();
        tab.Selection = (0, text.Length);
        await tab.RunStatementAsync();

        Assert.Equal(2, _dialogs.Requests.Count);
        Assert.All(_dialogs.Requests, r => Assert.Contains("DELETE FROM t WHERE id = @id", r.Items));
        Assert.Equal(0, resolved);   // refused: not even a connection
    }

    [Fact]
    public async Task WithoutADialog_TheWriteDoesNotRun()
    {
        var tab = await Tab(Write, withDialogs: false);

        await tab.RunScriptAsync();

        Assert.Equal(0, Changed());
    }
}

public class ProductionTableTests : IDisposable
{
    private readonly TableTabFixture _t = new();
    private readonly FakeProductionDialogs _dialogs = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task GridApply_AsksFirst_AndKeepsTheEditsWhenCancelled()
    {
        var session = await _t.OpenSession();
        var guard = new ProductionGuard(_dialogs, _ => _t.Config with { Environment = DbEnvironment.Production });
        var hooks = new TableTabHooks(_ => Task.FromResult<Session?>(session), (_, _, _) => Task.CompletedTask,
            new FakeEditDialogs(), Guard: guard);
        var tab = new TableTabViewModel(session, new TableRef(null, "customers"), hooks);
        await tab.LoadAsync();
        var name = tab.FindColumn("name")!.Value;
        tab.SetCell(0, name, "Alicia");

        await tab.SaveAsync();

        Assert.True(tab.HasPendingEdits);
        Assert.False(tab.IsSaving);
        Assert.Contains("customers", Assert.Single(_dialogs.Requests).Action);

        _dialogs.Confirm = true;
        await tab.SaveAsync();

        Assert.False(tab.HasPendingEdits);
    }
}

public class ProductionUpgradeAndFolderTests : IDisposable
{
    private readonly UpgradeFixture _f = new();
    private readonly TreeFixture _fx = new();
    private readonly FakeProductionDialogs _dialogs = new();

    public void Dispose()
    {
        _f.Dispose();
        _fx.Dispose();
    }

    [Fact]
    public async Task Upgrade_OnProduction_AsksBeforeContactingAnything()
    {
        var db = _f.Db("A") with { Environment = DbEnvironment.Production };
        var vm = new UpgradeViewModel(_f.Catalog(), [UpgradeFixture.One(db)], _f.Resolve, requireSqlServer: false,
            guard: new ProductionGuard(_dialogs));
        vm.SprintText = "143";
        await vm.LoadCommand.ExecuteAsync(null);
        vm.RequestRunCommand.Execute(null);
        Assert.Contains("A", vm.ProductionTargetsText);

        await vm.ConfirmRunCommand.ExecuteAsync(null);

        Assert.Equal(UpgradePhase.Confirm, vm.Phase);
        Assert.Equal(0, _f.ResolveCount);
        Assert.Empty(UpgradeFixture.Rows(db));
        var request = Assert.Single(_dialogs.Requests);
        Assert.False(request.CanSnooze);
        Assert.NotEmpty(request.Items);
    }

    [Fact]
    public async Task Folder_SetEnvironment_ChangesItsConnectionsOnly()
    {
        var config = new AppConfig(["F"], [TreeFixture.Conn("in", "F"), TreeFixture.Conn("out")], new());
        var tree = _fx.CreateTree(config);
        _fx.Dialogs.ChooseResult = (int)DbEnvironment.Production;

        await tree.SetFolderEnvironmentCommand.ExecuteAsync(tree.Nodes.OfType<FolderNodeViewModel>().Single());

        Assert.True(tree.Config.Connections.Single(c => c.Name == "in").IsProduction);
        Assert.False(tree.Config.Connections.Single(c => c.Name == "out").IsProduction);
    }
}
