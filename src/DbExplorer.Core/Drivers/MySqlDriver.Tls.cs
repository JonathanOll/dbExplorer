using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DbExplorer.Core.Model;
using MySqlConnector;

namespace DbExplorer.Core.Drivers;

// SSL mode and certificates, and what their failures mean.
public sealed partial class MySqlDriver
{
    private void ApplyTls(MySqlConnectionStringBuilder builder, TlsConfig tls)
    {
        builder.SslMode = tls.Mode switch
        {
            TlsMode.Disable => MySqlSslMode.Disabled,
            TlsMode.Require => MySqlSslMode.Required,
            TlsMode.VerifyCA => MySqlSslMode.VerifyCA,
            TlsMode.VerifyFull => MySqlSslMode.VerifyFull,
            _ => MySqlSslMode.Preferred,   // MySQL has no "allow"
        };
        if (TlsConfig.Verifies(tls.Mode) && FilePath(tls.RootCertificate) is { } root) builder.SslCa = root;
        if (tls.Mode == TlsMode.Disable || FilePath(tls.ClientCertificate) is not { } certificate) return;

        var passphrase = Passphrase(tls);
        if (IsPkcs12(certificate))
        {
            builder.CertificateFile = certificate;
            if (passphrase is not null) builder.CertificatePassword = passphrase;
            return;
        }
        if (passphrase is not null)
            throw new NotSupportedException(
                "MySQL connections cannot use an encrypted PEM client key: use a .pfx file (with its passphrase) or an unencrypted key.");
        builder.SslCert = certificate;
        builder.SslKey = FilePath(tls.ClientKey) ?? certificate;
    }

    private string? Passphrase(TlsConfig tls)
    {
        if (string.IsNullOrEmpty(tls.ProtectedKeyPassphrase)) return null;
        if (secrets is null) throw new InvalidOperationException("The passphrase of the client key cannot be decrypted here.");
        return secrets.Unprotect(tls.ProtectedKeyPassphrase);
    }

    private static bool IsPkcs12(string path) =>
        Path.GetExtension(path).Equals(".pfx", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".p12", StringComparison.OrdinalIgnoreCase);

    private static string? FilePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Environment.ExpandEnvironmentVariables(path.Trim());

    private static MySqlConnection Connect(ConnectionConfig cfg, MySqlConnectionStringBuilder builder, string? tunneledHost = null)
    {
        // Through a tunnel the connector would check the certificate against 127.0.0.1: the check is done here instead,
        // against the real server name.
        if (tunneledHost is null || cfg.Tls is not { Mode: TlsMode.VerifyFull } tls) return new MySqlConnection(builder.ConnectionString);

        var root = FilePath(tls.RootCertificate);
        builder.SslMode = MySqlSslMode.Required;
        builder.SslCa = "";
        // Pooled connections are keyed on the connection string, which must then differ per verified name and authority.
        builder.ApplicationName = $"dbExplorer verify {tunneledHost} {root}";
        return new MySqlConnection(builder.ConnectionString)
        {
            RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                Verify(certificate, chain, errors, tunneledHost, root),
        };
    }

    /// <summary>The server certificate chains to a trusted authority (the root CA file when given) and names <paramref name="host"/>.</summary>
    internal static bool Verify(
        X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors, string host, string? rootFile)
    {
        if (certificate is null) return false;
        using var server = new X509Certificate2(certificate);
        if (!server.MatchesHostname(host)) return false;
        if (rootFile is null) return (errors & ~SslPolicyErrors.RemoteCertificateNameMismatch) == SslPolicyErrors.None;

        var roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(rootFile);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (presented is not null)
            foreach (var element in presented.ChainElements) chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        return chain.Build(server);
    }

    public string? DescribeTlsFailure(ConnectionConfig cfg, Exception e)
    {
        var mode = cfg.Tls?.Mode ?? TlsMode.Prefer;
        if (e is NotSupportedException && e.Message.Contains("PEM client key", StringComparison.Ordinal)) return e.Message;
        var root = e.GetBaseException();
        var cause = root.Message.Trim();
        var tls = root is AuthenticationException or CryptographicException
                  || e.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase);
        var clientFiles = cfg.Tls?.ClientCertificate is not null && mode != TlsMode.Disable
            && root is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException;
        if (!tls && !clientFiles) return null;

        if (MissingFile(cfg.Tls, mode) is { } missing) return missing;
        if (clientFiles) return $"cannot read the client certificate or its key. Check both files and the passphrase. ({cause})";
        if (e.Message.Contains("does not support SSL", StringComparison.OrdinalIgnoreCase))
            return $"the server does not offer TLS, and SSL mode {mode} requires it.";
        if (root is AuthenticationException && TlsConfig.Verifies(mode))
        {
            var issuer = string.IsNullOrWhiteSpace(cfg.Tls!.RootCertificate)
                ? "an authority this computer trusts"
                : $"the authority in {cfg.Tls.RootCertificate}";
            var name = mode == TlsMode.VerifyFull ? $" and issued for the host name '{NetworkTarget(cfg).Host}'" : "";
            return $"the server certificate was refused. SSL mode {mode} needs a valid certificate signed by {issuer}{name}. ({cause})";
        }
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
