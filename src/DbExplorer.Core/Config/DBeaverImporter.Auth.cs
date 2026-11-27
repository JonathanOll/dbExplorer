using System.Text.Json;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Config;

// Authentication mode and SSH tunnel of an imported connection.
public static partial class DBeaverImporter
{
    private const string SshHandler = "ssh_tunnel";

    /// <summary>Key of the credentials of a connection's network handler in the credentials dictionary.</summary>
    private static string HandlerCredentialsKey(string dbeaverId, string handler) => dbeaverId + "\n" + handler;

    private static ConnectionConfig WithAuthentication(ConnectionConfig connection, JsonElement node)
    {
        if (connection.Kind == DbKind.Sqlite) return connection;
        var cfg = node.TryGetProperty("configuration", out var c) ? c : default;
        var mode = AuthModeOf(connection.Kind, cfg);
        if (mode == AuthMode.Password) return connection;
        return connection with
        {
            Auth = mode,
            User = AuthModes.UsesUser(connection.Kind, mode) ? connection.User : null,
            ProtectedPassword = AuthModes.UsesPassword(mode) ? connection.ProtectedPassword : null,
            Encrypt = connection.Encrypt || AuthModes.IsEntra(mode),
        };
    }

    private static AuthMode AuthModeOf(DbKind kind, JsonElement cfg)
    {
        var model = Str(cfg, "auth-model") ?? "";
        bool Model(string part) => model.Contains(part, StringComparison.OrdinalIgnoreCase);

        // JDBC driver properties, which DBeaver keeps in either of these objects.
        string? Property(string name) =>
            Str(Child(cfg, "provider-properties"), name) ?? Str(Child(cfg, "properties"), name);

        if (kind == DbKind.SqlServer)
        {
            var entra = Property("authentication")?.Replace(" ", "").ToLowerInvariant() switch
            {
                "activedirectoryinteractive" => AuthMode.EntraInteractive,
                "activedirectorypassword" => AuthMode.EntraPassword,
                "activedirectoryintegrated" => AuthMode.EntraIntegrated,
                "activedirectoryserviceprincipal" => AuthMode.EntraServicePrincipal,
                "activedirectorydefault" or "activedirectorymsi" or "activedirectorymanagedidentity" => AuthMode.EntraDefault,
                _ => (AuthMode?)null,
            };
            if (entra is { } mode) return mode;
            if (IsTrue(Child(cfg, "provider-properties"), "integratedSecurity") || IsTrue(Child(cfg, "properties"), "integratedSecurity"))
                return AuthMode.Integrated;
        }

        return Model("windows") || Model("kerberos") || Model("sspi") || Model("ntlm") ? AuthMode.Integrated : AuthMode.Password;
    }

    private static ConnectionConfig WithSshTunnel(
        ConnectionConfig connection, string dbeaverId, JsonElement node,
        Dictionary<string, (string? User, string? Password)> credentials, ISecretProtector protector, List<string> warnings)
    {
        if (connection.Kind == DbKind.Sqlite) return connection;
        var handler = Child(Child(node.TryGetProperty("configuration", out var c) ? c : default, "handlers"), SshHandler);
        if (!IsTrue(handler, "enabled")) return connection;

        var properties = Child(handler, "properties");
        credentials.TryGetValue(HandlerCredentialsKey(dbeaverId, SshHandler), out var secret);
        var host = Str(properties, "host") ?? "";
        var port = Number(properties, "port") ?? SshTunnelConfig.DefaultPort;
        var user = secret.User ?? Str(handler, "user") ?? Str(properties, "user") ?? "";
        var authType = Str(properties, "authType") ?? "PASSWORD";
        var password = string.IsNullOrEmpty(secret.Password) ? null : protector.Protect(secret.Password);

        // The tunnel is kept even when incomplete: connecting directly instead could reach another server.
        if (authType.Equals("PASSWORD", StringComparison.OrdinalIgnoreCase))
            return connection with { Ssh = new SshTunnelConfig(host, port, user, SshAuthKind.Password, ProtectedPassword: password) };

        var keyFile = Str(properties, "keyPath");
        if (!authType.Equals("PUBLIC_KEY", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(keyFile))
            warnings.Add($"Connection '{connection.Name}': SSH '{authType}' sign-in without a key file is not supported; choose one in the connection");
        return connection with
        {
            Ssh = new SshTunnelConfig(host, port, user, SshAuthKind.PrivateKey, KeyFile: keyFile, ProtectedPassphrase: password),
        };
    }

    private static JsonElement Child(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var v) ? v : default;

    private static int? Number(JsonElement node, string name)
    {
        var value = Child(node, name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return int.TryParse(Str(node, name), out var parsed) ? parsed : null;
    }
}
