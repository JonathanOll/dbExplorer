using System.Text;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// Reads and writes script files without corrupting them. Opening detects the encoding (BOM, else strict UTF-8, else
/// Windows-1252); saving writes back in the encoding that was detected, BOM included. New files are UTF-8 without BOM.
/// </summary>
internal static class ScriptFileEncoding
{
    public static readonly Encoding NewFile = new UTF8Encoding(false);

    static ScriptFileEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

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

    /// <summary>
    /// Encodes the text with <paramref name="encoding"/> (null = new file), preamble included. When the text holds
    /// characters the encoding cannot represent it is written as UTF-8 instead (never as '?'), and
    /// <c>Changed</c> is true.
    /// </summary>
    public static (byte[] Bytes, Encoding Used, bool Changed) Encode(string text, Encoding? encoding)
    {
        encoding ??= NewFile;
        try
        {
            var strict = Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var body = strict.GetBytes(text);
            var preamble = encoding.GetPreamble();
            return ([.. preamble, .. body], encoding, false);
        }
        catch (EncoderFallbackException)
        {
            return (NewFile.GetBytes(text), NewFile, true);
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
