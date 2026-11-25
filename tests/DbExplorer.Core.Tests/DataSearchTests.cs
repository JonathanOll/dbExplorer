using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;
using DbExplorer.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

public sealed class DataSearchTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "dbx-dsearch-" + Guid.NewGuid().ToString("N") + ".db");

    public async Task InitializeAsync()
    {
        await using var c = new SqliteConnection($"Data Source={_file};Pooling=False");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        var script = new System.Text.StringBuilder(
            """
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT, city TEXT, score REAL, picture BLOB);
            INSERT INTO customers VALUES (1, 'Acme Corp', 'Paris', 4.5, x'41636D65');
            INSERT INTO customers VALUES (2, 'Globex', 'Acme City', 3.0, NULL);
            INSERT INTO customers VALUES (3, 'acme', 'Rome', 12.25, NULL);
            INSERT INTO customers VALUES (4, 'Percent 50%', 'a_b', 1, NULL);
            CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT);
            INSERT INTO notes VALUES (1, 'nothing here');
            CREATE TABLE blobs (id INTEGER PRIMARY KEY, data BLOB);
            INSERT INTO blobs VALUES (1, x'41636D65');
            CREATE TABLE logs (id INTEGER PRIMARY KEY, line TEXT);
            CREATE VIEW v_customers AS SELECT * FROM customers;
            """);
        for (var i = 1; i <= 50; i++) script.Append($"INSERT INTO logs VALUES ({i}, 'error code {i:000}');");
        cmd.CommandText = script.ToString();
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_file); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private async Task<Session> OpenAsync(bool readOnly = false)
    {
        var driver = new SqliteDriver();
        var config = new ConnectionConfig(Guid.NewGuid(), "Lite", DbKind.Sqlite, null, null, null, _file, null, null, false, false, readOnly);
        await using var c = driver.CreateConnection(config, null);
        await c.OpenAsync();
        return new Session(config, driver, await driver.LoadSchemaAsync(c, CancellationToken.None));
    }

    private static async Task<(DataSearchSummary Summary, List<DataMatch> Matches)> Run(Session s, DataSearchOptions o, CancellationToken ct = default)
    {
        var matches = new List<DataMatch>();
        var summary = await DataSearch.RunAsync(s, o, (_, m) => matches.AddRange(m), null, ct);
        return (summary, matches);
    }

    [Fact]
    public async Task Contains_FindsEveryColumn_CaseInsensitively_WithAttributionAndPreview()
    {
        var (summary, matches) = await Run(await OpenAsync(), new("acme"));

        var hits = matches.Select(m => $"{m.Table}.{m.Column}={m.Value}").OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(["customers.city=Acme City", "customers.name=Acme Corp", "customers.name=acme"], hits);
        Assert.False(summary.Cancelled);
        var first = matches.Single(m => m.Value == "Acme Corp");
        Assert.Equal("id", first.Preview[0].Column);   // the key leads
        Assert.Contains(first.Preview, c => c.IsMatch && c.Column == "name");
        Assert.True(first.Preview.Count <= DataSearch.MaxPreviewColumns + 1);
    }

    [Fact]
    public async Task Exact_And_StartsWith_ModesDiffer()
    {
        var s = await OpenAsync();

        var exact = (await Run(s, new("acme", DataMatchMode.Exact))).Matches;
        var starts = (await Run(s, new("acme", DataMatchMode.StartsWith))).Matches;

        Assert.Equal(["acme"], exact.Select(m => m.Value));
        Assert.Equal(["Acme City", "Acme Corp", "acme"], starts.Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task LikeWildcards_InTheSearchTextAreLiteral()
    {
        var s = await OpenAsync();

        Assert.Equal(["Percent 50%"], (await Run(s, new("50%"))).Matches.Select(m => m.Value));
        Assert.Equal(["a_b"], (await Run(s, new("a_b"))).Matches.Select(m => m.Value));
        Assert.DoesNotContain((await Run(s, new("%"))).Matches, m => m.Value != "Percent 50%");
    }

    [Fact]
    public async Task Numbers_AreFoundThroughTheirText_AndOnlyForNumericLookingText()
    {
        var s = await OpenAsync();

        var hits = (await Run(s, new("12.25"))).Matches;
        Assert.Equal(["score"], hits.Select(m => m.Column));

        var customers = s.Schema.Single(t => t.Ref.Name == "customers");
        Assert.DoesNotContain(DataSearch.SearchableColumns(customers, "abc"), c => c.Name == "score");
        Assert.Contains(DataSearch.SearchableColumns(customers, "12"), c => c.Name == "score");
    }

    [Fact]
    public async Task BinaryColumns_AreNeverSearched_AndTablesWithOnlyBinaryAreSkipped()
    {
        var s = await OpenAsync();

        var customers = s.Schema.Single(t => t.Ref.Name == "customers");
        Assert.DoesNotContain(DataSearch.SearchableColumns(customers, "Acme"), c => c.Name == "picture");
        Assert.DoesNotContain(DataSearch.Candidates(s.Schema, new("Acme")), t => t.Ref.Name == "blobs");
        var (summary, matches) = await Run(s, new("Acme"));
        Assert.DoesNotContain(matches, m => m.Table.Name == "blobs" || m.Column == "picture");
        Assert.DoesNotContain(DataSearch.Candidates(s.Schema, new("Acme")), t => t.IsView);
        Assert.Equal(DataSearch.Candidates(s.Schema, new("Acme")).Count, summary.TablesTotal);
    }

    [Fact]
    public async Task MaxRowsPerTable_CapsEachTable()
    {
        var s = await OpenAsync();

        var (_, defaults) = await Run(s, new("error"));
        var (_, three) = await Run(s, new("error", MaxRowsPerTable: 3));

        Assert.Equal(20, defaults.Count);
        Assert.Equal(3, three.Count);
    }

    [Fact]
    public async Task SchemaAndTableFilters_NarrowTheTables()
    {
        var s = await OpenAsync();

        var (summary, matches) = await Run(s, new("o", TableContains: "NOTE"));

        Assert.All(matches, m => Assert.Equal("notes", m.Table.Name));
        Assert.Equal(1, summary.TablesTotal);
    }

    [Fact]
    public async Task Progress_ReportsTablesDoneOfTotal()
    {
        var s = await OpenAsync();
        var reports = new List<DataSearchProgress>();
        var progress = new SyncProgress<DataSearchProgress>(reports.Add);

        var summary = await DataSearch.RunAsync(s, new("acme"), (_, _) => { }, progress, CancellationToken.None);

        Assert.Equal(0, reports[0].TablesDone);
        Assert.Equal(summary.TablesTotal, reports[0].TablesTotal);
        lock (reports) Assert.Equal(summary.TablesTotal, reports.Max(r => r.TablesDone));
    }

    [Fact]
    public async Task Cancellation_StopsAndKeepsWhatWasFound_WithoutThrowing()
    {
        var s = await OpenAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (summary, matches) = await Run(s, new("acme"), cts.Token);

        Assert.True(summary.Cancelled);
        Assert.Empty(matches);
        Assert.Equal(0, summary.TablesSearched);
    }

    [Fact]
    public async Task CancelledInTheMiddle_ReportsCancelled()
    {
        var s = await OpenAsync();
        using var cts = new CancellationTokenSource();
        var summary = await DataSearch.RunAsync(s, new("e", MaxParallel: 1), (_, _) => cts.Cancel(), null, cts.Token);

        Assert.True(summary.Cancelled);
        Assert.True(summary.TablesSearched < summary.TablesTotal);
    }

    [Fact]
    public async Task ReadOnlyConnection_StillSearches_AndNothingIsWritten()
    {
        var s = await OpenAsync(readOnly: true);

        var (summary, matches) = await Run(s, new("acme"));

        Assert.Equal(3, matches.Count);
        Assert.Empty(summary.Failures);
        await using var c = new SqliteDriver().CreateConnection(s.Config, null);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM customers";
        Assert.Equal(4L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task AFailingTable_IsReported_AndTheOthersStillRun()
    {
        var s = await OpenAsync();
        // A schema entry for a table that does not exist: its query fails, the rest must go on.
        var ghost = new TableInfo(new TableRef(null, "ghost"), false,
            [new ColumnInfo("name", "TEXT", typeof(string), true, false, 0)], [], []);
        var withGhost = new Session(s.Config, s.Driver, [ghost, .. s.Schema]);

        var (summary, matches) = await Run(withGhost, new("acme"));

        Assert.Equal("ghost", summary.Failures.Single().Table.Name);
        Assert.Equal(3, matches.Count);
    }

    [Fact]
    public async Task EmptyText_Throws()
    {
        var s = await OpenAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => Run(s, new("")));
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) { lock (this) report(value); }
    }
}
