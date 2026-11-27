using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

// TLS of a PostgreSQL, MySQL or MariaDB connection: SSL mode and certificate files.
public partial class ConnectionDialogViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TlsHint), nameof(TlsWarning), nameof(HasTlsWarning), nameof(ShowRootCertificate),
        nameof(ShowClientCertificateChoice), nameof(ShowClientCertificate))]
    private TlsMode _tlsMode = TlsMode.Prefer;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(TlsWarning), nameof(HasTlsWarning))]
    private string _tlsRootFile = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowClientCertificate))]
    private bool _useClientCertificate;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(TlsPassphraseHint))] private string _tlsCertificateFile = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TlsPassphraseHint))] private string _tlsKeyFile = "";
    [ObservableProperty] private string _tlsPassphrase = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasGeneralError))] private string? _tlsCertificateError;

    public bool IsPostgres => Kind == DbKind.Postgres;

    /// <summary>The engine has the TLS settings.</summary>
    public bool ShowTls => Kind is DbKind.Postgres or DbKind.MySql;

    private static readonly TlsMode[] AllTlsModes = Enum.GetValues<TlsMode>();

    // MySQL has no "allow".
    private static readonly TlsMode[] MySqlTlsModes = [.. AllTlsModes.Where(m => m != TlsMode.Allow)];

    public IReadOnlyList<TlsMode> TlsModes => Kind == DbKind.MySql ? MySqlTlsModes : AllTlsModes;

    private void FollowKindWithTls()
    {
        if (Kind == DbKind.MySql && TlsMode == TlsMode.Allow) TlsMode = TlsMode.Prefer;
        var chosen = TlsMode;
        OnPropertyChanged(nameof(TlsModes));
        OnPropertyChanged(nameof(TlsRootHint));
        OnPropertyChanged(nameof(TlsPassphraseHint));
        TlsMode = chosen;   // the drop-down may drop its selection when the list changes
        OnPropertyChanged(nameof(TlsMode));
    }

    /// <summary>Where the authority comes from when no root CA file is chosen.</summary>
    public string TlsRootHint => Kind == DbKind.MySql
        ? "Empty: the authorities Windows trusts."
        : @"Empty: %APPDATA%\postgresql\root.crt if present, else the authorities Windows trusts.";

    /// <summary>What the selected mode does, in plain words.</summary>
    public string TlsHint => TlsMode switch
    {
        TlsMode.Disable => "No encryption.",
        TlsMode.Allow => "Connects without encryption, and uses TLS only if the server insists.",
        TlsMode.Prefer => "Uses TLS when the server offers it, otherwise connects without. This is the default.",
        TlsMode.Require => "Always encrypted. Any server certificate is accepted.",
        TlsMode.VerifyCA => "Always encrypted. The server certificate must come from a trusted authority; its host name is not checked.",
        _ => "Always encrypted. The server certificate must come from a trusted authority and match the host above. The safest choice.",
    };

    /// <summary>What is left unchecked by the selected settings; empty when the server's identity is verified.</summary>
    public string TlsWarning => TlsMode switch
    {
        TlsMode.Disable => "Not verified: data travels in clear text and the server's identity is not checked.",
        TlsMode.Allow or TlsMode.Prefer =>
            "Not verified: someone on the network can pose as the server, or keep the connection unencrypted.",
        TlsMode.Require => "Not verified: someone on the network can pose as the server.",
        // Public authorities sign certificates for anyone: without a CA of one's own, the name is what identifies the server.
        TlsMode.VerifyCA when string.IsNullOrWhiteSpace(TlsRootFile) =>
            "Weak without a root CA file: a certificate for any host name passes. Choose a file, or use VerifyFull.",
        _ => "",
    };

    public bool HasTlsWarning => TlsWarning.Length > 0;
    public bool ShowRootCertificate => TlsConfig.Verifies(TlsMode);
    public bool ShowClientCertificateChoice => TlsMode != TlsMode.Disable;
    public bool ShowClientCertificate => ShowClientCertificateChoice && UseClientCertificate;

    public string TlsPassphraseHint => SavedTlsPassphrase() is not null ? "Saved. Type to replace it"
        : Kind == DbKind.MySql ? "Only for a .pfx file" : "Only for an encrypted key";

    partial void OnTlsModeChanged(TlsMode value) { ResetTest(); RefreshErrors(); }
    partial void OnTlsRootFileChanged(string value) => ResetTest();
    partial void OnUseClientCertificateChanged(bool value) { ResetTest(); RefreshErrors(); }
    partial void OnTlsCertificateFileChanged(string value) { ResetTest(); RefreshErrors(); }
    partial void OnTlsKeyFileChanged(string value) => ResetTest();
    partial void OnTlsPassphraseChanged(string value) { ResetTest(); RefreshErrors(); }

    private void LoadTls(TlsConfig? tls)
    {
        if (tls is null) return;
        TlsMode = tls.Mode;
        TlsRootFile = tls.RootCertificate ?? "";
        UseClientCertificate = tls.ClientCertificate is not null;
        TlsCertificateFile = tls.ClientCertificate ?? "";
        TlsKeyFile = tls.ClientKey ?? "";
    }

    // A passphrase belongs to its key: choosing other files forgets it.
    private string? SavedTlsPassphrase() =>
        _existing?.Tls is { ClientCertificate: not null } tls
        && tls.ClientCertificate == TlsCertificateFile.Trim() && (tls.ClientKey ?? "") == TlsKeyFile.Trim()
            ? tls.ProtectedKeyPassphrase
            : null;

    /// <summary>Only what the mode uses is saved. Null (the driver's defaults) for plain Prefer.</summary>
    private TlsConfig? BuildTls()
    {
        if (!ShowTls) return null;
        var certificate = ShowClientCertificate ? Filled(TlsCertificateFile) : null;
        if (TlsMode == TlsMode.Prefer && certificate is null) return null;
        return new TlsConfig(
            TlsMode,
            ShowRootCertificate ? Filled(TlsRootFile) : null,
            certificate,
            certificate is null ? null : Filled(TlsKeyFile),
            certificate is null ? null
            : TlsPassphrase.Length > 0 ? _protector.Protect(TlsPassphrase) : SavedTlsPassphrase());

        static string? Filled(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private string? TlsCertificateProblem()
    {
        if (!ShowTls || !ShowClientCertificate) return null;
        if (string.IsNullOrWhiteSpace(TlsCertificateFile)) return "Client certificate file is required.";
        var pfx = Path.GetExtension(TlsCertificateFile.Trim()).ToLowerInvariant() is ".pfx" or ".p12";
        return Kind == DbKind.MySql && !pfx && (TlsPassphrase.Length > 0 || SavedTlsPassphrase() is not null)
            ? "MySQL cannot read an encrypted PEM key: use a .pfx file, or a key without passphrase."
            : null;
    }
}
