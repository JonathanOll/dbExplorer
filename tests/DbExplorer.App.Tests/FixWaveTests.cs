using System.Data.Common;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using static DbExplorer.App.Tests.TreeFixture;

namespace DbExplorer.App.Tests;

/// <summary>A SQLite driver whose schema load waits for a gate: a connect that stays "in flight" for as long as the test wants.</summary>
internal sealed class GatedSqliteDriver : IDbDriver
{
    private readonly SqliteDriver _inner = new();
    public TaskCompletionSource Entered { get; } = new();
    public TaskCompletionSource Gate { get; } = new();

    public DbKind Kind => _inner.Kind;
    public string ParamPrefix => _inner.ParamPrefix;
    public string Quote(string identifier) => _inner.Quote(identifier);
    public string QuoteTable(TableRef t) => _inner.QuoteTable(t);
    public string CastToText(string quotedColumn) => _inner.CastToText(quotedColumn);
    public string EscapeLike(string text) => _inner.EscapeLike(text);
    public string PageSql(string selectSql, string orderBy, int offset, int limit) => _inner.PageSql(selectSql, orderBy, offset, limit);
    public DbConnection CreateConnection(ConnectionConfig cfg, string? plainPassword) => _inner.CreateConnection(cfg, plainPassword);

    public async Task<IReadOnlyList<TableInfo>> LoadSchemaAsync(DbConnection c, CancellationToken ct)
    {
        Entered.TrySetResult();
        await Gate.Task.WaitAsync(ct);
        return await _inner.LoadSchemaAsync(c, ct);
    }
}

/// <summary>The command line is checked before anything starts, so a flag without a value is a message, not a crash.</summary>
public class StartupArgumentTests
{
    [Theory]
    [InlineData("--data-dir")]
    [InlineData("--dbeaver-dir")]
    public void FlagWithoutValue_GivesAMessageNamingTheFlag_AndTheUsage(string flag)
    {
        var message = AppPaths.CheckArguments([flag]);

        Assert.NotNull(message);
        Assert.Contains(flag, message);
        Assert.Contains("requires a value", message);
        Assert.Contains("Usage:", message);
    }

    [Fact]
    public void FlagFollowedByAnotherFlag_IsMissingItsValue()
    {
        Assert.Contains("--data-dir", AppPaths.CheckArguments(["--data-dir", "--dbeaver-dir", "x"]));
    }

    [Theory]
    [InlineData]
    [InlineData("--data-dir", @"C:\tmp\x")]
    [InlineData("--data-dir", @"C:\tmp\x", "--dbeaver-dir", @"C:\tmp\y")]
    [InlineData("--unknown")]
    public void ValidCommandLines_HaveNoProblem(params string[] args)
    {
        Assert.Null(AppPaths.CheckArguments(args));
    }

    [Fact]
    public void WhateverCheckArgumentsAccepts_ResolveDoesNotThrow()
    {
        string[][] lines = [[], ["--data-dir", @"C:\tmp\x"], ["--dbeaver-dir", @"C:\tmp\y"]];
        foreach (var args in lines)
        {
            Assert.Null(AppPaths.CheckArguments(args));
            AppPaths.Resolve(args, _ => null);
        }
    }
}

/// <summary>Pending edits belong to the database they were loaded from: "Reload, then Save" never writes to a re-pointed connection.</summary>
public class PendingEditsTargetTests : IDisposable
{
    private readonly TableTabFixture _t = new();
    private readonly FakeEditDialogs _dialogs = new() { PreviewResult = true };

    public void Dispose() => _t.Dispose();

    private string CopyDb()
    {
        var copy = Path.Combine(_t.Fx.Dir, "other.db");
        File.Copy(_t.DbFile, copy);
        return copy;
    }

    private static string NameOfAlice(string file)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM customers WHERE id = 1";
        return (string)cmd.ExecuteScalar()!;
    }

    private async Task<(TableTabViewModel Tab, ConnectionConfig Moved)> OpenTabThatWillBeRepointed()
    {
        var moved = _t.Config with { Database = CopyDb() };
        var session = await _t.OpenSession();
        var hooks = new TableTabHooks(
            ct => _t.Sessions.GetOrOpenAsync(moved, ct).ContinueWith(t => (Session?)t.Result, ct),   // the connection was edited
            (_, _, _) => Task.CompletedTask,
            _dialogs);
        var tab = new TableTabViewModel(session, new TableRef(null, "customers"), hooks);
        await tab.LoadAsync();
        return (tab, moved);
    }

    [Fact]
    public async Task ReloadThenSave_AfterTheConnectionWasRepointed_RefusesAndWritesNowhere()
    {
        var (tab, moved) = await OpenTabThatWillBeRepointed();
        tab.SetCell(0, tab.FindColumn("name")!.Value, "Alicia");
        _t.Sessions.Close(_t.Config.Id);   // the edit of the connection closed the session
        _dialogs.Choice = SaveChoice.Save;

        await tab.ReloadAsync();

        Assert.Contains("different server or database", tab.ErrorMessage);
        Assert.True(tab.NeedsReload);
        Assert.True(tab.HasPendingEdits);      // the edits are kept for the user to discard
        Assert.Empty(_dialogs.Previews);       // no preview, no write
        Assert.Equal("Alice", NameOfAlice(_t.DbFile));
        Assert.Equal("Alice", NameOfAlice(moved.Database));
    }

    [Fact]
    public async Task ReloadThenSave_WhenOnlyTheNameChanged_StillWorks()
    {
        var session = await _t.OpenSession();
        var renamed = _t.Config with { Name = "Renamed" };
        var hooks = new TableTabHooks(
            ct => _t.Sessions.GetOrOpenAsync(renamed, ct).ContinueWith(t => (Session?)t.Result, ct),
            (_, _, _) => Task.CompletedTask,
            _dialogs);
        var tab = new TableTabViewModel(session, new TableRef(null, "customers"), hooks);
        await tab.LoadAsync();
        tab.SetCell(0, tab.FindColumn("name")!.Value, "Alicia");
        _t.Sessions.Close(_t.Config.Id);
        _dialogs.Choice = SaveChoice.Save;

        await tab.ReloadAsync();

        Assert.Null(tab.ErrorMessage);
        Assert.Equal("Alicia", NameOfAlice(_t.DbFile));
    }

    [Fact]
    public async Task SqlPreview_NamesTheConnectionAndTheDatabase()
    {
        var session = await _t.OpenSession();
        var tab = new TableTabViewModel(session, new TableRef(null, "customers"), new TableTabHooks(
            ct => Task.FromResult<Session?>(session), (_, _, _) => Task.CompletedTask, _dialogs));
        await tab.LoadAsync();
        tab.SetCell(0, tab.FindColumn("name")!.Value, "Alicia");

        await tab.ReviewAndSaveAsync();

        var target = Assert.Single(_dialogs.PreviewTargets);
        Assert.Contains("Lite", target);
        Assert.Contains(_t.DbFile, target);
    }
}

public class TableReloadThroughTreeTests : IDisposable
{
    private readonly TableTabFixture _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task Reload_AfterDisconnect_ReconnectsTheTreeNodeToo()
    {
        var tree = new ConnectionTreeViewModel(
            _t.Fx.Store, new AppConfig([], [_t.Config], new()), _t.Sessions, _t.Fx.Status, _t.Fx.Dialogs, _t.Fx.DBeaverDir);
        var node = tree.Nodes.OfType<ConnectionNodeViewModel>().Single();
        await tree.ConnectAsync(node);
        var main = new MainViewModel(tree, _t.Fx.Status);
        main.UseTableTabs(_t.Sessions);
        await main.OpenTable(tree.FindSession(_t.Config.Id)!, new TableRef(null, "customers"));
        var tab = Assert.IsType<TableTabViewModel>(Assert.Single(main.Tabs));
        await tab.LoadAsync();

        tree.Disconnect(_t.Config.Id);
        Assert.Equal(ConnectionState.Disconnected, node.State);
        await tab.ReloadAsync();

        Assert.Equal(ConnectionState.Connected, node.State);   // the tree follows, it no longer shows Disconnected
        Assert.NotNull(tree.FindSession(_t.Config.Id));
        Assert.Null(tab.ErrorMessage);
        Assert.Equal(4, tab.Rows.Count);
    }

    [Fact]
    public async Task Reload_WhenTheConnectionCannotBeOpened_SaysSoInsteadOfClaimingItIsGone()
    {
        var broken = _t.Config with { Database = Path.Combine(_t.Fx.Dir, "missing.db") };
        var tree = new ConnectionTreeViewModel(
            _t.Fx.Store, new AppConfig([], [_t.Config], new()), _t.Sessions, _t.Fx.Status, _t.Fx.Dialogs, _t.Fx.DBeaverDir);
        var node = tree.Nodes.OfType<ConnectionNodeViewModel>().Single();
        await tree.ConnectAsync(node);
        var main = new MainViewModel(tree, _t.Fx.Status);
        main.UseTableTabs(_t.Sessions);
        await main.OpenTable(tree.FindSession(_t.Config.Id)!, new TableRef(null, "customers"));
        var tab = Assert.IsType<TableTabViewModel>(Assert.Single(main.Tabs));
        await tab.LoadAsync();
        tree.UpdateConnection(broken);   // a target change: closes the session

        await tab.ReloadAsync();

        Assert.NotNull(tab.ErrorMessage);
        Assert.DoesNotContain("no longer exists", tab.ErrorMessage);
        Assert.True(tab.NeedsReload);
    }
}

public class ImportReadOnlyTests : IDisposable
{
    private readonly TreeFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Import_ProdAndReadOnlyConnections_AreReadOnly_AndTheSummaryNamesThem()
    {
        Directory.CreateDirectory(_fx.DBeaverDir);
        File.WriteAllText(Path.Combine(_fx.DBeaverDir, "data-sources.json"), """
            {
              "connections": {
                "a": { "provider": "postgresql", "name": "Dev DB",
                       "configuration": { "host": "h", "database": "d", "type": "dev" } },
                "b": { "provider": "postgresql", "name": "Prod DB",
                       "configuration": { "host": "h", "database": "d", "type": "prod" } },
                "c": { "provider": "sqlite", "name": "Frozen", "read-only": true,
                       "configuration": { "database": "x.db", "type": "dev" } }
              }
            }
            """);
        var tree = _fx.CreateTree(new AppConfig([], [], new()));

        await tree.ImportFromDBeaverAsync();

        Assert.False(tree.Config.Connections.Single(c => c.Name == "Dev DB").ReadOnly);
        Assert.True(tree.Config.Connections.Single(c => c.Name == "Prod DB").ReadOnly);
        Assert.True(tree.Config.Connections.Single(c => c.Name == "Frozen").ReadOnly);
        Assert.True(_fx.Store.Load().Connections.Single(c => c.Name == "Prod DB").ReadOnly);   // persisted
        Assert.Equal(2, tree.LastImportReadOnly.Count);
        Assert.Contains("READ-ONLY", _fx.Status.Message);
        Assert.Contains("Prod DB", _fx.Status.Message);
        Assert.Contains("Frozen", _fx.Status.Message);
        Assert.DoesNotContain("Dev DB (", _fx.Status.Message);
    }
}

public class ReadOnlyDuringConnectTests : IDisposable
{
    private readonly TreeFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task ToggleReadOnly_WhileConnecting_ProducesAReadOnlySession()
    {
        var driver = new GatedSqliteDriver();
        var sessions = new SessionManager(_fx.Store, _fx.Cache, _fx.Protector, _ => driver);
        var config = Conn("Slow", database: _fx.CreateSqliteFile());
        var tree = new ConnectionTreeViewModel(
            _fx.Store, new AppConfig([], [config], new()), sessions, _fx.Status, _fx.Dialogs, _fx.DBeaverDir);
        var node = tree.Nodes.OfType<ConnectionNodeViewModel>().Single();

        var connecting = tree.ConnectAsync(node);                         // double-click a slow connection...
        await driver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tree.ToggleReadOnly(node);                                          // ...and toggle read-only meanwhile
        Assert.True(node.IsReadOnly);
        driver.Gate.SetResult();
        await connecting.WaitAsync(TimeSpan.FromSeconds(10));

        var session = tree.FindSession(config.Id);
        Assert.NotNull(session);
        Assert.True(session!.Config.ReadOnly);
        Assert.Equal(node.Config, session.Config);
    }

    [Fact]
    public async Task ToggleReadOnly_WhileConnecting_Off_AlsoReachesTheSession()
    {
        var driver = new GatedSqliteDriver();
        var sessions = new SessionManager(_fx.Store, _fx.Cache, _fx.Protector, _ => driver);
        var config = Conn("Slow", database: _fx.CreateSqliteFile()) with { ReadOnly = true };
        var tree = new ConnectionTreeViewModel(
            _fx.Store, new AppConfig([], [config], new()), sessions, _fx.Status, _fx.Dialogs, _fx.DBeaverDir);
        var node = tree.Nodes.OfType<ConnectionNodeViewModel>().Single();

        var connecting = tree.ConnectAsync(node);
        await driver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tree.ToggleReadOnly(node);
        driver.Gate.SetResult();
        await connecting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(tree.FindSession(config.Id)!.Config.ReadOnly);
    }
}
