using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests;

public class ConnectionTlsDialogTests : IDisposable
{
    private readonly TreeFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private ConnectionDialogViewModel Filled(ConnectionConfig? existing = null)
    {
        var vm = new ConnectionDialogViewModel(_fx.Protector, [], [], existing);
        if (existing is not null) return vm;
        vm.Name = "n";
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
    public void NewConnection_IsPrefer_SavedAsTheDriverDefault_WithAWarning()
    {
        var vm = Filled();

        Assert.Equal(TlsMode.Prefer, vm.TlsMode);
        Assert.True(vm.HasTlsWarning);
        Assert.False(vm.ShowRootCertificate);
        Assert.Null(Confirm(vm).Tls);
    }

    [Theory]
    [InlineData(TlsMode.Disable, true)]
    [InlineData(TlsMode.Allow, true)]
    [InlineData(TlsMode.Prefer, true)]
    [InlineData(TlsMode.Require, true)]
    [InlineData(TlsMode.VerifyCA, true)]   // without a root CA file
    [InlineData(TlsMode.VerifyFull, false)]
    public void EveryMode_IsExplained_AndWarnsWhenTheServerIsNotIdentified(TlsMode mode, bool warns)
    {
        var vm = Filled();

        vm.TlsMode = mode;

        Assert.NotEmpty(vm.TlsHint);
        Assert.Equal(warns, vm.HasTlsWarning);
        vm.TlsRootFile = @"C:\ca.crt";
        if (mode == TlsMode.VerifyCA) Assert.False(vm.HasTlsWarning);
    }

    [Fact]
    public void OnlyWhatTheModeUses_IsSaved()
    {
        var vm = Filled();
        vm.TlsMode = TlsMode.VerifyFull;
        vm.TlsRootFile = @" C:\ca.crt ";
        vm.UseClientCertificate = true;
        vm.TlsCertificateFile = @"C:\c.crt";
        vm.TlsKeyFile = @"C:\c.key";
        vm.TlsPassphrase = "secret";

        Assert.Equal(new TlsConfig(TlsMode.VerifyFull, @"C:\ca.crt", @"C:\c.crt", @"C:\c.key",
            _fx.Protector.Protect("secret")), Confirm(vm).Tls);

        vm.TlsMode = TlsMode.Require;
        Assert.Null(Confirm(vm).Tls!.RootCertificate);

        vm.UseClientCertificate = false;
        Assert.Equal(new TlsConfig(TlsMode.Require), Confirm(vm).Tls);

        vm.UseClientCertificate = true;
        vm.TlsMode = TlsMode.Disable;
        Assert.False(vm.ShowClientCertificate);
        Assert.Equal(new TlsConfig(TlsMode.Disable), Confirm(vm).Tls);

        vm.Kind = DbKind.SqlServer;
        Assert.Null(Confirm(vm).Tls);
    }

    [Fact]
    public void ClientCertificate_NeedsItsFile()
    {
        var vm = Filled();
        vm.UseClientCertificate = true;

        Assert.False(vm.TryConfirm());
        Assert.Equal("Client certificate file is required.", vm.TlsCertificateError);
        Assert.False(vm.HasGeneralError);

        vm.TlsCertificateFile = @"C:\c.pfx";
        Assert.Null(vm.TlsCertificateError);
        Assert.Equal(new TlsConfig(TlsMode.Prefer, null, @"C:\c.pfx"), Confirm(vm).Tls);
    }

    [Fact]
    public void Editing_KeepsTheSavedPassphrase_UntilTheKeyChanges()
    {
        var existing = new ConnectionConfig(Guid.NewGuid(), "n", DbKind.Postgres, null, "db", 5432, "d", "u", null, false, false, false)
        {
            Tls = new TlsConfig(TlsMode.VerifyCA, @"C:\ca.crt", @"C:\c.crt", @"C:\c.key", "kept"),
        };
        var vm = Filled(existing);

        Assert.True(vm.UseClientCertificate);
        Assert.Equal("", vm.TlsPassphrase);
        Assert.Equal("Saved. Type to replace it", vm.TlsPassphraseHint);
        Assert.Equal(existing.Tls, Confirm(vm).Tls);

        vm.TlsKeyFile = @"C:\other.key";
        Assert.Null(Confirm(vm).Tls!.ProtectedKeyPassphrase);
    }

    [Fact]
    public async Task Test_ReceivesTheTlsSettings()
    {
        ConnectionConfig? tested = null;
        var vm = new ConnectionDialogViewModel(_fx.Protector, [], [], test: (c, _, _) =>
        {
            tested = c;
            return Task.CompletedTask;
        });
        vm.Host = "db";
        vm.Database = "d";
        vm.TlsMode = TlsMode.VerifyFull;

        await vm.TestConnectionAsync();

        Assert.True(vm.TestSucceeded);
        Assert.Equal(new TlsConfig(TlsMode.VerifyFull), tested!.Tls);
        vm.TlsMode = TlsMode.Require;
        Assert.False(vm.HasTestMessage);   // the result described other settings
    }

    [AvaloniaFact]
    public void View_ShowsOnlyTheFieldsOfTheChoice()
    {
        var vm = Filled();
        var dialog = new ConnectionDialog { DataContext = vm };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        T Find<T>(string name) where T : Control => dialog.FindControl<T>(name)!;

        Assert.True(Find<StackPanel>("TlsSection").IsVisible);
        Assert.False(Find<TextBox>("TlsRootBox").IsEffectivelyVisible);
        Assert.False(Find<TextBox>("TlsCertificateBox").IsEffectivelyVisible);

        vm.TlsMode = TlsMode.VerifyFull;
        Find<CheckBox>("ClientCertificateCheck").IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(Find<TextBox>("TlsRootBox").IsEffectivelyVisible);
        Assert.True(Find<TextBox>("TlsCertificateBox").IsEffectivelyVisible);
        Assert.False(Find<Border>("TlsWarningBanner").IsVisible);
        Assert.Equal(TlsMode.VerifyFull, Find<ComboBox>("TlsModeBox").SelectedItem);

        vm.Kind = DbKind.SqlServer;
        Dispatcher.UIThread.RunJobs();

        Assert.False(Find<StackPanel>("TlsSection").IsVisible);
        dialog.Close();
    }
}
