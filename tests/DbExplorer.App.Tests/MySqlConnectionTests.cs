using DbExplorer.App.ViewModels;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.Tests;

public class MySqlConnectionTests : IDisposable
{
    private readonly TreeFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private ConnectionDialogViewModel Filled(ConnectionConfig? existing = null) =>
        existing is not null
            ? new ConnectionDialogViewModel(_fx.Protector, [], [], existing)
            : new ConnectionDialogViewModel(_fx.Protector, [], []) { Name = "m", Kind = DbKind.MySql, Host = "db", Database = "shop" };

    private static ConnectionConfig Confirm(ConnectionDialogViewModel vm)
    {
        Assert.True(vm.TryConfirm(), vm.Error);
        return vm.Result!;
    }

    [Fact]
    public void NewMySqlConnection_HasItsPortAuthAndTls()
    {
        var vm = Filled();

        Assert.Equal("3306", vm.PortText);
        Assert.True(vm.ShowTls);
        Assert.DoesNotContain(TlsMode.Allow, vm.TlsModes);
        Assert.Equal([AuthMode.Password, AuthMode.Integrated], vm.AuthOptions.Select(o => o.Mode));
        Assert.DoesNotContain("postgresql", vm.TlsRootHint);
        var saved = Confirm(vm);
        Assert.Equal((DbKind.MySql, 3306, false, false), (saved.Kind, saved.Port, saved.Encrypt, saved.TrustServerCertificate));
        Assert.Null(saved.Tls);
    }

    [Fact]
    public void AllowMode_FallsBackToPrefer_WhenTheEngineBecomesMySql()
    {
        var vm = Filled();
        vm.Kind = DbKind.Postgres;
        vm.TlsMode = TlsMode.Allow;

        vm.Kind = DbKind.MySql;

        Assert.Equal(TlsMode.Prefer, vm.TlsMode);
    }

    [Fact]
    public void TlsSettings_AreSavedAndLoadedBack()
    {
        var vm = Filled();
        vm.TlsMode = TlsMode.VerifyCA;
        vm.TlsRootFile = @"C:\ca.pem";

        var again = Filled(Confirm(vm));

        Assert.Equal((DbKind.MySql, TlsMode.VerifyCA, @"C:\ca.pem"), (again.Kind, again.TlsMode, again.TlsRootFile));
    }

    [Fact]
    public void EncryptedPemKey_IsRefusedInTheDialog_APfxIsNot()
    {
        var vm = Filled();
        vm.UseClientCertificate = true;
        vm.TlsCertificateFile = @"C:\c.pem";
        vm.TlsPassphrase = "x";

        Assert.False(vm.TryConfirm());
        Assert.Contains(".pfx", vm.TlsCertificateError);

        vm.TlsCertificateFile = @"C:\c.pfx";
        Assert.True(vm.TryConfirm(), vm.Error);
    }

    [Fact]
    public void Tree_ListsTheTablesWithoutASchemaLevel()
    {
        var config = Confirm(Filled());
        var node = new ConnectionNodeViewModel(config);
        var table = new TableInfo(new TableRef(null, "orders"), false, [], [], []);

        ConnectionTreeViewModel.ApplySchema(node, new Session(config, DriverFactory.For(DbKind.MySql), [table], null));

        Assert.True(node.IsExpanded);
        // Procedures and functions, then the tables: no schema node in between.
        Assert.Equal(2, node.Children.OfType<RoutineGroupNodeViewModel>().Count());
        Assert.Single(node.Children.OfType<TableNodeViewModel>());
        Assert.Empty(node.Children.OfType<SchemaNodeViewModel>());
    }
}
