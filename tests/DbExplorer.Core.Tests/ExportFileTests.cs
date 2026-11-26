using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DbExplorer.Core.Config;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Export;
using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;
using DbExplorer.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

/// <summary>Export files: encodings, XLSX package, cancel without leftovers, and "all rows" streamed from a real database.</summary>
public sealed class ExportFileTests : IDisposable
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly IReadOnlyList<ExportColumn> Cols = [new("id"), new("name")];

    private readonly string _dir = Directory.CreateTempSubdirectory("dbx-export-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private string File_(string name) => Path.Combine(_dir, name);

    private static IEnumerable<object?[]> Many(int count) => Enumerable.Range(1, count).Select(i => new object?[] { i, "row " + i });

    [Theory]
    [InlineData(ExportKind.Csv, ExportEncoding.Automatic, "EFBBBF")]
    [InlineData(ExportKind.Json, ExportEncoding.Automatic, "5B")]
    [InlineData(ExportKind.Csv, ExportEncoding.Utf8, "6964")]
    [InlineData(ExportKind.Csv, ExportEncoding.Utf16, "FFFE")]
    public async Task Encodings_AndByteOrderMarks(ExportKind kind, ExportEncoding encoding, string start)
    {
        var path = File_("e.txt");

        var outcome = await Exporter.ToFileAsync(new(kind) { Encoding = encoding }, path, Exporter.FromRows(Cols, [[1, "é"]]), null, default);

        Assert.Equal(1, outcome.Rows);
        Assert.StartsWith(start, Convert.ToHexString(await File.ReadAllBytesAsync(path)));
        Assert.Contains("é", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Windows1252_WritesOneBytePerAccent()
    {
        var path = File_("w.csv");

        await Exporter.ToFileAsync(new(ExportKind.Csv) { Encoding = ExportEncoding.Windows1252, Text = new() { Header = false } },
            path, Exporter.FromRows(Cols, [[1, "é€"]]), null, default);

        Assert.Equal([(byte)'1', (byte)',', 0xE9, 0x80, 13, 10], await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ALargeExport_StreamsEveryRow_AndReportsProgress()
    {
        var path = File_("big.csv");
        var seen = new List<long>();

        var outcome = await Exporter.ToFileAsync(new(ExportKind.Csv), path, Exporter.FromRows(Cols, Many(200_000)), new Sync(seen.Add), default);

        Assert.Equal(200_000, outcome.Rows);
        Assert.Equal(200_001, File.ReadLines(path).Count());
        Assert.Equal(200_000, seen[^1]);
        Assert.True(seen.Count > 10);
    }

    private sealed class Sync(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    [Theory]
    [InlineData(ExportKind.Csv)]
    [InlineData(ExportKind.Xlsx)]
    public async Task Cancel_LeavesNoPartialFile_AndKeepsTheOldOne(ExportKind kind)
    {
        var path = File_("keep.out");
        await File.WriteAllTextAsync(path, "old");
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Exporter.ToFileAsync(
            new(kind), path, Exporter.FromRows(Cols, Many(100_000)), new Sync(_ => cts.Cancel()), cts.Token));

        Assert.Equal("old", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Failure_LeavesNoFile()
    {
        var spec = new ExportSpec(ExportKind.SqlUpdate);   // no key: refused

        await Assert.ThrowsAsync<ExportException>(() => Exporter.ToFileAsync(spec, File_("x.sql"), Exporter.FromRows(Cols, Many(1)), null, default));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    private static XDocument Part(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream);
    }

    [Fact]
    public async Task Xlsx_IsAReadablePackage_WithTypedCells()
    {
        var path = File_("a.xlsx");
        IReadOnlyList<ExportColumn> columns = [new("int"), new("text"), new("null"), new("bool"), new("date"), new("big"), new("old")];

        await Exporter.ToFileAsync(new(ExportKind.Xlsx), path, Exporter.FromRows(columns,
            [[42, "=1+1 <é> 😀 \u0001 _x0041_", null, true, new DateTime(2024, 3, 5), 1234567890123456789L, new DateTime(1800, 1, 1)]]), null, default);

        using var zip = ZipFile.OpenRead(path);
        foreach (var name in new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml" })
            Part(zip, name);
        var rows = Part(zip, "xl/worksheets/sheet1.xml").Descendants(Ns + "row").ToList();
        Assert.Equal(2, rows.Count);
        var cells = rows[1].Elements(Ns + "c").ToDictionary(c => (string)c.Attribute("r")!);
        Assert.Equal("42", cells["A2"].Value);
        Assert.Equal("=1+1 <é> 😀 _x0001_ _x005F_x0041_", cells["B2"].Value);
        Assert.Empty(cells["B2"].Descendants(Ns + "f"));
        Assert.False(cells.ContainsKey("C2"));
        Assert.Equal("b", (string)cells["D2"].Attribute("t")!);
        Assert.Equal("45356", cells["E2"].Value);
        Assert.Equal("1234567890123456789", cells["F2"].Value);
        Assert.Equal("inlineStr", (string)cells["F2"].Attribute("t")!);
        Assert.Equal("1800-01-01", cells["G2"].Value);
    }

    [Fact]
    public void Xlsx_StartsANewSheetWhenOneIsFull_AndSaysWhenCellsAreCut()
    {
        using var stream = new MemoryStream();
        using (var writer = new XlsxWriter(stream, header: true, rowsPerSheet: 3))
        {
            writer.Begin([new("t")]);
            for (var i = 0; i < 4; i++) writer.Write([new string('x', XlsxWriter.MaxCellChars + 5)]);
            writer.End();
            Assert.Contains("4 cell(s)", writer.Warning);
        }

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Equal(2, Part(zip, "xl/workbook.xml").Descendants(Ns + "sheet").Count());
        Assert.Equal(3, Part(zip, "xl/worksheets/sheet2.xml").Descendants(Ns + "row").Count());
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(16383, "XFD")]
    public void Xlsx_ColumnLetters(int index, string letters) => Assert.Equal(letters, XlsxWriter.ColumnLetters(index));

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plain) => plain;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    // ---- all rows from a database ----

    private async Task<(Session Session, TableInfo Table)> OpenDatabase()
    {
        var file = File_("data.db");
        await using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT);
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1000)
                INSERT INTO t SELECT i, CASE WHEN i % 2 = 0 THEN 'even' ELSE 'odd' END FROM n;
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var config = new ConnectionConfig(Guid.NewGuid(), "x", DbKind.Sqlite, null, null, null, file, null, null, false, false, false);
        var session = new Session(config, DriverFactory.For(DbKind.Sqlite), []);
        await using var open = await session.OpenConnectionAsync(default);
        var schema = await session.Driver.LoadSchemaAsync(open, default);
        return (session, schema.Single(t => t.Ref.Name == "t"));
    }

    [Fact]
    public async Task AllRowsOfATable_HonourFilterWhereAndSort_WithoutPaging()
    {
        var (session, table) = await OpenDatabase();
        using var _ = session;
        var query = new TableQuery(table, [new ColumnFilter("name", "=even")], "id > 10", [new SortSpec("id", true)], 200, 200);
        var path = File_("t.csv");

        var outcome = await Exporter.ToFileAsync(new(ExportKind.Csv), path, QueryRows.FromTable(session, query), null, default);

        Assert.Equal(495, outcome.Rows);
        Assert.Equal(["id,name", "1000,even", "998,even"], File.ReadLines(path).Take(3));
    }

    [Fact]
    public async Task AStatement_RunsAgainWithoutARowLimit_AndAWriteNeverRuns()
    {
        var (session, _) = await OpenDatabase();
        using var __ = session;
        var path = File_("s.csv");

        var outcome = await Exporter.ToFileAsync(new(ExportKind.Csv), path, QueryRows.FromStatement(session, "SELECT id FROM t"), null, default);

        Assert.Equal(1000, outcome.Rows);
        foreach (var write in new[] { "DELETE FROM t", "SELECT 1; DELETE FROM t", "UPDATE t SET name = 'x' RETURNING *" })
        {
            Assert.False(QueryRows.CanRunAgain(write, DbKind.Sqlite));
            Assert.Throws<ExportException>(() => QueryRows.FromStatement(session, write));
        }
        await Assert.ThrowsAsync<ExportException>(() =>
            Exporter.ToFileAsync(new(ExportKind.Csv), File_("n.csv"), QueryRows.FromStatement(session, "SELECT 1", setIndex: 2), null, default));
    }

    [Fact]
    public async Task CancellingAnEndlessQuery_StopsIt_AndLeavesNoFile()
    {
        var (session, _) = await OpenDatabase();
        using var __ = session;
        const string endless = "WITH RECURSIVE c(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM c) SELECT n FROM c WHERE n < 0";
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Exporter
            .ToFileAsync(new(ExportKind.Csv), File_("c.csv"), QueryRows.FromStatement(session, endless), null, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.DoesNotContain(Directory.GetFiles(_dir), f => f.Contains("c.csv"));
    }

    [Fact]
    public void BuildAll_HasNoPaging()
    {
        var table = new TableInfo(new TableRef("dbo", "t"), false, [new ColumnInfo("id", "int", typeof(int), false, false, 0)], [], []);

        var sql = TableQueryBuilder.BuildAll(DriverFactory.For(DbKind.SqlServer), new TableQuery(table, [], null, [], 400, 200)).Sql;

        Assert.Equal("SELECT * FROM [dbo].[t]", sql);
    }

    [Fact]
    public void ExportPreferences_AreKeptByTheConfigStore()
    {
        var store = new ConfigStore(File_("config.json"), new PlainProtector());
        var config = AppConfig.Empty();
        config.Export = new ExportPreferences { Format = ExportKind.Xlsx, GuardFormulas = true, Copy = new() { Delimiter = ";", Quoting = QuoteMode.Always } };

        store.Save(config);

        Assert.Equal(config.Export, store.Load().Export);
        File.WriteAllText(File_("config.json"), "{}");
        Assert.Equal(new ExportPreferences(), store.Load().Export);
    }
}
