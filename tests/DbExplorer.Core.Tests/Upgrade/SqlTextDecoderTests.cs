using System.Text;
using DbExplorer.Core.Upgrade;

namespace DbExplorer.Core.Tests.Upgrade;

public class SqlTextDecoderTests
{
    [Fact]
    public void Utf8WithBom_HasNoBomInText()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("SELECT 'é'")];
        var (text, _) = SqlTextDecoder.Decode(bytes);
        Assert.Equal("SELECT 'é'", text);
        Assert.DoesNotContain('﻿', text);
    }

    [Fact]
    public void Utf8WithoutBom_Decodes()
    {
        var (text, enc) = SqlTextDecoder.Decode(Encoding.UTF8.GetBytes("SELECT 'é€'"));
        Assert.Equal("SELECT 'é€'", text);
        Assert.Empty(enc.GetPreamble());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf16Bom_IsStripped(bool bigEndian)
    {
        var enc = new UnicodeEncoding(bigEndian, true);
        byte[] bytes = [.. enc.GetPreamble(), .. enc.GetBytes("SELECT 'é'")];
        var (text, _) = SqlTextDecoder.Decode(bytes);
        Assert.Equal("SELECT 'é'", text);
    }

    [Fact]
    public void Windows1252_FallbackWhenNotUtf8()
    {
        var (text, enc) = SqlTextDecoder.Decode([0x53, 0xE9]); // "Sé" in cp1252, invalid UTF-8
        Assert.Equal("Sé", text);
        Assert.Equal(1252, enc.CodePage);
    }

    [Fact]
    public void PureAscii_Decodes()
    {
        var (text, _) = SqlTextDecoder.Decode(Encoding.ASCII.GetBytes("SELECT 1"));
        Assert.Equal("SELECT 1", text);
    }
}
