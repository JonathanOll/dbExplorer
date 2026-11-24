using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Tests;

/// <summary>The Statistics / Messages tables built from the messages of a run.</summary>
public class StatisticsReportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ServerMessage M(string text) => new(T0, text);

    private static string Io(string table, int scan, int logical, int physical = 0, int readAhead = 0) =>
        $"Table '{table}'. Scan count {scan}, logical reads {logical}, physical reads {physical}, read-ahead reads {readAhead}, " +
        "lob logical reads 0, lob physical reads 0, lob read-ahead reads 0.";

    private static string Io11(string table, int logical, int pageServer) =>
        $"Table '{table}'. Scan count 1, logical reads {logical}, physical reads 0, page server reads {pageServer}, read-ahead reads 0, " +
        "page server read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob page server reads 0, " +
        "lob read-ahead reads 0, lob page server read-ahead reads 0.";

    private static string Time(string header, int cpu, int elapsed) => $"{header}\n   CPU time = {cpu} ms, elapsed time = {elapsed} ms.";

    private static readonly string Parse = "SQL Server parse and compile time:";
    private static readonly string Exec = "SQL Server Execution Times:";

    private static StatementMessages One(int statement, params string[] texts) =>
        new(null, statement, texts.Select(M).ToList());

    [Fact]
    public void Statistics_ColumnsAndRows_OfASingleStatement()
    {
        var table = StatisticsReport.BuildStatistics([One(1,
            Time(Parse, 0, 3), Io("Invoices", 1, 5, 2, 4), Io("Worktable", 0, 7), Time(Exec, 15, 20))]);

        Assert.Equal(
            ["Statement", "Kind", "Table", "Scan count", "Logical reads", "Physical reads", "Read-ahead reads", "LOB logical reads",
             "LOB physical reads", "LOB read-ahead reads", "CPU ms", "Elapsed ms"],
            table.Columns);
        Assert.Equal(5, table.Rows.Count);
        Assert.Equal(new object?[] { 1L, "Parse/compile", null, null, null, null, null, null, null, null, 0L, 3L }, table.Rows[0]);
        Assert.Equal(new object?[] { 1L, "IO", "Invoices", 1L, 5L, 2L, 4L, 0L, 0L, 0L, null, null }, table.Rows[1]);
        Assert.Equal(new object?[] { 1L, "IO", "Worktable", 0L, 7L, 0L, 0L, 0L, 0L, 0L, null, null }, table.Rows[2]);
        Assert.Equal(new object?[] { 1L, "Execution", null, null, null, null, null, null, null, null, 15L, 20L }, table.Rows[3]);
        // Total: sums of every IO column, CPU and elapsed; a single statement has no grand total.
        Assert.Equal(new object?[] { 1L, "Total", null, 1L, 12L, 2L, 4L, 0L, 0L, 0L, 15L, 23L }, table.Rows[4]);
    }

    [Fact]
    public void Statistics_TwoStatements_GetATotalEachAndAGrandTotal()
    {
        var table = StatisticsReport.BuildStatistics([
            One(1, Io("A", 1, 10), Time(Exec, 5, 6)),
            One(2, Io("B", 2, 30), Time(Exec, 7, 8))]);

        var kinds = table.Rows.Select(r => (string)r[1]!).ToList();
        Assert.Equal(["IO", "Execution", "Total", "IO", "Execution", "Total", "Grand total"], kinds);
        var grand = table.Rows[^1];
        Assert.Null(grand[0]);
        Assert.Equal(3L, grand[3]);     // scan count
        Assert.Equal(40L, grand[4]);    // logical reads
        Assert.Equal(12L, grand[^2]);   // CPU
        Assert.Equal(14L, grand[^1]);   // elapsed
    }

    [Fact]
    public void Statistics_StatementsWithoutStatisticsAddNothing()
    {
        var table = StatisticsReport.BuildStatistics([
            One(1, "just a PRINT"), One(2, Io("B", 1, 2)), One(3)]);

        Assert.Equal(new object?[] { 2L, 2L }, table.Rows.Select(r => r[0]).ToArray());   // IO + Total of statement 2, no grand total
        Assert.Equal(["IO", "Total"], table.Rows.Select(r => (string)r[1]!).ToArray());
    }

    [Fact]
    public void Statistics_PageServerColumns_OnlyWhenOneIsNotZero()
    {
        var without = StatisticsReport.BuildStatistics([One(1, Io11("A", 5, 0))]);
        Assert.DoesNotContain("Page server reads", without.Columns);

        var with = StatisticsReport.BuildStatistics([One(1, Io11("A", 5, 0), Io11("B", 1, 9))]);
        Assert.Contains("Page server reads", with.Columns);
        Assert.Equal(4, with.Columns.Count(c => c.Contains("page server", StringComparison.OrdinalIgnoreCase)));
        var psIndex = with.Columns.ToList().IndexOf("Page server reads");
        Assert.Equal(9L, with.Rows[1][psIndex]);
        Assert.Equal(9L, with.Rows[^1][psIndex]);   // the Total row sums them too
    }

    [Fact]
    public void Statistics_Numbers_AreBoxedLongs_ForRightAlignmentAndNumericSort()
    {
        var table = StatisticsReport.BuildStatistics([One(1, Io("A", 1, 10), Time(Exec, 5, 6))]);

        foreach (var row in table.Rows)
            for (var i = 3; i < row.Length; i++)
                Assert.True(row[i] is null or long, $"column {table.Columns[i]} holds {row[i]?.GetType()}");
    }

    [Fact]
    public void Statistics_FrenchRun_GivesTheSameTableAsEnglish()
    {
        var en = StatisticsReport.BuildStatistics([One(1, Io("A", 1, 10, 2), "SQL Server Execution Times:\n CPU time = 5 ms, elapsed time = 6 ms.")]);
        var fr = StatisticsReport.BuildStatistics([One(1,
            "Table 'A'. Nombre d'analyses 1, lectures logiques 10, lectures physiques 2, lectures anticipées 0, lectures logiques de type LOB 0, lectures physiques de type LOB 0, lectures anticipées de type LOB 0.",
            "Temps d'exécution de SQL Server : \n Temps UC = 5 ms, temps écoulé = 6 ms.")]);

        Assert.Equal(en.Columns, fr.Columns);
        Assert.Equal(en.Rows, fr.Rows);
    }

    [Fact]
    public void Statistics_Group_LeadsWithDatabase_AndTotalsPerDatabase()
    {
        var table = StatisticsReport.BuildStatistics([
            new StatementMessages("Prod", 1, [M(Io("A", 1, 10))]),
            new StatementMessages("Prod", 2, [M(Io("A", 1, 20))]),
            new StatementMessages("Test", 1, [M(Io("A", 1, 99))])]);

        Assert.Equal("Database", table.Columns[0]);
        Assert.Equal("Statement", table.Columns[1]);
        var logical = table.Columns.ToList().IndexOf("Logical reads");
        var prod = table.Rows.Where(r => (string?)r[0] == "Prod").ToList();
        var test = table.Rows.Where(r => (string?)r[0] == "Test").ToList();
        Assert.Equal(["IO", "Total", "IO", "Total", "Grand total"], prod.Select(r => (string)r[2]!).ToArray());
        Assert.Equal(30L, prod[^1][logical]);
        Assert.Equal(["IO", "Total"], test.Select(r => (string)r[2]!).ToArray());
        Assert.Equal(99L, test[^1][logical]);
    }

    [Fact]
    public void Statistics_Empty_HasColumnsAndNoRows()
    {
        var table = StatisticsReport.BuildStatistics([]);

        Assert.NotEmpty(table.Columns);
        Assert.Empty(table.Rows);
        Assert.False(StatisticsReport.HasStatistics([One(1, "hello")]));
        Assert.True(StatisticsReport.HasStatistics([One(1, Time(Exec, 1, 1))]));
    }

    [Fact]
    public void Messages_OneRowPerLine_WithStatementAndTime()
    {
        var table = StatisticsReport.BuildMessages([
            One(1, "hello\r\nworld", Io("A", 1, 2)),
            One(2, "", "bye")]);

        Assert.Equal(["Statement", "Time", "Message"], table.Columns);
        Assert.Equal(4, table.Rows.Count);
        Assert.Equal(new object?[] { 1L, 1L, 1L, 2L }, table.Rows.Select(r => r[0]).ToArray());
        Assert.Equal(["hello", "world", Io("A", 1, 2), "bye"], table.Rows.Select(r => (string)r[2]!).ToArray());
        Assert.Matches(@"^\d\d:\d\d:\d\d\.\d\d\d$", (string)table.Rows[0][1]!);
    }

    [Fact]
    public void Messages_Group_HasADatabaseColumn()
    {
        var table = StatisticsReport.BuildMessages([new StatementMessages("Prod", 3, [M("hi")])]);

        Assert.Equal(["Database", "Statement", "Time", "Message"], table.Columns);
        Assert.Equal("Prod", table.Rows[0][0]);
        Assert.Equal(3L, table.Rows[0][1]);
    }
}
