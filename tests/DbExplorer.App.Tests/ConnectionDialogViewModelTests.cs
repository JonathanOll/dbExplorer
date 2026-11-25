using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests;

public class ConnectionDialogViewModelTests : IDisposable
{
    private readonly TreeFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private ConnectionDialogViewModel New(ConnectionConfig? existing = null, params string[] taken) =>
        new(_fx.Protector, ["Company", "Admin"], taken, existing);

    [Fact]
    public void KindChange_SetsDefaultPort_UnlessUserTypedOne()
    {
        var vm = New();

        vm.Kind = DbKind.Postgres;
        Assert.Equal("5432", vm.PortText);
        vm.Kind = DbKind.SqlServer;
        Assert.Equal("1433", vm.PortText);
        vm.PortText = "14330";
        vm.Kind = DbKind.Postgres;
        Assert.Equal("14330", vm.PortText);
        vm.Kind = DbKind.Sqlite;
        Assert.True(vm.IsSqlite);
        Assert.False(vm.IsNetwork);
    }

    [Fact]
    public void Validate_ReportsMissingNameDatabaseHostAndBadPort()
    {
        var vm = New();
        vm.Kind = DbKind.Postgres;

        Assert.Contains("name", vm.Validate(), StringComparison.OrdinalIgnoreCase);
        vm.Name = "x";
        Assert.Contains("host", vm.Validate(), StringComparison.OrdinalIgnoreCase);
        vm.Host = "h";
        Assert.Contains("database", vm.Validate(), StringComparison.OrdinalIgnoreCase);
        vm.Database = "db";
        vm.PortText = "abc";
        Assert.Contains("port", vm.Validate(), StringComparison.OrdinalIgnoreCase);
        vm.PortText = "70000";
        Assert.Contains("port", vm.Validate(), StringComparison.OrdinalIgnoreCase);
        vm.PortText = "5432";
        Assert.Null(vm.Validate());
    }

    [Fact]
    public void Validate_RejectsDuplicateName_CaseInsensitive()
    {
        var vm = New(null, "Existing");
        vm.Kind = DbKind.Sqlite;
        vm.Name = "existing";
        vm.Database = "a.db";

        Assert.Contains("already exists", vm.Validate());
    }

    [Fact]
    public void TryConfirm_BuildsConfig_ProtectsPassword_AndSetsErrorOtherwise()
    {
        var vm = New();
        vm.Kind = DbKind.SqlServer;
        vm.Name = "Prod";
        vm.Host = "srv";
        vm.Database = "Sales";
        vm.User = "sa";
        vm.Password = "s3cret";
        vm.Folder = "Company";
        vm.Encrypt = true;
        vm.TrustServerCertificate = true;
        vm.ReadOnly = true;

        Assert.True(vm.TryConfirm());

        var cfg = vm.Result!;
        Assert.Equal(DbKind.SqlServer, cfg.Kind);
        Assert.Equal(1433, cfg.Port);
        Assert.Equal("Company", cfg.Folder);
        Assert.Equal("p:s3cret", cfg.ProtectedPassword);
        Assert.True(cfg.Encrypt && cfg.TrustServerCertificate && cfg.ReadOnly);

        var empty = New();
        Assert.False(empty.TryConfirm());
        Assert.NotNull(empty.Error);
        Assert.Null(empty.Result);
    }

    [Fact]
    public void Sqlite_ConfigDropsNetworkFields()
    {
        var vm = New();
        vm.Kind = DbKind.Postgres;
        vm.Host = "h";
        vm.User = "u";
        vm.Password = "p";
        vm.Kind = DbKind.Sqlite;
        vm.Name = "lite";
        vm.Database = @"C:\data\x.db";

        Assert.True(vm.TryConfirm());

        var cfg = vm.Result!;
        Assert.Null(cfg.Host);
        Assert.Null(cfg.Port);
        Assert.Null(cfg.User);
        Assert.Null(cfg.ProtectedPassword);
        Assert.False(cfg.Encrypt);
    }

    [Fact]
    public void Edit_KeepsIdAndExistingPassword_WhenPasswordLeftEmpty_AndReplacesWhenTyped()
    {
        var existing = TreeFixture.Conn("PG", "Admin", "db", DbKind.Postgres) with { Port = 5432, User = "u", ProtectedPassword = "p:old" };
        var vm = New(existing);

        Assert.Equal("PG", vm.Name);
        Assert.Equal("5432", vm.PortText);
        Assert.Equal("Admin", vm.Folder);
        Assert.Equal("", vm.Password);
        vm.Name = "PG renamed";
        Assert.True(vm.TryConfirm());
        Assert.Equal(existing.Id, vm.Result!.Id);
        Assert.Equal("p:old", vm.Result.ProtectedPassword);

        vm.Password = "new";
        Assert.True(vm.TryConfirm());
        Assert.Equal("p:new", vm.Result!.ProtectedPassword);
    }

    [Fact]
    public async Task TestConnection_Sqlite_ReportsSuccess()
    {
        var vm = New();
        vm.Kind = DbKind.Sqlite;
        vm.Name = "lite";
        vm.Database = _fx.CreateSqliteFile();

        await vm.TestConnectionAsync();

        Assert.True(vm.TestSucceeded);
        Assert.Contains("succe", vm.TestMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsTesting);
    }

    [Fact]
    public async Task TestConnection_Failure_ShowsExceptionMessageWithoutThrowing()
    {
        var vm = New();
        vm.Kind = DbKind.Sqlite;
        vm.Name = "lite";
        vm.Database = Path.Combine(_fx.Dir, "missing.db");

        await vm.TestConnectionAsync();

        Assert.False(vm.TestSucceeded);
        Assert.Contains("unable to open database file", vm.TestMessage); // the driver's own message
        Assert.DoesNotContain("succe", vm.TestMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsTesting);
    }

    [Fact]
    public async Task TestConnection_InvalidInput_ShowsValidationErrorInsteadOfConnecting()
    {
        var vm = New();
        vm.Kind = DbKind.Postgres; // no host/database
        vm.Name = "pg";

        await vm.TestConnectionAsync();

        Assert.False(vm.TestSucceeded);
        Assert.Null(vm.TestMessage);                       // a missing field is not a failed test: the field says what to fix
        Assert.Equal("Host is required.", vm.HostError);
        Assert.Equal("Database is required.", vm.DatabaseError);
        Assert.Null(vm.NameError);
        Assert.False(vm.IsTesting);
    }

    [Fact]
    public async Task TestConnection_DoesNotNeedAName_OrComplainAboutOne()
    {
        var vm = New(null, "lite");   // "lite" is taken: a save would refuse it, a test does not care
        vm.Kind = DbKind.Sqlite;
        vm.Name = "lite";
        vm.Database = _fx.CreateSqliteFile();

        await vm.TestConnectionAsync();

        Assert.True(vm.TestSucceeded);
        Assert.Contains("succe", vm.TestMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" ms", vm.TestMessage);
    }

    [Fact]
    public async Task EditingASetting_ClearsTheOldTestResult()
    {
        var vm = New();
        vm.Kind = DbKind.Sqlite;
        vm.Database = _fx.CreateSqliteFile();
        await vm.TestConnectionAsync();
        Assert.True(vm.HasTestMessage);

        vm.Database += "x";

        Assert.False(vm.HasTestMessage);
        Assert.False(vm.TestSucceeded);
    }

    [Fact]
    public void InlineErrors_ShowAfterTheFirstOk_ThenFollowTheInput()
    {
        var vm = New();
        Assert.Null(vm.NameError);
        Assert.Null(vm.HostError);

        Assert.False(vm.TryConfirm());
        Assert.Equal("Name is required.", vm.NameError);
        Assert.Equal("Host is required.", vm.HostError);
        Assert.Equal("Database is required.", vm.DatabaseError);
        Assert.False(vm.HasGeneralError);   // the field lines explain it: no banner

        vm.Host = "srv";
        Assert.Null(vm.HostError);
        vm.PortText = "99999";
        Assert.NotNull(vm.PortError);
        vm.PortText = "5432";
        Assert.Null(vm.PortError);
    }

    [Fact]
    public void ANameClash_ShowsAsItIsTyped_AnEmptyNameOnlyAfterOk()
    {
        var vm = New(null, "Billing");

        vm.Name = "";
        Assert.Null(vm.NameError);
        vm.Name = "billing";
        Assert.Equal("A connection named 'billing' already exists.", vm.NameError);
        vm.Name = "billing 2";
        Assert.Null(vm.NameError);
    }

    [Fact]
    public void ErrorOutsideAnyField_IsAGeneralError()
    {
        var vm = New();
        vm.Error = "Cannot open the file picker: boom";

        Assert.True(vm.HasGeneralError);
    }

    [Fact]
    public void TurningEncryptOff_AlsoTurnsTrustOff()
    {
        var vm = New();
        vm.Kind = DbKind.SqlServer;
        vm.Encrypt = true;
        vm.TrustServerCertificate = true;

        vm.Encrypt = false;

        Assert.False(vm.TrustServerCertificate);
    }

    [Fact]
    public void SavedPassword_IsKept_HintedAndRemovableOnRequest()
    {
        var existing = TreeFixture.Conn("PG", "Admin", "db", DbKind.Postgres) with { Port = 5432, User = "u", ProtectedPassword = "p:old" };
        var vm = New(existing);
        Assert.True(vm.HasSavedPassword);
        Assert.Contains("Saved", vm.PasswordHint);
        Assert.Equal("Save", vm.OkText);

        vm.ClearSavedPassword = true;
        Assert.True(vm.TryConfirm());
        Assert.Null(vm.Result!.ProtectedPassword);

        var fresh = New();
        Assert.False(fresh.HasSavedPassword);
        Assert.Equal("Optional", fresh.PasswordHint);
        Assert.Equal("Add connection", fresh.OkText);
    }
}


