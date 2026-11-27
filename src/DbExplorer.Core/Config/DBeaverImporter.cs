using System.Security.Cryptography;
using System.Text.Json;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Config;

/// <param name="ReadOnlyImported">One line per imported connection that was made read-only, with the reason
/// (<c>"Prod DB (DBeaver connection type 'prod')"</c>); null/empty when there were none.</param>
public record ImportResult(int Added, int Skipped, IReadOnlyList<string> Warnings, IReadOnlyList<string>? ReadOnlyImported = null);

/// <summary>Imports folders, connections, credentials and virtual keys from a DBeaver workspace.</summary>
public static partial class DBeaverImporter
{
    // DBeaver's well-known, fixed key for credentials-config.json (AES-128-CBC, IV = first 16 bytes).
    private static readonly byte[] CredentialsKey = Convert.FromHexString("babb4a9f774ab853c96c2d653dfe544a");

    /// <summary>Default DBeaver config directory: %APPDATA%\DBeaverData\workspace6\General\.dbeaver.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DBeaverData", "workspace6", "General", ".dbeaver");

    /// <summary>
    /// Merges the DBeaver workspace into <paramref name="target"/> (mutated in place).
    /// Connections whose name already exists (case-insensitive) are skipped.
    /// </summary>
    public static ImportResult Import(string dbeaverDir, AppConfig target, ISecretProtector protector)
    {
        var warnings = new List<string>();
        var sourcesPath = Path.Combine(dbeaverDir, "data-sources.json");
        if (!File.Exists(sourcesPath))
        {
            warnings.Add($"data-sources.json not found in {dbeaverDir}");
            return new ImportResult(0, 0, warnings);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(sourcesPath));
        var root = doc.RootElement;
        var credentials = LoadCredentials(dbeaverDir, warnings);
        var connectionTypes = root.TryGetProperty("connection-types", out var types) ? types : default;

        if (root.TryGetProperty("folders", out var folders) && folders.ValueKind == JsonValueKind.Object)
            foreach (var folder in folders.EnumerateObject())
                if (!target.Folders.Contains(folder.Name))
                    target.Folders.Add(folder.Name);

        var added = 0;
        var skipped = 0;
        var readOnlyNotes = new List<string>();
        var idMap = new Dictionary<string, ConnectionConfig>();

        if (root.TryGetProperty("connections", out var connections) && connections.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in connections.EnumerateObject())
            {
                var name = Str(entry.Value, "name") ?? entry.Name;
                if (target.Connections.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }

                var connection = ToConnection(entry.Name, name, entry.Value, credentials, protector, warnings);
                if (connection is null) continue;

                connection = connection with { Environment = EnvironmentOf(entry.Value, connectionTypes) };
                connection = WithAuthentication(connection, entry.Value);
                connection = WithSshTunnel(connection, entry.Name, entry.Value, credentials, protector, warnings);

                if (ReadOnlyReason(entry.Value, connectionTypes) is { } reason)
                {
                    connection = connection with { ReadOnly = true };
                    readOnlyNotes.Add($"{name} ({reason})");
                }

                target.Connections.Add(connection);
                idMap[entry.Name] = connection;
                added++;
            }
        }

        if (root.TryGetProperty("virtual-models", out var models) && models.ValueKind == JsonValueKind.Object)
            ImportVirtualKeys(models, idMap, target, warnings);

        return new ImportResult(added, skipped, warnings, readOnlyNotes);
    }

    /// <summary>
    /// Why an imported connection must not be editable, or null. Conservative on purpose: an imported production database
    /// must never silently become writable. DBeaver stores <c>"read-only": true</c> on the connection object (also looked up
    /// in its <c>configuration</c>) and the connection type id in <c>configuration.type</c> (<c>dev</c>, <c>test</c>,
    /// <c>prod</c> or a custom id; the custom types are described under <c>connection-types</c>). A type called prod /
    /// production (id or name, any case), or one whose <c>security.permission-restrictions</c> forbid data edits, counts.
    /// </summary>
    private static string? ReadOnlyReason(JsonElement node, JsonElement connectionTypes)
    {
        var cfg = node.TryGetProperty("configuration", out var c) ? c : default;
        if (IsTrue(node, "read-only") || IsTrue(cfg, "read-only")) return "read-only in DBeaver";

        var typeId = Str(cfg, "type");
        if (typeId is null) return null;
        if (IsProdName(typeId)) return $"DBeaver connection type '{typeId}'";

        if (connectionTypes.ValueKind == JsonValueKind.Object && connectionTypes.TryGetProperty(typeId, out var type))
        {
            // A custom type's display name ("Production EU", "PROD-replica"): anything mentioning prod is treated as production.
            if (Str(type, "name") is { } typeName && typeName.Contains("prod", StringComparison.OrdinalIgnoreCase))
                return $"DBeaver connection type '{typeName}'";
            if (type.ValueKind == JsonValueKind.Object
                && type.TryGetProperty("security", out var security) && security.ValueKind == JsonValueKind.Object
                && security.TryGetProperty("permission-restrictions", out var restrictions) && restrictions.ValueKind == JsonValueKind.Array
                && restrictions.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String
                                                          && r.GetString()!.Contains("EDIT", StringComparison.OrdinalIgnoreCase)))
                return $"DBeaver connection type '{typeId}' forbids data edits";
        }
        return null;
    }

    /// <summary>
    /// The environment named by the connection type (its display name for a custom type, else its id). DBeaver gives
    /// <c>dev</c> to a connection nobody classified, so that is what it is here too. An unknown name gives none.
    /// </summary>
    private static DbEnvironment EnvironmentOf(JsonElement node, JsonElement connectionTypes)
    {
        var cfg = node.TryGetProperty("configuration", out var c) ? c : default;
        if (Str(cfg, "type") is not { } typeId) return DbEnvironment.None;
        var name = connectionTypes.ValueKind == JsonValueKind.Object && connectionTypes.TryGetProperty(typeId, out var type)
            ? Str(type, "name") ?? typeId
            : typeId;
        var byName = Classify(name);
        return byName != DbEnvironment.None ? byName : Classify(typeId);

        static DbEnvironment Classify(string s)
        {
            bool Has(string part) => s.Contains(part, StringComparison.OrdinalIgnoreCase);
            if (Has("preprod") || Has("pre-prod") || Has("pre prod") || Has("staging")) return DbEnvironment.PreProduction;
            if (Has("prod")) return DbEnvironment.Production;
            if (Has("test") || s.Trim().Equals("qa", StringComparison.OrdinalIgnoreCase)) return DbEnvironment.Test;
            return Has("dev") ? DbEnvironment.Development : DbEnvironment.None;
        }
    }

    private static bool IsProdName(string s) =>
        s.Trim().Equals("prod", StringComparison.OrdinalIgnoreCase) || s.Trim().Equals("production", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrue(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var v)
        && (v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

    private static ConnectionConfig? ToConnection(
        string dbeaverId, string name, JsonElement node,
        Dictionary<string, (string? User, string? Password)> credentials,
        ISecretProtector protector, List<string> warnings)
    {
        var provider = Str(node, "provider");
        DbKind? kind = provider switch
        {
            "postgresql" => DbKind.Postgres,
            "sqlserver" => DbKind.SqlServer,
            "sqlite" => DbKind.Sqlite,
            _ => null
        };
        if (kind is null)
        {
            warnings.Add($"Connection '{name}' skipped: unsupported provider '{provider}'");
            return null;
        }

        var cfg = node.TryGetProperty("configuration", out var c) ? c : default;
        var database = Str(cfg, "database") ?? "";
        string? host = null;
        int? port = null;
        var trust = false;

        if (kind != DbKind.Sqlite)
        {
            host = Str(cfg, "host");
            port = int.TryParse(Str(cfg, "port"), out var p) ? p : null;
            if (cfg.ValueKind == JsonValueKind.Object
                && cfg.TryGetProperty("provider-properties", out var props)
                && props.ValueKind == JsonValueKind.Object)
                trust = string.Equals(Str(props, "sslTrustServerCertificate"), "true", StringComparison.OrdinalIgnoreCase);
        }

        string? user = null;
        string? protectedPassword = null;
        if (credentials.TryGetValue(dbeaverId, out var cred))
        {
            user = cred.User;
            if (!string.IsNullOrEmpty(cred.Password)) protectedPassword = protector.Protect(cred.Password);
        }

        // DBeaver does not store an "encrypt" setting; keep the JDBC default (no forced encryption).
        return new ConnectionConfig(
            Guid.NewGuid(), name, kind.Value, Str(node, "folder"), host, port, database,
            user, protectedPassword, Encrypt: false, TrustServerCertificate: trust, ReadOnly: false);
    }

    private static Dictionary<string, (string? User, string? Password)> LoadCredentials(string dir, List<string> warnings)
    {
        var result = new Dictionary<string, (string?, string?)>();
        var path = Path.Combine(dir, "credentials-config.json");
        if (!File.Exists(path))
        {
            warnings.Add("credentials-config.json not found: connections imported without credentials");
            return result;
        }

        try
        {
            var data = File.ReadAllBytes(path);
            using var aes = Aes.Create();
            aes.KeySize = 128;
            aes.Key = CredentialsKey;
            var plain = aes.DecryptCbc(data.AsSpan(16), data.AsSpan(0, 16), PaddingMode.PKCS7);
            using var doc = JsonDocument.Parse(plain);
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.TryGetProperty("#connection", out var conn))
                    result[entry.Name] = (Str(conn, "user"), Str(conn, "password"));
                if (entry.Value.TryGetProperty("network/" + SshHandler, out var ssh))
                    result[SshCredentialsKey(entry.Name)] = (Str(ssh, "user"), Str(ssh, "password"));
            }
        }
        catch (Exception e) when (e is CryptographicException or JsonException or ArgumentException or InvalidOperationException)
        {
            result.Clear();
            warnings.Add($"credentials-config.json could not be read ({e.Message}): connections imported without credentials");
        }

        return result;
    }

    /// <summary>
    /// DBeaver nests virtual keys as catalog > schema > ":TABLE" > constraints. The catalog is dropped,
    /// so the key is "schema.table" (or "table" when there is no schema level). "Use all columns" becomes ["*"].
    /// </summary>
    private static void ImportVirtualKeys(
        JsonElement models, Dictionary<string, ConnectionConfig> idMap, AppConfig target, List<string> warnings)
    {
        foreach (var model in models.EnumerateObject())
        {
            if (!idMap.TryGetValue(model.Name, out var connection)) continue;
            Walk(model.Value, new List<string>());

            void Walk(JsonElement node, List<string> path)
            {
                if (node.ValueKind != JsonValueKind.Object) return;
                foreach (var child in node.EnumerateObject())
                {
                    if (!child.Name.StartsWith(':'))
                    {
                        Walk(child.Value, [.. path, child.Name]);
                        continue;
                    }

                    var table = child.Name[1..];
                    var key = path.Count >= 2 ? $"{path[^1]}.{table}" : table;
                    if (!UsesAllColumns(child.Value))
                    {
                        warnings.Add($"Virtual key on '{key}' of '{connection.Name}' skipped: only 'use all columns' keys are supported");
                        continue;
                    }

                    if (!target.VirtualKeys.TryGetValue(connection.Id, out var keys))
                        target.VirtualKeys[connection.Id] = keys = new();
                    keys[key] = ["*"];
                }
            }
        }
    }

    private static bool UsesAllColumns(JsonElement table) =>
        table.ValueKind == JsonValueKind.Object
        && table.TryGetProperty("constraints", out var constraints)
        && constraints.ValueKind == JsonValueKind.Object
        && constraints.EnumerateObject().Any(c =>
            c.Value.ValueKind == JsonValueKind.Object
            && c.Value.TryGetProperty("useAllColumns", out var all)
            && all.ValueKind == JsonValueKind.True);

    private static string? Str(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
