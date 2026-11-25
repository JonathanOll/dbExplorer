using System.Text.Json;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Tests;

/// <summary>The text of copied and exported results: quoting, NULL, line breaks, JSON types, and the export through the tab.</summary>
public class ResultExportTests : IDisposable
{
    private static readonly IReadOnlyList<ResultColumn> Cols = [new("id"), new("name"), new("note")];

    private readonly ScriptTabFixture _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void Csv_QuotesCommasQuotesAndLineBreaks_NullIsEmpty_AndEndsLinesWithCrLf()
    {
        object?[][] rows = [[1L, "a,b", null], [2L, "say \"hi\"", "line1\nline2"], [3L, " padded ", ""]];

        var csv = ResultExport.ToCsv(Cols, rows);

        Assert.Equal("id,name,note\r\n1,\"a,b\",\r\n2,\"say \"\"hi\"\"\",\"line1\nline2\"\r\n3,\" padded \",\r\n", csv);
    }

    [Fact]
    public void Csv_QuotesAColumnNameThatNeedsIt()
    {
        var csv = ResultExport.ToCsv([new ResultColumn("a,b")], []);

        Assert.Equal("\"a,b\"\r\n", csv);
    }

    [Fact]
    public void Tsv_HasAHeader_KeepsCommas_AndQuotesTabsAndNewlines()
    {
        object?[][] rows = [[1L, "a,b", "x\ty"], [2L, null, "l1\r\nl2"]];

        var tsv = ResultExport.ToTsv(Cols, rows);

        Assert.Equal("id\tname\tnote\r\n1\ta,b\t\"x\ty\"\r\n2\t\t\"l1\r\nl2\"\r\n", tsv);
    }

    [Fact]
    public void Cells_AreWrittenInTheInvariantCulture()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
        try
        {
            Assert.Equal("1234.5", ResultExport.FormatCell(1234.5));
            Assert.Equal("2024-03-05 09:30:00", ResultExport.FormatCell(new DateTime(2024, 3, 5, 9, 30, 0)));
            Assert.Equal("2024-03-05", ResultExport.FormatCell(new DateTime(2024, 3, 5)));
            Assert.Equal("0x0AFF", ResultExport.FormatCell(new byte[] { 0x0A, 0xFF }));
            Assert.Equal("true", ResultExport.FormatCell(true));
            Assert.Equal("", ResultExport.FormatCell(DBNull.Value));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Json_IsAnArrayOfObjects_WithNativeNumbersBooleansAndNull()
    {
        IReadOnlyList<ResultColumn> cols = [new("id"), new("name"), new("ok"), new("price"), new("when")];
        object?[][] rows = [[1L, "é \"q\"\nx", true, 9.5m, new DateTime(2024, 3, 5, 9, 30, 0)], [2L, null, false, null, null]];

        using var doc = JsonDocument.Parse(ResultExport.ToJson(cols, rows));

        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(1, items[0].GetProperty("id").GetInt64());
        Assert.Equal("é \"q\"\nx", items[0].GetProperty("name").GetString());
        Assert.True(items[0].GetProperty("ok").GetBoolean());
        Assert.Equal(9.5m, items[0].GetProperty("price").GetDecimal());
        Assert.Equal("2024-03-05 09:30:00", items[0].GetProperty("when").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("name").ValueKind);
        Assert.False(items[1].GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Json_OfNoRows_IsAnEmptyArray()
    {
        using var doc = JsonDocument.Parse(ResultExport.ToJson(Cols, []));

        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    // ---- through the tab ----

    [Fact]
    public async Task Copy_UsesTheSelectedRows_OrAllWhenNoneAreSelected()
    {
        var tab = await _t.NewTab("SELECT id, name FROM t ORDER BY id");
        await tab.RunScriptAsync();
        var set = tab.Results[0].Sets[0];

        var all = tab.CopyText(set, null);
        Assert.Equal(4, all.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);   // header + 3 rows
        Assert.Contains("Copied 3 rows", _t.Status.Message);

        var one = tab.CopyText(set, [set.Rows[2]]);
        Assert.Equal("id\tname\r\n3\tgamma\r\n", one);
        Assert.Contains("Copied 1 row", _t.Status.Message);
    }

    [Fact]
    public async Task Export_WritesCsvWithBom_AndJsonWithout_AndSaysSo()
    {
        var tab = await _t.NewTab("SELECT id, name FROM t ORDER BY id");
        await tab.RunScriptAsync();
        var set = tab.Results[0].Sets[0];
        var csv = _t.TempFile("out.csv");
        var json = _t.TempFile("out.json");

        _t.Files.ExportPath = csv;
        await tab.ExportAsync(set, ExportFormat.Csv, null);
        var csvBytes = await File.ReadAllBytesAsync(csv);
        Assert.Equal([0xEF, 0xBB, 0xBF], csvBytes[..3]);
        Assert.Equal("id,name\r\n1,alpha\r\n2,\r\n3,gamma\r\n", System.Text.Encoding.UTF8.GetString(csvBytes[3..]));
        Assert.Contains("Exported 3 rows", _t.Status.Message);
        Assert.Equal(("Script 1 result.csv", "csv"), _t.Files.LastExport);

        _t.Files.ExportPath = json;
        await tab.ExportAsync(set, ExportFormat.Json, [set.Rows[0]]);
        var jsonBytes = await File.ReadAllBytesAsync(json);
        Assert.NotEqual(0xEF, jsonBytes[0]);
        using var doc = JsonDocument.Parse(jsonBytes);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Contains("Exported 1 row", _t.Status.Message);
    }

    [Fact]
    public async Task Export_Cancelled_WritesNothing_AndAFailureIsReported()
    {
        var tab = await _t.NewTab("SELECT id FROM big");
        tab.RowLimit = 5;
        await tab.RunScriptAsync();
        var set = tab.Results[0].Sets[0];

        _t.Files.ExportPath = null;
        await tab.ExportAsync(set, ExportFormat.Csv, null);
        Assert.DoesNotContain("Exported", _t.Status.Message);

        _t.Files.ExportPath = _t.TempFile("no-such-folder/x.csv");
        await tab.ExportAsync(set, ExportFormat.Csv, null);
        Assert.True(_t.Status.IsError);
        Assert.Contains("Cannot export", _t.Status.Message);

        _t.Files.ExportPath = _t.TempFile("ok.csv");
        await tab.ExportAsync(set, ExportFormat.Csv, null);
        Assert.Contains("limited to the first 5 rows", _t.Status.Message);   // the truncated result says what the file holds
    }
}
