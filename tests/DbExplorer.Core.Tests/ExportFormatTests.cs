using System.Collections;
using System.Globalization;
using System.Text.Json;
using DbExplorer.Core.Export;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Tests;

/// <summary>The text of copied and exported rows: quoting, NULL, dates, binary, unicode, formula protection, SQL literals.</summary>
public class ExportFormatTests
{
    private static readonly IReadOnlyList<ExportColumn> Cols = [new("id"), new("name"), new("note")];

    private static string Text(ExportSpec spec, IReadOnlyList<ExportColumn> columns, params object?[][] rows) => Exporter.ToText(spec, columns, rows);

    private static ExportSpec Delimited(DelimitedOptions options) => new(ExportKind.Delimited) { Text = options };

    [Fact]
    public void Csv_QuotesCommasQuotesLineBreaksAndEdgeSpaces_AndNullIsEmpty()
    {
        var csv = Text(new(ExportKind.Csv), Cols, [1L, "a,b", null], [2L, "say \"hi\"", "line1\nline2"], [3L, " padded ", ""]);

        Assert.Equal("id,name,note\r\n1,\"a,b\",\r\n2,\"say \"\"hi\"\"\",\"line1\nline2\"\r\n3,\" padded \",", csv);
    }

    [Fact]
    public void Tsv_KeepsCommas_AndQuotesTabs()
    {
        var tsv = Text(new(ExportKind.Tsv), Cols, [1L, "a,b", "x\ty"]);

        Assert.Equal("id\tname\tnote\r\n1\ta,b\t\"x\ty\"", tsv);
    }

    [Fact]
    public void Delimited_HonoursDelimiterRowSeparatorRowNumbersQuotingAndNullText()
    {
        var options = new DelimitedOptions { Delimiter = "||", RowSeparator = "\n", RowNumbers = true, NullText = "NULL" };

        Assert.Equal("#||id||name||note\n1||1||NULL||\"NULL\"", Text(Delimited(options), Cols, [1, null, "NULL"]));
        Assert.Equal("\"1\"\t\"a\"\t", Text(Delimited(new() { Header = false, Quoting = QuoteMode.Always }), Cols, [1, "a", null]));
        Assert.Equal("1,a,b,", Text(Delimited(new() { Header = false, Delimiter = ",", Quoting = QuoteMode.Never }), Cols, [1, "a,b", null]));
    }

    [Fact]
    public void Dates_AreIso_OrTheGivenFormat_InTheInvariantCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
        try
        {
            Assert.Equal("1234.5", ValueText.Format(1234.5));
            Assert.Equal("2024-03-05 09:30:00", ValueText.Format(new DateTime(2024, 3, 5, 9, 30, 0)));
            Assert.Equal("2024-03-05", ValueText.Format(new DateTime(2024, 3, 5)));
            Assert.Equal("2024-03-05 09:30:00+02:00", ValueText.Format(new DateTimeOffset(2024, 3, 5, 9, 30, 0, TimeSpan.FromHours(2))));
            Assert.Equal("05/03/2024", ValueText.Format(new DateOnly(2024, 3, 5), "dd/MM/yyyy"));
            Assert.Equal("0x0AFF", ValueText.Format(new byte[] { 0x0A, 0xFF }));
            Assert.Equal("{1,NULL,3}", ValueText.Format(new int?[] { 1, null, 3 }));
            Assert.Equal("", ValueText.Format(DBNull.Value));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
        Assert.Throws<ExportException>(() => ValueText.Format(DateTime.Now, "%"));
    }

    [Fact]
    public void FormulaGuard_PrefixesTextOnly_AndIsOffByDefault()
    {
        object?[] row = [-5, "=cmd|' /C calc'!A1", "@x"];
        IReadOnlyList<ExportColumn> columns = [new("n"), new("+head"), new("c")];

        Assert.Equal("n,+head,c\r\n-5,=cmd|' /C calc'!A1,@x", Text(new(ExportKind.Csv), columns, row));
        var guarded = new ExportSpec(ExportKind.Csv) { Text = new() { GuardFormulas = true } };
        Assert.Equal("n,'+head,c\r\n-5,'=cmd|' /C calc'!A1,'@x", Text(guarded, columns, row));
        foreach (var start in new[] { "+1", "-1", "\t=1", "\r=1", "＝1" }) Assert.True(DelimitedWriter.IsFormulaLike(start));
        Assert.False(DelimitedWriter.IsFormulaLike("a=1"));
    }

    [Fact]
    public void Json_KeepsTypes_Unicode_AndRenamesDuplicateColumns()
    {
        IReadOnlyList<ExportColumn> columns = [new("id"), new("id"), new("t"), new("n"), new("bin"), new("x")];

        var json = Text(new(ExportKind.Json), columns, [long.MaxValue, 9.50m, "é 😀 \"q\"\nx", null, new byte[] { 1 }, double.NaN]);

        using var doc = JsonDocument.Parse(json);
        var item = doc.RootElement[0];
        Assert.Equal(long.MaxValue, item.GetProperty("id").GetInt64());
        Assert.Equal(9.50m, item.GetProperty("id_2").GetDecimal());
        Assert.Equal("é 😀 \"q\"\nx", item.GetProperty("t").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("n").ValueKind);
        Assert.Equal("0x01", item.GetProperty("bin").GetString());
        Assert.Equal("NaN", item.GetProperty("x").GetString());
        Assert.Equal("[]", Text(new(ExportKind.Json), columns));
    }

    [Fact]
    public void Markdown_EscapesPipesAndLineBreaks_AndRightAlignsNumbers()
    {
        var markdown = Text(new(ExportKind.Markdown), [new("n", ClrType: typeof(int)), new("t|x")], [1, "a|b\r\nc<d"], [null, "\\"]);

        Assert.Equal("| n | t\\|x |\n| ---: | --- |\n| 1 | a\\|b<br>c&lt;d |\n|  | \\\\ |", markdown);
    }

    [Theory]
    [InlineData(DbKind.Postgres, "'O''Brien é'", "TRUE", "'\\x0AFF'")]
    [InlineData(DbKind.SqlServer, "N'O''Brien é'", "1", "0x0AFF")]
    [InlineData(DbKind.Sqlite, "'O''Brien é'", "1", "X'0AFF'")]
    public void SqlLiterals_FollowTheEngine(DbKind kind, string text, string yes, string binary)
    {
        Assert.Equal(text, SqlLiterals.Format(kind, "O'Brien é"));
        Assert.Equal(yes, SqlLiterals.Format(kind, true));
        Assert.Equal(binary, SqlLiterals.Format(kind, new byte[] { 0x0A, 0xFF }));
        Assert.Equal("NULL", SqlLiterals.Format(kind, null));
        Assert.Equal("NULL", SqlLiterals.Format(kind, DBNull.Value));
        Assert.Equal("-12.50", SqlLiterals.Format(kind, -12.50m));
        Assert.Equal("1E+300", SqlLiterals.Format(kind, 1e300));
    }

    [Fact]
    public void SqlLiterals_DatesIntervalsArraysAndSpecialNumbers()
    {
        var moment = new DateTime(2024, 3, 5, 9, 30, 0).AddTicks(1234567);

        Assert.Equal("'2024-03-05 09:30:00.1234567'", SqlLiterals.Format(DbKind.Postgres, moment));
        Assert.Equal("'2024-03-05T09:30:00.1234567'", SqlLiterals.Format(DbKind.SqlServer, moment));
        Assert.Equal("'2024-03-05T00:00:00.000'", SqlLiterals.Format(DbKind.SqlServer, new DateTime(2024, 3, 5)));
        Assert.Equal("'2024-03-05 00:00:00'", SqlLiterals.Format(DbKind.Sqlite, new DateTime(2024, 3, 5)));
        Assert.Equal("'2 days 03:04:05'", SqlLiterals.Format(DbKind.Postgres, new TimeSpan(2, 3, 4, 5)));
        Assert.Equal("'-1 days -00:00:00.5'", SqlLiterals.Format(DbKind.Postgres, -(TimeSpan.FromDays(1) + TimeSpan.FromMilliseconds(500))));
        Assert.Equal("ARRAY['a', NULL]", SqlLiterals.Format(DbKind.Postgres, new[] { "a", null }));
        Assert.Equal("'{}'", SqlLiterals.Format(DbKind.Postgres, Array.Empty<int>()));
        Assert.Equal("B'10'", SqlLiterals.Format(DbKind.Postgres, new BitArray(new[] { true, false })));
        Assert.Equal("'NaN'", SqlLiterals.Format(DbKind.Postgres, double.NaN));
        Assert.Equal("-9e999", SqlLiterals.Format(DbKind.Sqlite, double.NegativeInfinity));
        Assert.Equal("NULL", SqlLiterals.Format(DbKind.SqlServer, double.PositiveInfinity));
        Assert.Equal("N'a' + NCHAR(0) + N'b'", SqlLiterals.Format(DbKind.SqlServer, "a\0b"));
        Assert.Equal("CAST(X'610062' AS TEXT)", SqlLiterals.Format(DbKind.Sqlite, "a\0b"));
        Assert.Throws<ExportException>(() => SqlLiterals.Format(DbKind.Postgres, "a\0b"));
    }

    [Fact]
    public void SqlInsert_QuotesIdentifiersPerEngine()
    {
        var spec = new ExportSpec(ExportKind.SqlInsert) { Database = DbKind.SqlServer, Table = new TableRef("dbo", "my]table") };

        Assert.Equal("INSERT INTO [dbo].[my]]table] ([id], [name]) VALUES (1, NULL);", Text(spec, [new("id"), new("name")], [1, null]));
        Assert.StartsWith("INSERT INTO \"table_name\" (\"a\"\"b\")", Text(new(ExportKind.SqlInsert), [new("a\"b")], [1]));
    }

    [Fact]
    public void SqlUpdate_SetsTheOtherColumns_AndMatchesTheKey()
    {
        var spec = new ExportSpec(ExportKind.SqlUpdate) { Database = DbKind.Postgres, Table = new TableRef(null, "t"), KeyColumns = ["ID", "k"] };

        Assert.Equal("UPDATE \"t\" SET \"name\" = 'x' WHERE \"id\" = 1 AND \"k\" IS NULL;",
            Text(spec, [new("id"), new("name"), new("k")], [1, "x", null]));
        Assert.Contains("'k'", Assert.Throws<ExportException>(() => Text(spec, [new("id"), new("name")], [1, "x"])).Message);
        Assert.Throws<ExportException>(() => Text(spec, [new("id"), new("k")], [1, 2]));
        Assert.Throws<ExportException>(() => Text(spec with { KeyColumns = [] }, [new("id")], [1]));
    }

    [Fact]
    public void InList_SkipsNull_AndColumnNamesAreJoined()
    {
        var spec = new ExportSpec(ExportKind.InList) { Database = DbKind.Sqlite };

        Assert.Equal("IN ('a', 'b''c')", Text(spec, [new("n")], ["a"], [null], ["b'c"]));
        Assert.Equal("IN (NULL)", Text(spec, [new("n")], new object?[] { null }));
        Assert.Equal("id, name, note", Text(new(ExportKind.ColumnNames), Cols, [1, 2, 3]));
    }

    [Theory]
    [InlineData("orders", null, "orders")]
    [InlineData("dbo.orders", "dbo", "orders")]
    [InlineData("[my schema].[a.b]", "my schema", "a.b")]
    [InlineData("\"A\"\"B\"", null, "A\"B")]
    public void SqlName_ReadsTypedNames(string text, string? schema, string name) =>
        Assert.Equal(new TableRef(schema, name), SqlName.Parse(text));

    [Fact]
    public void Preferences_FromAHandEditedFile_FallBackToDefaults()
    {
        var broken = new ExportPreferences { Copy = null!, Format = (ExportKind)99, CsvDelimiter = "", NullText = null!, Encoding = (ExportEncoding)42 };

        Assert.Equal(new ExportPreferences(), broken.Normalized());
    }
}
