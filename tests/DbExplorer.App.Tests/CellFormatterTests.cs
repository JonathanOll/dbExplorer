using System.Globalization;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Querying;

namespace DbExplorer.App.Tests;

public class CellFormatterTests
{
    [Fact]
    public void Null_IsFlagged()
    {
        var cell = CellFormatter.Format(null);

        Assert.True(cell.IsNull);
        Assert.Equal("NULL", cell.Text);
        Assert.Null(cell.Tooltip);
    }

    [Fact]
    public void ShortBinary_ShowsAllBytesAsHex()
    {
        var cell = CellFormatter.Format(new byte[] { 0x01, 0xAB, 0xFF });

        Assert.Equal("0x01ABFF", cell.Text);
        Assert.Null(cell.Tooltip);
    }

    [Fact]
    public void LongBinary_ShowsFirst32Bytes_AndLength()
    {
        var bytes = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();

        var cell = CellFormatter.Format(bytes);

        Assert.Equal("0x" + Convert.ToHexString(bytes, 0, 32) + "…", cell.Text);
        Assert.Equal("100 bytes", cell.Tooltip);
        Assert.True(cell.IsBinary);
    }

    [Fact]
    public void LongText_IsTruncated_FullValueInTooltip()
    {
        var text = new string('x', 500);

        var cell = CellFormatter.Format(text);

        Assert.Equal(CellFormatter.MaxCellChars + 1, cell.Text.Length);
        Assert.EndsWith("…", cell.Text);
        Assert.Equal(text, cell.Tooltip);
    }

    [Fact]
    public void HugeText_TooltipIsCapped()
    {
        var cell = CellFormatter.Format(new string('x', CellFormatter.MaxTooltipChars + 5000));

        Assert.Equal(CellFormatter.MaxTooltipChars + 1, cell.Tooltip!.Length);
        Assert.EndsWith("…", cell.Tooltip);
    }

    [Fact]
    public void MultilineText_ShowsFirstLine_FullValueInTooltip()
    {
        var cell = CellFormatter.Format("first line\r\nsecond line");

        Assert.Equal("first line …", cell.Text);
        Assert.Equal("first line\r\nsecond line", cell.Tooltip);
    }

    [Fact]
    public void ShortText_IsUnchanged_NoTooltip()
    {
        var cell = CellFormatter.Format("hello");

        Assert.Equal("hello", cell.Text);
        Assert.Null(cell.Tooltip);
        Assert.False(cell.IsNull);
    }

    [Fact]
    public void Booleans_AreInvariant()
    {
        Assert.Equal("true", CellFormatter.Format(true).Text);
        Assert.Equal("false", CellFormatter.Format(false).Text);
        Assert.Equal("1", CellFormatter.Format(1L).Text);   // SQLite: BOOLEAN comes back as long
    }

    [Fact]
    public void Dates_AreInvariantAndIsoLike_RegardlessOfCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

            Assert.Equal("2024-01-05 10:00:00", CellFormatter.Format(new DateTime(2024, 1, 5, 10, 0, 0)).Text);
            Assert.Equal("2024-01-05 10:00:00.5", CellFormatter.Format(new DateTime(2024, 1, 5, 10, 0, 0, 500)).Text);
            Assert.Equal("2024-01-05 10:00:00 +02:00",
                CellFormatter.Format(new DateTimeOffset(2024, 1, 5, 10, 0, 0, TimeSpan.FromHours(2))).Text);
            Assert.Equal("2024-01-05", CellFormatter.Format(new DateOnly(2024, 1, 5)).Text);
            Assert.Equal("10:30:00", CellFormatter.Format(new TimeSpan(10, 30, 0)).Text);
            Assert.Equal("1234.5", CellFormatter.Format(1234.5m).Text);
            Assert.Equal("0.1", CellFormatter.Format(0.1).Text);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void StringDateFromSqlite_IsShownAsIs()
    {
        Assert.Equal("2024-01-05 10:00:00", CellFormatter.Format("2024-01-05 10:00:00").Text);
    }

    [Fact]
    public void Numbers_AreRightAligned()
    {
        Assert.True(CellFormatter.Format(5L).IsNumber);
        Assert.True(CellFormatter.Format(5.5).IsNumber);
        Assert.False(CellFormatter.Format("5").IsNumber);
    }

    [Fact]
    public void Guid_IsLowercaseHyphenated()
    {
        var g = Guid.Parse("AABBCCDD-0000-0000-0000-000000000001");
        Assert.Equal("aabbccdd-0000-0000-0000-000000000001", CellFormatter.Format(g).Text);
    }

    [Fact]
    public void FilterText_IsEqualsPlusInvariantValue()
    {
        Assert.Equal("=5", CellFormatter.FilterText(5L));
        Assert.Equal("=1.5", CellFormatter.FilterText(1.5));
        Assert.Equal("=true", CellFormatter.FilterText(true));
        Assert.Equal("=2024-01-05 10:00:00", CellFormatter.FilterText("2024-01-05 10:00:00"));
        Assert.Equal("=null", CellFormatter.FilterText("null"));   // the string "null" is a value, not the NULL filter
        Assert.Equal("null", CellFormatter.FilterText(null));
    }

    [Fact]
    public void FilterText_Binary_Throws()
    {
        Assert.Throws<FilterException>(() => CellFormatter.FilterText(new byte[] { 1 }, "payload"));
    }

    [Fact]
    public void FilterText_RoundTripsThroughTheParser()
    {
        var expr = FilterParser.Parse(CellFormatter.FilterText("null"));
        Assert.Equal(FilterOp.Eq, expr!.Op);
        Assert.Equal("null", expr.Values[0]);
    }
}
