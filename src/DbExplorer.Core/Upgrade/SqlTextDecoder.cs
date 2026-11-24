using System.Text;

namespace DbExplorer.Core.Upgrade;

/// <summary>
/// Decodes SQL script files without corrupting them: BOM (UTF-8/16/32, stripped), else strict UTF-8, else Windows-1252.
/// </summary>
public static class SqlTextDecoder
{
    static SqlTextDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Decodes the file bytes; <c>Encoding</c> is the one to write the file back with.</summary>
    public static (string Text, Encoding Encoding) Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
            return Strip(bytes, new UTF32Encoding(false, true, true));
        if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
            return Strip(bytes, new UTF32Encoding(true, true, true));
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return Strip(bytes, new UTF8Encoding(true, true));
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            return Strip(bytes, new UnicodeEncoding(false, true, true));
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            return Strip(bytes, new UnicodeEncoding(true, true, true));

        try
        {
            var utf8 = new UTF8Encoding(false, true);
            return (utf8.GetString(bytes), utf8);
        }
        catch (DecoderFallbackException)
        {
            var ansi = Windows1252();
            return (ansi.GetString(bytes), ansi);
        }
    }

    private static Encoding Windows1252() =>
        Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);

    private static (string, Encoding) Strip(byte[] bytes, Encoding encoding)
    {
        var skip = encoding.GetPreamble().Length;
        return (encoding.GetString(bytes, skip, bytes.Length - skip), encoding);
    }
}
