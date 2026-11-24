using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using Microsoft.Data.SqlClient;

namespace DbExplorer.Core.Tests;

/// <summary>
/// Against a real SQL Server: skipped unless env var DBX_MSSQL holds an ADO.NET connection string to a scratch database (see the
/// README). Only temp tables are created. Checks what the fakes cannot: that SqlClient delivers STATISTICS output through
/// InfoMessage, attributed to the statement that produced it, in the language of the session.
/// </summary>
public class StatisticsIntegrationTests
{
    private const string EnvVar = "DBX_MSSQL";

    private static string RequireConnectionString()
    {
        var cs = Environment.GetEnvironmentVariable(EnvVar);
        Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {EnvVar} to a SQL Server connection string to run this test.");
        return cs!;
    }

    private static async Task<List<StatementResult>> RunAsync(string connectionString, string script)
    {
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync();
        await StatisticsSession.EnableAsync(c, CancellationToken.None);
        var results = new List<StatementResult>();
        try
        {
            var statements = ScriptSplitter.Split(script, DbKind.SqlServer);
            await foreach (var r in ScriptRunner.RunAsync(c, statements, new ScriptOptions(Kind: DbKind.SqlServer, Statistics: true), CancellationToken.None))
                results.Add(r);
        }
        finally
        {
            Assert.True(await StatisticsSession.DisableAsync(c));
        }
        return results;
    }

    [SkippableFact]
    public async Task Statistics_ArriveAsMessages_OfTheStatementThatReadTheTable()
    {
        var cs = RequireConnectionString();

        var results = await RunAsync(cs,
            "CREATE TABLE #dbx_stats (id INT PRIMARY KEY, v INT); INSERT INTO #dbx_stats SELECT 1, 1; SELECT * FROM #dbx_stats; PRINT 'dbx hello'");

        Assert.All(results, r => Assert.Null(r.Error));
        var select = results[2].Messages!.SelectMany(m => StatisticsParser.Parse(m.Text)).ToList();
        var io = Assert.Single(select.OfType<IoStats>());
        Assert.StartsWith("#dbx_stats", io.Table);
        Assert.True(io.LogicalReads > 0);
        Assert.Contains(select, i => i is TimeStats { Phase: TimePhase.Execution });
        Assert.Contains(results[3].Messages!, m => m.Text.Contains("dbx hello"));
    }

    [SkippableFact]
    public async Task FailingStatement_KeepsItsStatisticsLines_AndItsErrorTextIsOnlyTheError()
    {
        var cs = RequireConnectionString();
        await using var setup = new SqlConnection(cs);
        await setup.OpenAsync();
        await StatisticsSession.EnableAsync(setup, CancellationToken.None);

        // One statement (no ';' / GO between the two lines): the read sends its IO lines, then the error ends the batch.
        var statements = new[] { new ScriptStatement("CREATE TABLE #t (id INT)", 0, 0) };
        var results = new List<StatementResult>();
        await foreach (var r in ScriptRunner.RunAsync(setup, statements, new ScriptOptions(Kind: DbKind.SqlServer, Statistics: true), CancellationToken.None))
            results.Add(r);
        var failing = new[] { new ScriptStatement("SELECT * FROM #t" + Environment.NewLine + "RAISERROR('x', 16, 1)", 0, 0) };
        await foreach (var r in ScriptRunner.RunAsync(setup, failing, new ScriptOptions(Kind: DbKind.SqlServer, Statistics: true), CancellationToken.None))
            results.Add(r);
        await StatisticsSession.DisableAsync(setup);

        var failed = results[^1];
        Assert.Equal("x", failed.Error);
        Assert.Contains(failed.Messages!.SelectMany(m => StatisticsParser.Parse(m.Text)), i => i is IoStats);
    }

    [SkippableFact]
    public async Task Statistics_InFrench_AreStillParsedByStructure()
    {
        var cs = RequireConnectionString();

        var results = await RunAsync(cs,
            "SET LANGUAGE French; CREATE TABLE #dbx_stats (id INT PRIMARY KEY); INSERT INTO #dbx_stats SELECT 1; SELECT * FROM #dbx_stats");

        var items = results[^1].Messages!.SelectMany(m => StatisticsParser.Parse(m.Text)).ToList();
        Assert.Single(items.OfType<IoStats>());
        Assert.Contains(items, i => i is TimeStats);
    }

    [SkippableFact]
    public async Task Statistics_AreOffAgainAfterTheRun()
    {
        var cs = RequireConnectionString();
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await StatisticsSession.EnableAsync(c, CancellationToken.None);
        await StatisticsSession.DisableAsync(c);
        var messages = new List<string>();
        c.InfoMessage += (_, e) => messages.Add(e.Message);

        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync();

        Assert.Empty(messages);
    }
}
