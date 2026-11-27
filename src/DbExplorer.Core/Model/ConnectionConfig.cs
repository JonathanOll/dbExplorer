namespace DbExplorer.Core.Model;

/// <summary>One saved connection = one database. For SQLite, <see cref="Database"/> is the file path.</summary>
public record ConnectionConfig(
    Guid Id,
    string Name,
    DbKind Kind,
    string? Folder,
    string? Host,
    int? Port,
    string Database,
    string? User,
    string? ProtectedPassword,
    bool Encrypt,
    bool TrustServerCertificate,
    bool ReadOnly)
{
    /// <summary>How the database sign-in works. Init property: old configs load as user and password.</summary>
    public AuthMode Auth { get; init; }

    /// <summary>The SSH tunnel the connection goes through; null connects directly.</summary>
    public SshTunnelConfig? Ssh { get; init; }
}
