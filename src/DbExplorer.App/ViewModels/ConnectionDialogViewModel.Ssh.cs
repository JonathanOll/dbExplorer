using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

// The optional SSH tunnel.
public partial class ConnectionDialogViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSsh))] private bool _useSsh;
    [ObservableProperty] private string _sshHost = "";
    [ObservableProperty] private string _sshPortText = SshTunnelConfig.DefaultPort.ToString();
    [ObservableProperty] private string _sshUser = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SshAuthIndex), nameof(IsSshKey), nameof(IsSshPassword))]
    private SshAuthKind _sshAuth;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(SshPasswordHint))] private string _sshPassword = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SshPassphraseHint))] private string _sshKeyFile = "";
    [ObservableProperty] private string _sshPassphrase = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasGeneralError))] private string? _sshHostError;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasGeneralError))] private string? _sshPortError;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasGeneralError))] private string? _sshUserError;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasGeneralError))] private string? _sshKeyError;

    public bool ShowSsh => IsNetwork && UseSsh;

    public IReadOnlyList<string> SshAuthChoices { get; } = ["Password", "Private key"];

    public int SshAuthIndex
    {
        get => (int)SshAuth;
        set { if (value >= 0) SshAuth = (SshAuthKind)value; }
    }

    public bool IsSshPassword => SshAuth == SshAuthKind.Password;
    public bool IsSshKey => SshAuth == SshAuthKind.PrivateKey;

    public string SshPasswordHint => SavedSshPassword() is null ? "" : "Saved. Type to replace it";

    public string SshPassphraseHint => SavedSshPassphrase() is null ? "Only for an encrypted key" : "Saved. Type to replace it";

    partial void OnUseSshChanged(bool value) { ResetTest(); RefreshErrors(); }
    partial void OnSshHostChanged(string value) { ResetTest(); RefreshErrors(); }
    partial void OnSshPortTextChanged(string value) { ResetTest(); RefreshErrors(); }
    partial void OnSshUserChanged(string value) { ResetTest(); RefreshErrors(); }
    partial void OnSshAuthChanged(SshAuthKind value) { ResetTest(); RefreshErrors(); }
    partial void OnSshPasswordChanged(string value) => ResetTest();
    partial void OnSshKeyFileChanged(string value) { ResetTest(); RefreshErrors(); }
    partial void OnSshPassphraseChanged(string value) => ResetTest();

    private void LoadSsh(SshTunnelConfig? ssh)
    {
        if (ssh is null) return;
        UseSsh = true;
        SshHost = ssh.Host;
        SshPortText = (ssh.Port > 0 ? ssh.Port : SshTunnelConfig.DefaultPort).ToString();
        SshUser = ssh.User;
        SshAuth = ssh.Auth;
        SshKeyFile = ssh.KeyFile ?? "";
    }

    private string? SavedSshPassword() =>
        _existing?.Ssh is { Auth: SshAuthKind.Password } ssh ? ssh.ProtectedPassword : null;

    // A passphrase belongs to its key file: choosing another file forgets it.
    private string? SavedSshPassphrase() =>
        _existing?.Ssh is { Auth: SshAuthKind.PrivateKey } ssh && ssh.KeyFile == SshKeyFile.Trim() ? ssh.ProtectedPassphrase : null;

    private SshTunnelConfig? BuildSsh()
    {
        if (!IsNetwork || !UseSsh) return null;
        var port = int.TryParse(SshPortText, out var p) ? p : SshTunnelConfig.DefaultPort;
        return IsSshPassword
            ? new SshTunnelConfig(SshHost.Trim(), port, SshUser.Trim(), SshAuthKind.Password,
                ProtectedPassword: SshPassword.Length > 0 ? _protector.Protect(SshPassword) : SavedSshPassword())
            : new SshTunnelConfig(SshHost.Trim(), port, SshUser.Trim(), SshAuthKind.PrivateKey,
                KeyFile: SshKeyFile.Trim(),
                ProtectedPassphrase: SshPassphrase.Length > 0 ? _protector.Protect(SshPassphrase) : SavedSshPassphrase());
    }

    private string? SshHostProblem() => ShowSsh && string.IsNullOrWhiteSpace(SshHost) ? "SSH host is required." : null;

    private string? SshPortProblem() =>
        ShowSsh && !(int.TryParse(SshPortText, out var port) && port is > 0 and <= 65535)
            ? "SSH port must be a number between 1 and 65535."
            : null;

    private string? SshUserProblem() => ShowSsh && string.IsNullOrWhiteSpace(SshUser) ? "SSH user is required." : null;

    private string? SshKeyProblem() =>
        ShowSsh && IsSshKey && string.IsNullOrWhiteSpace(SshKeyFile) ? "Private key file is required." : null;
}
