using System.Security.Authentication;
using System.Security.Cryptography;
using DbExplorer.Core.Model;
using Npgsql;

namespace DbExplorer.Core.Drivers;

// SSL mode and certificates, and what their failures mean.
public sealed partial class PostgresDriver
{
    private void ApplyTls(NpgsqlConnectionStringBuilder builder, TlsConfig tls)
    {
        builder.SslMode = tls.Mode switch
        {
            TlsMode.Disable => SslMode.Disable,
            TlsMode.Allow => SslMode.Allow,
            TlsMode.Require => SslMode.Require,
            TlsMode.VerifyCA => SslMode.VerifyCA,
            TlsMode.VerifyFull => SslMode.VerifyFull,
            _ => SslMode.Prefer,
        };
        // GSS encryption is tried before TLS and would replace it, certificate check included.
        if (tls.Mode is TlsMode.Require or TlsMode.VerifyCA or TlsMode.VerifyFull)
            builder.GssEncryptionMode = GssEncryptionMode.Disable;
        if (TlsConfig.Verifies(tls.Mode)) builder.RootCertificate = FilePath(tls.RootCertificate);
        if (tls.Mode == TlsMode.Disable) return;

        builder.SslCertificate = FilePath(tls.ClientCertificate);
        if (builder.SslCertificate is null) return;
        builder.SslKey = FilePath(tls.ClientKey);
        if (string.IsNullOrEmpty(tls.ProtectedKeyPassphrase)) return;
        if (secrets is null) throw new InvalidOperationException("The passphrase of the client key cannot be decrypted here.");
        builder.SslPassword = secrets.Unprotect(tls.ProtectedKeyPassphrase);
    }

    private static string? FilePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Environment.ExpandEnvironmentVariables(path.Trim());

    public string? DescribeTlsFailure(ConnectionConfig cfg, Exception e)
    {
        var mode = cfg.Tls?.Mode ?? TlsMode.Prefer;
        var root = e.GetBaseException();
        var cause = root.Message.Trim();

        if (e is NpgsqlException && e.Message.StartsWith("SSL connection requested", StringComparison.Ordinal))
            return mode == TlsMode.Allow
                ? "the server refused the connection without TLS, and it does not offer TLS."
                : $"the server does not offer TLS, and SSL mode {mode} requires it.";

        // Npgsql wraps a failed handshake; a client certificate it cannot load is thrown as it is.
        var handshake = e is NpgsqlException && e.Message.Contains("SSL handshake", StringComparison.OrdinalIgnoreCase);
        var clientFiles = cfg.Tls?.ClientCertificate is not null && mode != TlsMode.Disable
            && e is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException;
        if (!handshake && !clientFiles && e is not CryptographicException) return null;

        if (MissingFile(cfg.Tls, mode) is { } missing) return missing;
        if (!handshake)
            return $"cannot read the client certificate or its key. Check both files and the passphrase. ({cause})";

        if (root is AuthenticationException && TlsConfig.Verifies(mode))
        {
            var issuer = string.IsNullOrWhiteSpace(cfg.Tls!.RootCertificate)
                ? "an authority this computer trusts"
                : $"the authority in {cfg.Tls.RootCertificate}";
            var name = mode == TlsMode.VerifyFull ? $" and issued for the host name '{NetworkTarget(cfg).Host}'" : "";
            return $"the server certificate was refused. SSL mode {mode} needs a valid certificate signed by {issuer}{name}. ({cause})";
        }

        if (root is CryptographicException && TlsConfig.Verifies(mode)) return $"cannot read the root CA file. ({cause})";
        return $"the TLS handshake with the server failed. ({cause})";
    }

    private static string? MissingFile(TlsConfig? tls, TlsMode mode)
    {
        if (tls is null || mode == TlsMode.Disable) return null;
        (string What, string? Path)[] files =
        [
            ("root CA file", TlsConfig.Verifies(mode) ? tls.RootCertificate : null),
            ("client certificate file", tls.ClientCertificate),
            ("client key file", tls.ClientCertificate is null ? null : tls.ClientKey),
        ];
        foreach (var (what, path) in files)
            if (FilePath(path) is { } full && !File.Exists(full))
                return $"the {what} was not found: {full}";
        return null;
    }
}
