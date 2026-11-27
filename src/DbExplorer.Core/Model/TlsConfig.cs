namespace DbExplorer.Core.Model;

/// <summary>How a connection uses TLS (PostgreSQL's <c>sslmode</c>). Persisted by name.</summary>
public enum TlsMode { Disable, Allow, Prefer, Require, VerifyCA, VerifyFull }

/// <summary>
/// The TLS settings of a PostgreSQL connection. A connection without them keeps the driver's defaults:
/// <see cref="TlsMode.Prefer"/>, server certificate not checked. Paths may hold environment variables; the passphrase is
/// protected like the database password.
/// </summary>
/// <param name="RootCertificate">CA file the server certificate must chain to (verifying modes); null: what Windows trusts.</param>
/// <param name="ClientCertificate">PEM certificate, or a .pfx file holding the key too.</param>
/// <param name="ClientKey">PEM key file; null when the certificate file holds the key.</param>
public sealed record TlsConfig(
    TlsMode Mode = TlsMode.Prefer,
    string? RootCertificate = null,
    string? ClientCertificate = null,
    string? ClientKey = null,
    string? ProtectedKeyPassphrase = null)
{
    /// <summary>The mode checks the server certificate.</summary>
    public static bool Verifies(TlsMode mode) => mode is TlsMode.VerifyCA or TlsMode.VerifyFull;
}
