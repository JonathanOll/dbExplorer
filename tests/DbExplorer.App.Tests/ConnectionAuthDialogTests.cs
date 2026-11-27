using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using DbExplorer.Core.Tunnels;

namespace DbExplorer.App.Tests;

public class ConnectionAuthDialogTests : IDisposable
{
    private readonly TreeFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private ConnectionDialogViewModel New(ConnectionConfig? existing = null,
        Func<ConnectionConfig, string?, CancellationToken, Task>? test = null) =>
        new(_fx.Protector, [], [], existing, test);

    private ConnectionDialogViewModel Filled(DbKind kind = DbKind.SqlServer)
    {
        var vm = New();
        vm.Name = "n";
        vm.Kind = kind;
        vm.Host = "db";
        vm.Database = "d";
        return vm;
    }

    private static ConnectionConfig Confirm(ConnectionDialogViewModel vm)
    {
        Assert.True(vm.TryConfirm(), vm.Error);
        return vm.Result!;
    }

    [Fact]
    public void AuthChoices_FollowTheEngine()
    {
        var vm = Filled();
        Assert.Equal(7, vm.AuthOptions.Count);

        vm.Auth = AuthMode.EntraDefault;
        vm.Kind = DbKind.Postgres;

        Assert.Equal(2, vm.AuthOptions.Count);
        Assert.Equal(AuthMode.Password, vm.Auth);
    }

    [Fact]
    public void WindowsAuthentication_SavesNoUserNorPassword()
    {
        var vm = Filled();
        vm.User = "sa";
        vm.Password = "secret";
        vm.Auth = AuthMode.Integrated;

        Assert.False(vm.ShowUser);
        Assert.False(vm.ShowPassword);
        var c = Confirm(vm);
        Assert.Equal(AuthMode.Integrated, c.Auth);
        Assert.Null(c.User);
        Assert.Null(c.ProtectedPassword);
    }

    [Fact]
    public void EntraModes_TickEncrypt_AndServicePrincipalNeedsAClientId()
    {
        var vm = Filled();

        vm.Auth = AuthMode.EntraServicePrincipal;

        Assert.True(vm.Encrypt);
        Assert.Equal("Client _ID", vm.UserLabel);
        Assert.False(vm.TryConfirm());
        Assert.Equal("Client ID is required.", vm.UserError);
        Assert.False(vm.HasGeneralError);
        vm.User = "app";
        Assert.Null(vm.UserError);
        Assert.Equal(AuthMode.EntraServicePrincipal, Confirm(vm).Auth);
    }

    [Fact]
    public void ADatabasePassword_IsNeverReusedAsAnApplicationSecret()
    {
        var first = Filled();
        first.Password = "pw";
        var saved = Confirm(first);

        var edit = New(saved);
        Assert.True(edit.HasSavedPassword);
        edit.Auth = AuthMode.EntraServicePrincipal;
        edit.User = "app";

        Assert.False(edit.HasSavedPassword);
        Assert.Null(Confirm(edit).ProtectedPassword);
    }

    [Fact]
    public void Ssh_IsValidated_Saved_AndKeepsItsSecretsOnEdit()
    {
        var vm = Filled(DbKind.Postgres);
        vm.UseSsh = true;
        Assert.False(vm.TryConfirm());
        Assert.NotNull(vm.SshHostError);
        Assert.NotNull(vm.SshUserError);
        vm.SshHost = "jump";
        vm.SshUser = "me";
        vm.SshPassword = "ssh-secret";
        var saved = Confirm(vm);
        Assert.Equal("ssh-secret", _fx.Protector.Unprotect(saved.Ssh!.ProtectedPassword!));
        Assert.Equal(22, saved.Ssh.Port);

        var edit = New(saved);
        Assert.True(edit.UseSsh);
        Assert.Equal("", edit.SshPassword);
        Assert.Equal(saved.Ssh, Confirm(edit).Ssh);

        edit.SshAuth = SshAuthKind.PrivateKey;
        Assert.False(edit.TryConfirm());
        Assert.NotNull(edit.SshKeyError);
        edit.SshKeyFile = @"C:\keys\id";
        edit.SshPassphrase = "phrase";
        var withKey = Confirm(edit).Ssh!;
        Assert.Null(withKey.ProtectedPassword);

        var again = New(saved with { Ssh = withKey });
        Assert.Equal(withKey, Confirm(again).Ssh);
        again.SshKeyFile = @"C:\keys\other";
        Assert.Null(Confirm(again).Ssh!.ProtectedPassphrase);

        again.UseSsh = false;
        Assert.Null(Confirm(again).Ssh);
    }

    [Fact]
    public void PostgresWindowsAuthentication_IsRefusedWithATunnel()
    {
        var vm = Filled(DbKind.Postgres);
        vm.Auth = AuthMode.Integrated;
        vm.UseSsh = true;
        vm.SshHost = "jump";
        vm.SshUser = "me";

        Assert.False(vm.TryConfirm());
        Assert.Contains("SSH tunnel", vm.AuthError);
    }

    [Fact]
    public async Task Test_PassesTunnelAndAuth_AndShowsTheFailedStage()
    {
        ConnectionConfig? tested = null;
        Exception? failure = null;
        var vm = New(null, (c, _, _) =>
        {
            tested = c;
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        });
        vm.Host = "db";
        vm.Database = "d";
        vm.UseSsh = true;
        vm.SshHost = "jump";
        vm.SshUser = "me";
        vm.SshPassword = "s";

        await vm.TestConnectionAsync();

        Assert.True(vm.TestSucceeded);
        Assert.Contains("through the SSH tunnel", vm.TestMessage);
        Assert.Equal("jump", tested!.Ssh!.Host);

        failure = new ConnectionStageException(ConnectionStage.SshSignIn, "Permission denied");
        await vm.TestConnectionAsync();

        Assert.False(vm.TestSucceeded);
        Assert.Equal("SSH tunnel, sign-in failed: Permission denied", vm.TestMessage);
    }

    [AvaloniaFact]
    public void Dialog_ShowsOnlyWhatTheChoicesNeed()
    {
        var vm = New();
        var window = new ConnectionDialog { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        bool Visible(string name) => window.FindControl<Control>(name)!.IsEffectivelyVisible;

        Assert.True(Visible("PasswordBox"));
        Assert.False(Visible("SshHostBox"));

        vm.Kind = DbKind.SqlServer;
        vm.Auth = AuthMode.Integrated;
        vm.UseSsh = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(Visible("UserBox"));
        Assert.False(Visible("PasswordBox"));
        Assert.True(Visible("SshHostBox"));
        Assert.True(Visible("SshPasswordBox"));
        Assert.False(Visible("SshKeyBox"));
        Assert.Same(vm.SelectedAuthOption, window.FindControl<ComboBox>("AuthBox")!.SelectedItem);

        vm.SshAuthIndex = 1;
        vm.Kind = DbKind.Sqlite;
        Dispatcher.UIThread.RunJobs();
        Assert.False(Visible("SshCheck"));
        Assert.False(Visible("AuthBox"));
        window.Close();
    }

    [AvaloniaFact]
    public void HostKeyDialog_ShowsTheFingerprint_AndDefaultsToCancel()
    {
        var (window, cancel) = HostKeyDialog.Build(new HostKeyInfo("jump", 22, "ssh-ed25519", "SHA256:abc"));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(cancel.IsDefault);
        Assert.True(cancel.IsCancel);
        window.Close();
    }
}
