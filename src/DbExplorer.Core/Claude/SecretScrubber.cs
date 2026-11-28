using DbExplorer.Core.Config;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Claude;

/// <summary>
/// Last line of defence: removes every stored secret, plain or protected, from a text about to leave the process (a
/// provider's error message, a log line, a result row that happens to hold one).
/// </summary>
public static class SecretScrubber
{
    public const string Mask = "***";

    // Shorter secrets would mask ordinary words, which would itself give them away.
    private const int MinLength = 4;

    public static string Scrub(string text, AppConfig config, ISecretProtector protector)
    {
        foreach (var secret in Secrets(config, protector).OrderByDescending(s => s.Length))
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
            // As it appears inside a JSON string.
            var escaped = System.Text.Json.JsonEncodedText.Encode(secret, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();
            if (escaped != secret) text = text.Replace(escaped, Mask, StringComparison.Ordinal);
        }
        return text;
    }

    private static IEnumerable<string> Secrets(AppConfig config, ISecretProtector protector)
    {
        foreach (var c in config.Connections)
        foreach (var stored in new[] { c.ProtectedPassword, c.Ssh?.ProtectedPassword, c.Ssh?.ProtectedPassphrase, c.Tls?.ProtectedKeyPassphrase })
        {
            if (string.IsNullOrEmpty(stored)) continue;
            yield return stored;
            string? plain = null;
            try { plain = protector.Unprotect(stored); }
            catch (Exception) { /* written by another Windows account: it cannot be used either */ }
            if (plain is { Length: >= MinLength }) yield return plain;
        }
    }
}
