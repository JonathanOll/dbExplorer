using Avalonia.Controls;
using Avalonia.VisualTree;
using DbExplorer.App;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using DbExplorer.Core.Tunnels;

namespace UiShots;

internal static partial class Scenes
{
    // SQL Server with Microsoft Entra sign-in: the form shows only what the mode asks for.
    private static async Task<Window?> ConnectionEntraScene(Demo d)
    {
        await Open(d);
        var vm = new ConnectionDialogViewModel(new PlainProtector(), ["Production", "Staging"], [])
        {
            Name = "Billing (Azure)", Kind = DbKind.SqlServer, Host = "billing.database.windows.net", Database = "billing",
            Auth = AuthMode.EntraServicePrincipal, User = "6f0d2c1e-4b7a-4c2e-9a51-0d6f7c1b2a34", Password = "client-secret",
        };
        var dialog = new ConnectionDialog { DataContext = vm };
        dialog.Show(d.Window);
        await Settle();
        return dialog;
    }

    // Through an SSH tunnel with a private key, after a test that failed at the SSH stage (scrolled to the end).
    private static async Task<Window?> ConnectionSshScene(Demo d)
    {
        await Open(d);
        var vm = new ConnectionDialogViewModel(new PlainProtector(), ["Production", "Staging"], [], null, (_, _, _) =>
            Task.FromException(new ConnectionStageException(ConnectionStage.SshSignIn,
                "deploy@bastion.example.com:22: Permission denied (publickey).")))
        {
            Name = "Orders (private network)", Host = "orders-db.internal", Database = "orders", User = "report_ro",
            UseSsh = true, SshHost = "bastion.example.com", SshUser = "deploy", SshAuth = SshAuthKind.PrivateKey,
            SshKeyFile = @"%USERPROFILE%\.ssh\id_ed25519",
        };
        var dialog = new ConnectionDialog { DataContext = vm };
        dialog.Show(d.Window);
        await Settle();
        await vm.TestConnectionAsync();
        await Settle();
        dialog.GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
        await Settle();
        return dialog;
    }

    private static async Task<Window?> ConnectionTlsScene(Demo d)
    {
        await Open(d);
        var vm = new ConnectionDialogViewModel(new PlainProtector(), ["Production", "Staging"], [], null, (_, _, _) =>
            Task.FromException(new ConnectionStageException(ConnectionStage.Tls,
                "the server certificate was refused. SSL mode VerifyFull needs a valid certificate signed by the authority in "
                + @"C:\certs\company-ca.crt and issued for the host name 'orders-db.internal'.")))
        {
            Name = "Orders (verified)", Host = "orders-db.internal", Database = "orders", User = "report_ro",
            TlsMode = TlsMode.VerifyFull, TlsRootFile = @"C:\certs\company-ca.crt",
            UseClientCertificate = true, TlsCertificateFile = @"C:\certs\report.crt", TlsKeyFile = @"C:\certs\report.key",
        };
        var dialog = new ConnectionDialog { DataContext = vm };
        dialog.Show(d.Window);
        await Settle();
        await vm.TestConnectionAsync();
        await Settle();
        dialog.GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
        await Settle();
        return dialog;
    }

    private static async Task<Window?> HostKeyScene(Demo d)
    {
        await Open(d);
        var (dialog, cancel) = HostKeyDialog.Build(new HostKeyInfo(
            "bastion.example.com", 22, "ssh-ed25519", "SHA256:uNiVztksCsDhcc0u9e8BujQXVUpKZIDTMczCvj3tD2s"));
        dialog.Show(d.Window);
        cancel.Focus();
        await Settle();
        return dialog;
    }
}
