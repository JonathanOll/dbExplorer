using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.Settings;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Claude;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests;

public sealed class ClaudeIntegrationTests : IDisposable
{
    private sealed class Listener(Action disposed) : IDisposable
    {
        public void Dispose() => disposed();
    }

    private readonly ScriptTabFixture _fx = new();
    private readonly ConnectionTreeViewModel _tree;
    private readonly MainViewModel _main;
    private readonly ConnectionConfig _data, _schema, _prod, _hidden;
    private readonly List<ProcessStartInfo> _started = [];
    private string? _claude = @"C:\tools\claude.exe";
    private int _listening;
    private bool _windowActive;

    public ClaudeIntegrationTests()
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
            new ClaudeActivityLog(Path.Combine(_fx.Fx.Dir, "activity.jsonl")), () => _claude, _started.Add, _fx.Fx.Dialogs,
            _ =>
            {
                _listening++;
                return new Listener(() => _listening--);
            },
            () => _windowActive));
    }

    public void Dispose()
    {
        foreach (var session in _fx.Fx.Sessions.Open) _fx.Fx.Sessions.Close(session.Config.Id);   // releases the file
        _fx.Fx.Dispose();
    }

    private ClaudeAppReply Open(ConnectionConfig connection, string sql, string? title = null) =>
        _main.HandleClaudeRequest(new ClaudeAppRequest(ClaudeAppRequest.OpenScript, connection.Id, title, sql));

    private ClaudeAppReply Read() => _main.HandleClaudeRequest(new ClaudeAppRequest(ClaudeAppRequest.ActiveTab));

    private ScriptTabViewModel NewTab(ConnectionConfig connection, string text)
    {
        Assert.True(Open(connection, "select 0").Ok);
        var tab = Assert.IsType<ScriptTabViewModel>(_main.ActiveTab);
        tab.Text = text;
        return tab;
    }

    [Fact]
    public void OpenScript_ShowsTheSql_WithoutConnectingOrRunning()
    {
        Assert.True(Open(_data, "DELETE FROM t", "clean up").Ok);

        var tab = Assert.IsType<ScriptTabViewModel>(_main.ActiveTab);
        Assert.Equal(MainViewModel.ProposedHeader + "DELETE FROM t", tab.Text);
        Assert.Contains("Claude: clean up", tab.Title);
        Assert.True(tab.HasUnsavedWork);
        Assert.False(tab.IsRunning);
        Assert.Empty(tab.Results);
        Assert.Null(_tree.FindSession(_data.Id));
        Assert.Equal(3, _fx.Scalar("SELECT COUNT(*) FROM t"));
    }

    [Fact]
    public void OpenScript_RefusesWhatIsNotAllowed()
    {
        Assert.False(Open(_prod, "select 1").Ok);
        Assert.False(Open(_hidden, "select 1").Ok);
        Assert.False(Open(_data with { Id = Guid.NewGuid() }, "select 1").Ok);
        Assert.False(Open(_data, "  ").Ok);
        Assert.False(Open(_data, new string('x', 100_001)).Ok);
        _tree.Config.Claude.Enabled = false;
        Assert.Equal(ClaudePolicy.Disabled, Open(_data, "select 1").Message);
        Assert.Equal(ClaudePolicy.Disabled, Read().Message);
        Assert.Empty(_main.Tabs);
    }

    [Fact]
    public void OpenScript_ShowsWhatWouldRun()
    {
        Open(_data, "select 1 -- a\rdelete from t\u2028x\u202Ey\u200Bz\0w", "a\nb\u202E");

        var tab = Assert.IsType<ScriptTabViewModel>(_main.ActiveTab);
        Assert.Equal(MainViewModel.ProposedHeader + "select 1 -- a\ndelete from t\nxyz w", tab.Text);
        Assert.Contains("Claude: a b", tab.Title);
    }

    [Fact]
    public void OpenScript_DoesNotTakeTheFrontWhileTheUserWorksInTheApp()
    {
        Open(_data, "select 1");
        var mine = _main.ActiveTab;
        _windowActive = true;
        Open(_data, "select 2");

        Assert.Same(mine, _main.ActiveTab);
        Assert.Equal(2, _main.Tabs.Count);
    }

    [Fact]
    public void OpenScript_StopsAtTheLimit()
    {
        for (var i = 0; i < MainViewModel.MaxProposedTabs; i++) Assert.True(Open(_data, "select 1").Ok);
        Assert.False(Open(_data, "select 1").Ok);
    }

    [Fact]
    public async Task ActiveTab_SharesSqlAndError_OfAnAllowedConnectionOnly()
    {
        var tab = NewTab(_data, "SELECT nope FROM t");
        await tab.RunScriptAsync();

        var shared = Read().Tab!;
        Assert.Equal("SELECT nope FROM t", shared.Sql);
        Assert.Equal(_data.Id, shared.ConnectionId);
        Assert.Contains("nope", shared.LastError);

        _tree.Config.Claude.Connections.Remove(_data.Id);
        var refused = Read();
        Assert.False(refused.Ok);
        Assert.Null(refused.Tab);
    }

    [Fact]
    public async Task ActiveTab_SchemaOnly_HidesTheError()
    {
        var tab = NewTab(_schema, "SELECT nope FROM t");
        await tab.RunScriptAsync();

        var shared = Read().Tab!;
        Assert.Null(shared.LastError);
        Assert.Contains("not shared", shared.Note);
    }

    [Fact]
    public async Task ActiveTab_AnErrorFromAnotherConnection_IsNotShared()
    {
        var tab = NewTab(_data, "SELECT nope FROM t");
        await tab.RunScriptAsync();
        Assert.NotNull(tab.ErrorToShare.Error);

        // What a switch to another database of the group does.
        typeof(TabViewModel).GetProperty(nameof(TabViewModel.Connection))!.SetValue(tab, _hidden);
        Assert.Null(tab.ErrorToShare.Error);
    }

    [Fact]
    public void ActiveTab_NeedsAScriptTab() => Assert.False(Read().Ok);

    [Fact]
    public void Listener_FollowsTheSwitch()
    {
        _main.SyncClaudeListener();
        _main.SyncClaudeListener();
        Assert.Equal(1, _listening);
        _tree.Config.Claude.Enabled = false;
        _main.SyncClaudeListener();
        Assert.Equal(0, _listening);
    }

    [Fact]
    public void Connections_LoseTheirGrant_WhenRepointedFlaggedOrDeleted()
    {
        _tree.UpdateConnection(_data with { Name = "renamed", ProtectedPassword = "p:other" });
        Assert.Equal(ClaudeAccess.SchemaAndData, _tree.Config.Claude.Of(_data.Id));
        _tree.UpdateConnection(_data with { Database = "other.db" });
        Assert.Equal(ClaudeAccess.None, _tree.Config.Claude.Of(_data.Id));

        _tree.UpdateConnection(_schema with { Environment = DbEnvironment.Production });
        _tree.UpdateConnection(_schema);
        Assert.Equal(ClaudeAccess.None, _tree.Config.Claude.Of(_schema.Id));

        _tree.DeleteConnection(_prod);
        Assert.Empty(_tree.Config.Claude.Connections);
    }

    [Fact]
    public async Task OpenInClaudeCode_WritesTheConfigAndStartsATerminal()
    {
        await _main.OpenInClaudeCodeCommand.ExecuteAsync(null);

        var start = Assert.Single(_started);
        Assert.Equal(Path.Combine(_fx.Fx.Dir, "library"), start.WorkingDirectory);
        var written = File.ReadAllText(Path.Combine(_fx.Fx.Dir, "claude", "mcp.json"));
        Assert.Contains("--mcp", written);
        Assert.DoesNotContain("p:", written);
    }

    [Fact]
    public async Task OpenInClaudeCode_SaysWhenClaudeIsMissing_OrTheIntegrationIsOff()
    {
        _claude = null;
        await _main.OpenInClaudeCodeCommand.ExecuteAsync(null);
        _tree.Config.Claude.Enabled = false;
        _fx.Fx.Dialogs.ConfirmResult = false;
        await _main.OpenInClaudeCodeCommand.ExecuteAsync(null);
        Assert.Empty(_started);
    }

    [Fact]
    public void ScriptsFolder_IsTheRepositoryOfTheDbUpdateFolder()
    {
        var repository = Path.Combine(_fx.Fx.Dir, "repo");
        var dbupdate = Path.Combine(repository, "sql", "dbupdate");
        Directory.CreateDirectory(dbupdate);
        var library = Path.Combine(_fx.Fx.Dir, "lib");
        Assert.Equal(library, MainViewModel.ScriptsFolder(null, library));
        Assert.True(Directory.Exists(library));
        Assert.Equal(library, MainViewModel.ScriptsFolder(Path.Combine(repository, "gone"), library));
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        Assert.Equal(repository, MainViewModel.ScriptsFolder(dbupdate, library));
    }

    private SettingsViewModel Settings() => new(new SettingsContext(new SettingsService(new AppSettings()))
    {
        Claude = new ClaudeSettingsAccess(() => _tree.Config.Claude, () => _tree.Config.Connections, _tree.SetClaude,
            new ClaudeActivityLog(Path.Combine(_fx.Fx.Dir, "activity.jsonl")), "claude mcp add"),
    }, "claude");

    [Fact]
    public void SettingsPage_ChoosesAccessPerConnection_NeverForProduction()
    {
        using var vm = Settings();
        var page = Assert.IsType<ClaudeSettingsPage>(vm.SelectedPage);
        var hidden = page.Connections.Single(r => r.Name == "hidden");
        var prod = page.Connections.Single(r => r.Name == "prod");

        Assert.Equal(ClaudeAccess.None, hidden.Selected.Access);
        hidden.Selected = ClaudeConnectionRow.Choices[1];
        Assert.Equal(ClaudeAccess.Schema, _fx.Fx.Store.Load().Claude.Of(_hidden.Id));

        Assert.False(prod.CanChoose);
        Assert.NotNull(prod.DisabledReason);
        Assert.Equal(ClaudeAccess.None, prod.Selected.Access);

        page.IsEnabled = false;
        Assert.False(hidden.CanChoose);
        Assert.False(_fx.Fx.Store.Load().Claude.Enabled);

        page.ResetToDefaults();
        Assert.Empty(_tree.Config.Claude.Connections);
    }

    [AvaloniaFact]
    public void SettingsPage_Renders()
    {
        using var vm = Settings();
        var window = new SettingsDialog { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var view = window.GetVisualDescendants().OfType<ClaudePageView>().Single();
        Assert.Equal(4, view.GetVisualDescendants().OfType<ComboBox>().Count());
        Assert.Single(view.GetVisualDescendants().OfType<ComboBox>(), c => !c.IsEffectivelyEnabled);
        window.Close();
    }
}
