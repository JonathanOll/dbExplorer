using System.Text.Json;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Config;

// SSL settings of an imported PostgreSQL connection.
public static partial class DBeaverImporter
{
    private const string TlsHandler = "postgre_ssl";

    private static ConnectionConfig WithTls(
        ConnectionConfig connection, string dbeaverId, JsonElement node,
        Dictionary<string, (string? User, string? Password)> credentials, ISecretProtector protector, List<string> warnings)
    {
        if (connection.Kind != DbKind.Postgres) return connection;
        var cfg = node.TryGetProperty("configuration", out var c) ? c : default;
        var handler = Child(Child(cfg, "handlers"), TlsHandler);
        var enabled = IsTrue(handler, "enabled");
        var properties = Child(handler, "properties");

        // The first of these handler properties that is filled; DBeaver renamed them and workspaces still hold the old names.
        string? Setting(params string[] names) => !enabled
            ? null
            : names.Select(n => Str(properties, n)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var text = Setting("sslMode")
                   ?? Str(Child(cfg, "provider-properties"), "sslmode") ?? Str(Child(cfg, "properties"), "sslmode");
        TlsMode? known = text?.Replace("-", "").ToLowerInvariant() switch
        {
            "disable" => TlsMode.Disable,
            "allow" => TlsMode.Allow,
            "prefer" => TlsMode.Prefer,
            "require" => TlsMode.Require,
            "verifyca" => TlsMode.VerifyCA,
            "verifyfull" => TlsMode.VerifyFull,
            _ => null,
        };
        if (known is null && !string.IsNullOrWhiteSpace(text))
            warnings.Add($"Connection '{connection.Name}': unknown SSL mode '{text}'; check the TLS settings");
        // DBeaver's JDBC driver checks everything when SSL is on and no mode is chosen.
        if ((known ?? (enabled ? TlsMode.VerifyFull : null)) is not { } mode) return connection;

        var root = Setting("ssl.ca.cert", "rootCert", "sslRootCert");
        var certificate = Setting("ssl.client.cert", "clientCert", "sslClientCert");
        var key = certificate is null ? null : Setting("ssl.client.key", "clientKey", "sslClientKey");
        if (Setting("ssl.keystore") is not null)
            warnings.Add($"Connection '{connection.Name}': the Java keystore is not imported; choose certificate files in the connection");
        if (key is not null && Path.GetExtension(key).ToLowerInvariant() is ".pk8" or ".der")
            warnings.Add($"Connection '{connection.Name}': the client key must be a PEM file; convert '{key}' and choose it in the connection");

        // Prefer without a certificate is what a connection without TLS settings does.
        if (mode == TlsMode.Prefer && certificate is null) return connection;

        credentials.TryGetValue(HandlerCredentialsKey(dbeaverId, TlsHandler), out var secret);
        var passphrase = certificate is null || string.IsNullOrEmpty(secret.Password) ? null : protector.Protect(secret.Password);
        return connection with
        {
            Tls = new TlsConfig(mode, TlsConfig.Verifies(mode) ? root : null, certificate, key, passphrase),
        };
    }
}
