using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>An entry of the authentication drop-down.</summary>
public sealed record AuthOption(AuthMode Mode, string Label, string Hint);

// How the connection signs in to the database.
public partial class ConnectionDialogViewModel
{
    private static readonly AuthOption[] PostgresAuth =
    [
        new(AuthMode.Password, "User and password", ""),
        new(AuthMode.Integrated, "Windows (SSPI / Kerberos)", "Signs in with your Windows account. The server must offer SSPI or GSS."),
    ];

    private static readonly AuthOption[] SqlServerAuth =
    [
        new(AuthMode.Password, "SQL Server login", ""),
        new(AuthMode.Integrated, "Windows", "Signs in with your Windows account."),
        new(AuthMode.EntraInteractive, "Microsoft Entra: interactive (MFA)",
            "A Microsoft sign-in window opens on the first connection."),
        new(AuthMode.EntraDefault, "Microsoft Entra: default credential",
            "Uses what this machine already has: environment variables, managed identity, Azure CLI, Visual Studio."),
        new(AuthMode.EntraIntegrated, "Microsoft Entra: integrated", "For a Windows account of a domain federated with Entra ID."),
        new(AuthMode.EntraPassword, "Microsoft Entra: password",
            "No multi-factor authentication. Deprecated by Microsoft: prefer interactive."),
        new(AuthMode.EntraServicePrincipal, "Microsoft Entra: service principal",
            "An application registration: its client ID and a client secret."),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedAuthOption), nameof(AuthHint), nameof(HasAuthHint), nameof(ShowUser), nameof(ShowPassword),
        nameof(UserLabel), nameof(UserPlaceholder), nameof(PasswordLabel), nameof(HasSavedPassword), nameof(PasswordHint))]
    private AuthMode _auth;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasGeneralError))] private string? _userError;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasGeneralError))] private string? _authError;

    public IReadOnlyList<AuthOption> AuthOptions => Kind == DbKind.SqlServer ? SqlServerAuth : PostgresAuth;

    /// <summary>Bridge between the drop-down and <see cref="Auth"/>; the null a list swap selects is ignored.</summary>
    public AuthOption? SelectedAuthOption
    {
        get => AuthOptions.FirstOrDefault(o => o.Mode == Auth);
        set { if (value is not null) Auth = value.Mode; }
    }

    public string AuthHint => SelectedAuthOption?.Hint ?? "";
    public bool HasAuthHint => AuthHint.Length > 0;
    public bool ShowUser => IsNetwork && AuthModes.UsesUser(Kind, Auth);
    public bool ShowPassword => IsNetwork && AuthModes.UsesPassword(Auth);
    public string UserLabel => Auth == AuthMode.EntraServicePrincipal ? "Client _ID" : "_User";
    public string PasswordLabel => Auth == AuthMode.EntraServicePrincipal ? "_Secret" : "_Password";

    public string UserPlaceholder => Auth switch
    {
        AuthMode.EntraInteractive or AuthMode.EntraIntegrated => "Optional: name@example.com",
        AuthMode.EntraDefault => "Optional: client ID of a managed identity",
        AuthMode.EntraPassword => "name@example.com",
        AuthMode.EntraServicePrincipal => "Application (client) ID",
        AuthMode.Integrated => "Optional: database role, if it differs from your Windows name",
        _ => "",
    };

    partial void OnAuthChanged(AuthMode value)
    {
        // Azure SQL, where Entra sign-in is used, refuses clear connections; the user can still untick it.
        if (AuthModes.IsEntra(value) && _existing?.Auth != value) Encrypt = true;
        ResetTest();
        RefreshErrors();
    }

    private void FollowKindWithAuth()
    {
        if (!AuthModes.For(Kind).Contains(Auth)) Auth = AuthMode.Password;
        OnPropertyChanged(nameof(AuthOptions));
        OnPropertyChanged(nameof(SelectedAuthOption));
        OnPropertyChanged(nameof(AuthHint));
        OnPropertyChanged(nameof(HasAuthHint));
        OnPropertyChanged(nameof(ShowUser));
        OnPropertyChanged(nameof(ShowPassword));
    }

    /// <summary>A database password and an application secret are different things: one is never reused as the other.</summary>
    private static bool SameSecretKind(AuthMode a, AuthMode b) =>
        AuthModes.UsesPassword(a) && AuthModes.UsesPassword(b)
        && (a == AuthMode.EntraServicePrincipal) == (b == AuthMode.EntraServicePrincipal);

    private string? UserProblem() =>
        IsNetwork && Auth is AuthMode.EntraPassword or AuthMode.EntraServicePrincipal && string.IsNullOrWhiteSpace(User)
            ? (Auth == AuthMode.EntraServicePrincipal ? "Client ID is required." : "User is required.")
            : null;

    private string? AuthProblem() =>
        Kind == DbKind.Postgres && Auth == AuthMode.Integrated && UseSsh
            ? "PostgreSQL Windows authentication does not work through an SSH tunnel."
            : null;
}
