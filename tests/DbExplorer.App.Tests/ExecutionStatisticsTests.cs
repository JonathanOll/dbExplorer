using System.Data;
using System.Data.Common;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.Tests;

/// <summary>A SQL Server look-alike: reports server messages while a statement runs, records every command text.</summary>
internal sealed class StatsConnection : DbConnection, IServerMessageSource
{
    private System.Data.ConnectionState _state = System.Data.ConnectionState.Open;

    public event Action<string>? MessageReceived;

    public List<string> Log { get; } = [];
    public Func<string, string[]> Says { get; set; } = _ => [];

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString { get; set; } = "";
    public override string Database => "fake";
    public override string DataSource => "fake";
    public override string ServerVersion => "0";
    public override System.Data.ConnectionState State => _state;
    public override void ChangeDatabase(string databaseName) { }
    public override void Close() => _state = System.Data.ConnectionState.Closed;
    public override void Open() => _state = System.Data.ConnectionState.Open;
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand() => new Cmd(this);

    private sealed class Cmd(StatsConnection c) : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = c;
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        public override int ExecuteNonQuery() { lock (c.Log) c.Log.Add(CommandText); return 0; }
        public override object? ExecuteScalar() { lock (c.Log) c.Log.Add(CommandText); return 1; }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            lock (c.Log) c.Log.Add(CommandText);
            foreach (var message in c.Says(CommandText)) c.MessageReceived?.Invoke(message);
            var table = new DataTable();
            table.Columns.Add("n", typeof(int));
            table.Rows.Add(1);
            return new DataTableReader(table);
        }
    }
}

internal sealed class StatsDriver(DbKind kind, Func<StatsConnection> create) : IDbDriver
{
    public DbKind Kind => kind;
    public string ParamPrefix => "@";
    public string Quote(string identifier) => identifier;
    public string QuoteTable(TableRef t) => t.Name;
    public string CastToText(string quotedColumn) => quotedColumn;
    public string EscapeLike(string text) => text;
    public string PageSql(string selectSql, string orderBy, int offset, int limit) => selectSql;
    public DbConnection CreateConnection(ConnectionConfig cfg, string? plainPassword) => create();
    public Task<IReadOnlyList<TableInfo>> LoadSchemaAsync(DbConnection c, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TableInfo>>([]);
}

/// <summary>The "Statistics" toggle of a script tab and the Statistics / Messages tabs it adds to the results.</summary>
public class ExecutionStatisticsTests
{
    private const string Io1 =
        "Table 'Invoices'. Scan count 1, logical reads 5, physical reads 0, read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob read-ahead reads 0.";
    private const string Io2 =
        "Table 'Customers'. Scan count 2, logical reads 30, physical reads 1, read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob read-ahead reads 0.";
    private const string Exec = "SQL Server Execution Times:\n   CPU time = 15 ms, elapsed time = 20 ms.";

    private static (ScriptTabViewModel Tab, StatsConnection Connection) NewTab(DbKind kind = DbKind.SqlServer, string text = "SELECT 1; SELECT 2")
    {
        var connection = new StatsConnection
        {
            Says = sql => sql == "SELECT 1" ? [Io1, Exec] : sql == "SELECT 2" ? ["PRINT hello", Io2, Exec] : [],
        };
        var config = TreeFixture.Conn("Sql", kind: kind);
        var session = new Session(config, new StatsDriver(kind, () => connection), [], null);
        var tab = new ScriptTabViewModel(config, session, new ScriptTabHooks(_ => Task.FromResult<Session?>(session)), 1) { Text = text };
        return (tab, connection);
    }

    [Fact]
    public async Task ToggleOff_AddsNothing_NoSetCommands()
    {
        var (tab, connection) = NewTab();

        await tab.RunScriptAsync();

        Assert.Equal(2, tab.Results.Count);
        Assert.Equal(2, tab.ResultTabs.Count);
        Assert.DoesNotContain(connection.Log, l => l.StartsWith("SET STATISTICS"));
        Assert.Empty(tab.Results[0].Messages);
        tab.Close();
    }

    [Fact]
    public async Task ToggleOn_AddsStatisticsAndMessagesTabs_AfterTheStatements()
    {
        var (tab, connection) = NewTab();
        tab.StatisticsEnabled = true;

        await tab.RunScriptAsync();

        Assert.Equal(2, tab.Results.Count);   // Results is still one entry per statement
        Assert.Equal(["1 · SELECT 1", "2 · SELECT 2", "Statistics", "Messages"], tab.ResultTabs.Select(t => t.Header).ToArray());
        Assert.Same(tab.Results[1], tab.SelectedResult);
        Assert.Same(tab.Results[1], tab.SelectedResultTab);   // the statement stays selected, the grids are one click away
        Assert.Equal(
            ["SET STATISTICS IO ON; SET STATISTICS TIME ON;", "SELECT 1", "SELECT 2", "SET STATISTICS IO OFF; SET STATISTICS TIME OFF;"],
            connection.Log);

        var stats = Assert.IsType<GridResultTab>(tab.ResultTabs[2]);
        Assert.Equal(["Statement", "Kind", "Table"], stats.Columns.Take(3).Select(c => c.Name).ToArray());
        Assert.Equal(["IO", "Execution", "Total", "IO", "Execution", "Total", "Grand total"], stats.Rows.Select(r => (string)r[1]!).ToArray());
        Assert.Equal("Invoices", stats.Rows[0][2]);
        Assert.Contains("logical reads 35", stats.StatusText);

        var messages = Assert.IsType<GridResultTab>(tab.ResultTabs[3]);
        Assert.Contains(messages.Rows, r => (string?)r[2] == "PRINT hello" && (long)r[0]! == 2);
        tab.Close();
    }

    [Fact]
    public async Task Rerun_ReplacesTheTabs_AndTurningTheToggleOffRemovesThem()
    {
        var (tab, _) = NewTab();
        tab.StatisticsEnabled = true;
        await tab.RunScriptAsync();
        await tab.RunScriptAsync();

        Assert.Equal(4, tab.ResultTabs.Count);   // not 6

        tab.StatisticsEnabled = false;
        await tab.RunScriptAsync();

        Assert.Equal(2, tab.ResultTabs.Count);
        Assert.All(tab.ResultTabs, t => Assert.IsType<StatementResultViewModel>(t));
        tab.Close();
    }

    [Fact]
    public async Task SelectingAGridTab_DoesNotChangeTheSelectedStatement()
    {
        var (tab, _) = NewTab();
        tab.StatisticsEnabled = true;
        await tab.RunScriptAsync();

        tab.SelectedResultTab = tab.ResultTabs[2];

        Assert.Same(tab.Results[1], tab.SelectedResult);
        tab.SelectedResultTab = tab.ResultTabs[0];
        Assert.Same(tab.Results[0], tab.SelectedResult);
        tab.Close();
    }

    [Fact]
    public async Task OnlySqlServerCanUseStatistics()
    {
        var (sql, _) = NewTab(DbKind.SqlServer);
        var (lite, liteConnection) = NewTab(DbKind.Sqlite);
        lite.StatisticsEnabled = true;   // even if somehow set, nothing is sent to another engine

        await lite.RunScriptAsync();

        Assert.True(sql.CanUseStatistics);
        Assert.False(lite.CanUseStatistics);
        Assert.Equal(2, lite.ResultTabs.Count);
        Assert.DoesNotContain(liteConnection.Log, l => l.StartsWith("SET STATISTICS"));
        sql.Close();
        lite.Close();
    }

    [Fact]
    public async Task FailedStatement_StillShowsTheStatisticsOfWhatRan()
    {
        var (tab, connection) = NewTab(text: "SELECT 1; SELECT 2");
        var failing = connection.Says;
        connection.Says = sql =>
        {
            var said = failing(sql);
            if (sql == "SELECT 2") throw new InvalidOperationException("boom");
            return said;
        };
        tab.StatisticsEnabled = true;

        await tab.RunScriptAsync();

        Assert.Equal(["1 · SELECT 1", "2 · SELECT 2", "Statistics", "Messages"], tab.ResultTabs.Select(t => t.Header).ToArray());
        Assert.Same(tab.Results[1], tab.SelectedResult);   // the failed one
        Assert.Equal(2, ((GridResultTab)tab.ResultTabs[2]).Rows.Count(r => (string?)r[1] is "IO" or "Execution"));
        tab.Close();
    }

    // ---- group run: the tabs built from the members' results ----

    [Fact]
    public void GroupRun_Prepare_WithStatistics_AddsAStatisticsTabWithADatabaseColumn()
    {
        var statement = new ScriptStatement("SELECT 1", 0, 8);
        ServerMessage M(string t) => new(DateTimeOffset.Now, t);
        MemberResult Member(string name, string io) => new(
            TreeFixture.Conn(name, kind: DbKind.SqlServer), MemberState.Done,
            [new StatementResult(statement, [new ResultSet(["n"], [[1]], false)], null, TimeSpan.Zero, null, [M(io), M(Exec)])],
            TimeSpan.Zero, null);
        var results = new[] { Member("Prod", Io1), Member("Test", Io2) };

        var run = new GroupRunViewModel(results.Select(r => r.Connection).ToList(), 1000);
        run.Complete(results, 1, statistics: true);

        Assert.Equal(["All", "Prod", "Test", "Statistics", "Messages"], run.Tabs.Select(t => t.Title));
        var stats = run.Tabs[3];
        Assert.True(stats.IsGrid);
        Assert.False(stats.IsAll);
        Assert.False(stats.IsMember);
        Assert.Equal("Database", stats.Grid!.Columns[0].Name);
        Assert.Equal(["Prod", "Prod", "Prod", "Test", "Test", "Test"], stats.Grid.Rows.Select(r => (string)r[0]!).ToArray());

        var without = new GroupRunViewModel(results.Select(r => r.Connection).ToList(), 1000);
        without.Complete(results, 1);
        Assert.Equal(["All", "Prod", "Test"], without.Tabs.Select(t => t.Title));
    }

    // ---- view ----

    [AvaloniaFact]
    public async Task View_ShowsTheToggleOnlyForSqlServer_AndTheGridTabs()
    {
        var (tab, _) = NewTab();
        var host = new ContentControl { Content = tab };
        var window = new Window { Content = host, Width = 900, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var toggle = window.GetVisualDescendants().OfType<ToggleButton>().Single(c => (string?)c.Content == "Statistics");
        Assert.True(toggle.IsVisible);
        toggle.IsChecked = true;
        Assert.True(tab.StatisticsEnabled);

        await tab.RunScriptAsync();
        Dispatcher.UIThread.RunJobs();
        var strip = window.GetVisualDescendants().OfType<TabControl>().First(t => ReferenceEquals(t.ItemsSource, tab.ResultTabs));
        Assert.Equal(4, strip.ItemCount);

        strip.SelectedIndex = 2;   // Statistics
        Dispatcher.UIThread.RunJobs();
        var grid = window.GetVisualDescendants().OfType<ResultGrid>().Single();
        Assert.True(grid.ClientSort);
        Assert.Equal(((GridResultTab)tab.ResultTabs[2]).Columns.Count, grid.Columns!.Count);

        window.Close();
        tab.Close();

        var (lite, _) = NewTab(DbKind.Sqlite);
        var liteWindow = new Window { Content = new ContentControl { Content = lite }, Width = 900, Height = 600 };
        liteWindow.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(liteWindow.GetVisualDescendants().OfType<ToggleButton>().Single(c => (string?)c.Content == "Statistics").IsVisible);
        liteWindow.Close();
        lite.Close();
    }

    [AvaloniaFact]
    public void ResultGrid_ClientSort_SortsNumbersByValue_ThenDescending_ThenOriginalOrder()
    {
        object?[][] rows = [[10L, "b"], [9L, "a"], [null, "z"], [100L, "c"]];
        var grid = new ResultGrid { ClientSort = true, Columns = [new ResultColumn("n"), new ResultColumn("s")], ItemsSource = rows };
        var window = new Window { Content = grid, Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var data = grid.GetVisualDescendants().OfType<DataGrid>().Single();
        var toggle = typeof(ResultGrid).GetMethod("ToggleClientSort", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        object?[] Order() => ((System.Collections.IEnumerable)data.ItemsSource!).Cast<object?[]>().Select(r => r[0]).ToArray();

        toggle.Invoke(grid, [0]);
        Assert.Equal(new object?[] { 9L, 10L, 100L, null }, Order());   // numeric, not "10" < "100" < "9"; NULL last

        toggle.Invoke(grid, [0]);
        Assert.Equal(new object?[] { 100L, 10L, 9L, null }, Order());

        toggle.Invoke(grid, [0]);
        Assert.Equal(new object?[] { 10L, 9L, null, 100L }, Order());   // third click: back to the original order
        window.Close();
    }
}
