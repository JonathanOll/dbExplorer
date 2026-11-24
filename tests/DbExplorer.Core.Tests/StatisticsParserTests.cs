using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Tests;

/// <summary>STATISTICS IO / TIME lines in the server's own language: parsed by structure, never by English keywords.</summary>
public class StatisticsParserTests
{
    private const string En11 =
        "Table 'Invoices'. Scan count 1, logical reads 5, physical reads 0, page server reads 0, read-ahead reads 0, " +
        "page server read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob page server reads 0, " +
        "lob read-ahead reads 0, lob page server read-ahead reads 0.";

    private const string En7 =
        "Table 'Invoices'. Scan count 1, logical reads 3, physical reads 2, read-ahead reads 4, lob logical reads 5, " +
        "lob physical reads 6, lob read-ahead reads 7.";

    private static IoStats Io(string text) => Assert.IsType<IoStats>(Assert.Single(StatisticsParser.Parse(text)));

    private static TimeStats Time(string text) => Assert.IsType<TimeStats>(Assert.Single(StatisticsParser.Parse(text)));

    [Fact]
    public void Io_English11Numbers_MapsEveryColumn()
    {
        var io = Io("Table 'Invoices'. Scan count 1, logical reads 2, physical reads 3, page server reads 4, read-ahead reads 5, " +
                    "page server read-ahead reads 6, lob logical reads 7, lob physical reads 8, lob page server reads 9, " +
                    "lob read-ahead reads 10, lob page server read-ahead reads 11.");

        Assert.Equal(new IoStats("Invoices", 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11), io);
    }

    [Fact]
    public void Io_RealEnglishSampleWithZeros()
    {
        var io = Io(En11);

        Assert.Equal("Invoices", io.Table);
        Assert.Equal(1, io.ScanCount);
        Assert.Equal(5, io.LogicalReads);
        Assert.Equal(0, io.PhysicalReads);
    }

    [Fact]
    public void Io_English7Numbers_LeavesPageServerColumnsAtZero()
    {
        var io = Io(En7);

        Assert.Equal(new IoStats("Invoices", 1, 3, 2, 0, 4, 0, 5, 6, 0, 7, 0), io);
    }

    [Fact]
    public void Io_French7_MeansTheSameAsEnglish7()
    {
        var fr = Io(
            "Table 'Invoices'. Nombre d'analyses 1, lectures logiques 3, lectures physiques 2, lectures anticipées 4, " +
            "lectures logiques de type LOB 5, lectures physiques de type LOB 6, lectures anticipées de type LOB 7.");

        Assert.Equal(Io(En7), fr);
    }

    [Fact]
    public void Io_French11_MeansTheSameAsEnglish11()
    {
        var fr = Io(
            "Table 'Invoices'. Nombre d'analyses 1, lectures logiques 2, lectures physiques 3, lectures du serveur de pages 4, " +
            "lectures anticipées 5, lectures anticipées du serveur de pages 6, lectures logiques de type LOB 7, " +
            "lectures physiques de type LOB 8, lectures du serveur de pages de type LOB 9, lectures anticipées de type LOB 10, " +
            "lectures anticipées du serveur de pages de type LOB 11.");

        Assert.Equal(new IoStats("Invoices", 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11), fr);
    }

    [Theory]
    [InlineData("Worktable")]
    [InlineData("Workfile")]
    [InlineData("Table de travail")]
    [InlineData("#orders")]
    [InlineData("#t_____________________________________________________________________________________________000000000005")]
    [InlineData("two words")]
    [InlineData("it's here")]
    [InlineData("dbo.Orders")]
    public void Io_TableNames_AreTakenFromTheQuotes(string name)
    {
        Assert.Equal(name, Io($"Table '{name}'. Scan count 0, logical reads 0, physical reads 0, read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob read-ahead reads 0.").Table);
        Assert.Equal(name, Io($"Table '{name}'. Nombre d'analyses 0, lectures logiques 0, lectures physiques 0, lectures anticipées 0, lectures logiques de type LOB 0, lectures physiques de type LOB 0, lectures anticipées de type LOB 0.").Table);
    }

    [Fact]
    public void Io_LeadingWordsBeforeTheName_AreAccepted()
    {
        Assert.Equal("x", Io("Table de travail 'x'. Nombre d'analyses 0, lectures logiques 0, lectures physiques 0, lectures anticipées 0, lectures logiques de type LOB 0, lectures physiques de type LOB 0, lectures anticipées de type LOB 0.").Table);
    }

    [Fact]
    public void Io_LargeNumbers_FitInALong()
    {
        var io = Io("Table 'big'. Scan count 1, logical reads 9876543210, physical reads 0, read-ahead reads 3000000000, lob logical reads 0, lob physical reads 0, lob read-ahead reads 0.");

        Assert.Equal(9_876_543_210, io.LogicalReads);
        Assert.Equal(3_000_000_000, io.ReadAheadReads);
    }

    [Fact]
    public void Io_NumberTooBigForALong_StaysAPlainMessage()
    {
        var items = StatisticsParser.Parse("Table 'big'. Scan count 1, logical reads 99999999999999999999, physical reads 0, read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob read-ahead reads 0.");

        Assert.IsType<TextLine>(Assert.Single(items));
    }

    [Theory]
    [InlineData("Table 'x'. Scan count 1, logical reads 3.")]
    [InlineData("Table 'x'. Scan count 1, logical reads 3, physical reads 0, read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob read-ahead reads 0, extra 9.")]
    [InlineData("Table 'x'. Scan count 1, logical reads 3, physical reads 0, read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob read-ahead reads 0, a 1, b 2, c 3.")]
    [InlineData("Segment reads 1, segment skipped 0.")]
    [InlineData("Table 'x'.")]
    [InlineData("Table x. Scan count 1, logical reads 3, physical reads 0, read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob read-ahead reads 0.")]
    public void Io_WrongShape_IsAPlainMessage(string text)
    {
        Assert.IsType<TextLine>(Assert.Single(StatisticsParser.Parse(text)));
    }

    [Fact]
    public void Time_EnglishParseCompile_AndExecution()
    {
        Assert.Equal(new TimeStats(TimePhase.ParseCompile, 0, 3),
            Time("SQL Server parse and compile time: \r\n   CPU time = 0 ms, elapsed time = 3 ms."));
        Assert.Equal(new TimeStats(TimePhase.Execution, 15, 20),
            Time("SQL Server Execution Times:\r\n   CPU time = 15 ms,  elapsed time = 20 ms."));
    }

    [Fact]
    public void Time_French_ParseCompile_AndExecution()
    {
        Assert.Equal(new TimeStats(TimePhase.ParseCompile, 0, 3),
            Time("Temps d'analyse et de compilation de SQL Server : \n Temps UC = 0 ms, temps écoulé = 3 ms."));
        Assert.Equal(new TimeStats(TimePhase.Execution, 15, 20),
            Time("Temps d'exécution de SQL Server : \n Temps UC = 15 ms, temps écoulé = 20 ms."));
    }

    [Fact]
    public void Time_WithoutAHeader_IsExecution()
    {
        Assert.Equal(new TimeStats(TimePhase.Execution, 15, 20), Time("Temps UC = 15 ms, temps écoulé = 20 ms."));
        Assert.Equal(new TimeStats(TimePhase.Execution, 1, 2), Time("CPU time = 1 ms, elapsed time = 2 ms."));
    }

    [Fact]
    public void Time_OtherLanguageMarkers_StillClassifiedAsParse()
    {
        Assert.Equal(TimePhase.ParseCompile,
            Time("Tiempo de análisis y compilación de SQL Server: \n Tiempo de CPU = 0 ms, tiempo transcurrido = 1 ms.").Phase);
    }

    [Fact]
    public void Print_AndWarnings_StayPlain()
    {
        var items = StatisticsParser.Parse("Warning: Null value is eliminated by an aggregate or other SET operation.");

        Assert.Equal("Warning: Null value is eliminated by an aggregate or other SET operation.", Assert.IsType<TextLine>(Assert.Single(items)).Text);
        Assert.IsType<TextLine>(Assert.Single(StatisticsParser.Parse("took 5 ms")));
        Assert.IsType<TextLine>(Assert.Single(StatisticsParser.Parse("a = 1 ms, b = 2 ms, c = 3 ms")));
    }

    [Fact]
    public void MultiLineMessage_IsSplitPerLine_AndIoLinesAreStillFound()
    {
        var items = StatisticsParser.Parse(En7 + "\r\nSegment reads 1, segment skipped 0.\r\n\r\nhello");

        Assert.Equal(3, items.Count);
        Assert.IsType<IoStats>(items[0]);
        Assert.Equal("Segment reads 1, segment skipped 0.", ((TextLine)items[1]).Text);
        Assert.Equal("hello", ((TextLine)items[2]).Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\r\n")]
    public void EmptyInput_GivesNothing(string? text) => Assert.Empty(StatisticsParser.Parse(text));

    [Theory]
    [InlineData("'")]
    [InlineData("'. ")]
    [InlineData("'''. 1, 2, 3, 4, 5, 6, 7.")]
    [InlineData("= ms = ms")]
    [InlineData("\u0001garbage 'x'. 1 2 3")]
    public void Garbage_NeverThrows(string text) => Assert.NotNull(StatisticsParser.Parse(text));
}
