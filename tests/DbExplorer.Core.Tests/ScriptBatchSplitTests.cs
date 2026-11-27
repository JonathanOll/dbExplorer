using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Tests;

/// <summary>T-SQL variables live in their batch: a run must not send a DECLARE apart from the statements that use it.</summary>
public class ScriptBatchSplitTests
{
    [Fact]
    public void SqlServer_BatchWithVariable_StaysWhole()
    {
        var statements = ScriptSplitter.SplitForRun("DECLARE @x int = 1;\nSELECT @x;\nGO\nSELECT 2; SELECT 3", DbKind.SqlServer);

        Assert.Equal(["DECLARE @x int = 1;\nSELECT @x;", "SELECT 2", "SELECT 3"], statements.Select(s => s.Text).ToArray());
        Assert.Equal([true, false, false], statements.Select(s => s.IsBatch).ToArray());
        Assert.Equal(0, statements[0].Start);
        Assert.Equal(statements[0].Text.Length, statements[0].End);
    }

    [Theory]
    [InlineData("DECLARE @t TABLE (a int); INSERT INTO @t VALUES (1)", 1)]
    [InlineData("SELECT 'DECLARE @x'; SELECT 2", 2)]
    [InlineData("-- DECLARE @x int\nSELECT 1; SELECT 2", 2)]
    [InlineData("DECLARE c CURSOR FOR SELECT 1; OPEN c", 2)]
    public void SqlServer_OnlyARealVariableKeepsTheBatch(string script, int count) =>
        Assert.Equal(count, ScriptSplitter.SplitForRun(script, DbKind.SqlServer).Count);

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.Sqlite)]
    public void OtherEngines_SplitAsUsual(DbKind kind) =>
        Assert.Equal(2, ScriptSplitter.SplitForRun("DECLARE @x CURSOR FOR SELECT 1; SELECT 2", kind).Count);

    [Fact]
    public async Task CapturedMessages_WithoutStatistics_LandOnTheirStatement()
    {
        var connection = new MessagingConnection { Says = sql => sql == "PRINT 'hi'" ? ["hi"] : [] };
        var statements = ScriptSplitter.Split("PRINT 'hi'; SELECT 2", DbKind.SqlServer);

        var results = new List<StatementResult>();
        await foreach (var r in ScriptRunner.RunAsync(connection, statements, new ScriptOptions(CaptureMessages: true), CancellationToken.None))
            results.Add(r);

        Assert.Equal("hi", Assert.Single(results[0].Messages!).Text);
        Assert.Empty(results[1].Messages!);
        Assert.DoesNotContain(connection.Log, l => l.StartsWith("SET STATISTICS", StringComparison.Ordinal));
    }
}
