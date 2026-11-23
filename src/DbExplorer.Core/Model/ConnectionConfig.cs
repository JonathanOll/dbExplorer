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
    bool ReadOnly);
