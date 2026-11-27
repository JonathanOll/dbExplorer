namespace DbExplorer.Core.Model;

/// <summary>How a connection signs in to the database. Persisted by name; old files have none and mean <see cref="Password"/>.</summary>
public enum AuthMode
{
    /// <summary>User and password checked by the database.</summary>
    Password,

    /// <summary>The Windows account (SQL Server integrated security; PostgreSQL SSPI / GSS).</summary>
    Integrated,

    /// <summary>Microsoft Entra ID, sign-in window with multi-factor authentication (SQL Server).</summary>
    EntraInteractive,

    /// <summary>Microsoft Entra ID, the credential found on this machine: environment, managed identity, Azure CLI... (SQL Server).</summary>
    EntraDefault,

    /// <summary>Microsoft Entra ID with the signed-in Windows account of a federated domain (SQL Server).</summary>
    EntraIntegrated,

    /// <summary>Microsoft Entra ID user and password (SQL Server; deprecated by Microsoft).</summary>
    EntraPassword,

    /// <summary>Microsoft Entra ID application: client id and client secret (SQL Server).</summary>
    EntraServicePrincipal,
}

/// <summary>Which authentication modes each engine offers and what each one asks for.</summary>
public static class AuthModes
{
    private static readonly AuthMode[] SqlServer =
    [
        AuthMode.Password, AuthMode.Integrated, AuthMode.EntraInteractive, AuthMode.EntraDefault,
        AuthMode.EntraIntegrated, AuthMode.EntraPassword, AuthMode.EntraServicePrincipal,
    ];

    private static readonly AuthMode[] Postgres = [AuthMode.Password, AuthMode.Integrated];

    public static IReadOnlyList<AuthMode> For(DbKind kind) => kind switch
    {
        DbKind.SqlServer => SqlServer,
        DbKind.Postgres => Postgres,
        _ => [AuthMode.Password],
    };

    /// <summary>A secret (password or client secret) is stored and sent.</summary>
    public static bool UsesPassword(AuthMode mode) =>
        mode is AuthMode.Password or AuthMode.EntraPassword or AuthMode.EntraServicePrincipal;

    /// <summary>A user name means something (required, or an optional hint). SQL Server integrated security ignores it.</summary>
    public static bool UsesUser(DbKind kind, AuthMode mode) => !(kind == DbKind.SqlServer && mode == AuthMode.Integrated);

    public static bool IsEntra(AuthMode mode) => mode is not (AuthMode.Password or AuthMode.Integrated);
}

/// <summary>How the SSH server checks who we are.</summary>
public enum SshAuthKind { Password, PrivateKey }

/// <summary>
/// The SSH jump server a connection goes through; the database host and port of the connection are resolved from that server.
/// Secrets are protected like the database password.
/// </summary>
public sealed record SshTunnelConfig(
    string Host,
    int Port,
    string User,
    SshAuthKind Auth,
    string? ProtectedPassword = null,
    string? KeyFile = null,
    string? ProtectedPassphrase = null)
{
    public const int DefaultPort = 22;
}
