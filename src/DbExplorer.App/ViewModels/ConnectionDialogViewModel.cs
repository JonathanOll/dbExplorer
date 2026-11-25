using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Config;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.ViewModels;

/// <summary>Edits one connection. <see cref="Result"/> is set by <see cref="TryConfirm"/>.</summary>
public partial class ConnectionDialogViewModel : ObservableObject
{
    private static readonly Dictionary<DbKind, int?> DefaultPorts = new()
    {
        [DbKind.Postgres] = 5432,
        [DbKind.SqlServer] = 1433,
        [DbKind.Sqlite] = null,
    };

    private readonly ISecretProtector _protector;
    private readonly IReadOnlyCollection<string> _takenNames;
    private readonly ConnectionConfig? _existing;
    private CancellationTokenSource? _testCts;

    // Which inline messages are due: after the first OK / Test they follow the input; a name clash always shows at once.
    private bool _showNameErrors;
    private bool _showConnectionErrors;

    public ConnectionDialogViewModel(
        ISecretProtector protector, IReadOnlyList<string> folders, IReadOnlyCollection<string> takenNames,
        ConnectionConfig? existing = null)
    {
        _protector = protector;
        _takenNames = takenNames;
        _existing = existing;
        FolderChoices = [NoFolderLabel, .. folders];
        if (existing is null) return;

        Name = existing.Name;
        Kind = existing.Kind;
        Host = existing.Host ?? "";
        PortText = existing.Port?.ToString() ?? "";
        Database = existing.Database;
        User = existing.User ?? "";
        Folder = existing.Folder;
        Encrypt = existing.Encrypt;
        TrustServerCertificate = existing.TrustServerCertificate;
        ReadOnly = existing.ReadOnly;
    }

    public IReadOnlyList<DbKind> Kinds { get; } = [DbKind.Postgres, DbKind.SqlServer, DbKind.Sqlite];

    public const string NoFolderLabel = "(no folder)";

    /// <summary>Entries of the folder drop-down: "(no folder)" followed by the folder names.</summary>
    public IReadOnlyList<string> FolderChoices { get; }

    /// <summary>Bridge between the drop-down (which cannot select null) and <see cref="Folder"/> (null = tree root).</summary>
    public string SelectedFolderChoice
    {
        get => Folder ?? NoFolderLabel;
        set => Folder = value == NoFolderLabel ? null : value;
    }

    public bool IsEdit => _existing is not null;
    public string Title => IsEdit ? "Edit connection" : "New connection";

    /// <summary>The primary button: a verb rather than "OK".</summary>
    public string OkText => IsEdit ? "Save" : "Add connection";

    /// <summary>The connection has a saved password that stays unless a new one is typed (or its removal is ticked).</summary>
    public bool HasSavedPassword => !string.IsNullOrEmpty(_existing?.ProtectedPassword);

    public string PasswordHint => HasSavedPassword && !ClearSavedPassword ? "Saved. Type to replace it" : "Optional";

    [ObservableProperty] private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSqlite), nameof(IsNetwork), nameof(IsSqlServer), nameof(DatabaseLabel), nameof(DatabasePlaceholder))]
    private DbKind _kind = DbKind.Postgres;

    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _portText = "5432";
    [ObservableProperty] private string _database = "";
    [ObservableProperty] private string _user = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordHint))]
    private string _password = "";

    /// <summary>Remove the saved password on save (offered only when there is one and nothing new is typed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordHint))]
    private bool _clearSavedPassword;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFolderChoice))]
    private string? _folder;
    [ObservableProperty] private bool _encrypt;
    [ObservableProperty] private bool _trustServerCertificate;
    [ObservableProperty] private bool _readOnly;

    /// <summary>The first problem found by <see cref="TryConfirm"/>, or a failure that belongs to no field (the file picker).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGeneralError))]
    private string? _error;

    // One inline message per field (null = fine), shown under the field.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasGeneralError))] private string? _nameError;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasGeneralError))] private string? _hostError;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasGeneralError))] private string? _portError;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasGeneralError))] private string? _databaseError;

    /// <summary>An error that no field line explains (the banner shows it).</summary>
    public bool HasGeneralError =>
        Error is not null && NameError is null && HostError is null && PortError is null && DatabaseError is null;

    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private bool _testSucceeded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestMessage))]
    private string? _testMessage;

    public bool HasTestMessage => !string.IsNullOrEmpty(TestMessage);

    public ConnectionConfig? Result { get; private set; }

    public bool IsSqlite => Kind == DbKind.Sqlite;
    public bool IsNetwork => Kind != DbKind.Sqlite;
    public bool IsSqlServer => Kind == DbKind.SqlServer;
    public string DatabaseLabel => IsSqlite ? "Database file" : "Database";
    public string DatabasePlaceholder => IsSqlite ? @"C:\data\my.db" : "";

    partial void OnKindChanging(DbKind oldValue, DbKind newValue)
    {
        // Follow the default port of the kind unless the user typed a custom one.
        var oldDefault = DefaultPorts[oldValue]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(PortText) || PortText == oldDefault)
            PortText = DefaultPorts[newValue]?.ToString() ?? "";
    }

    partial void OnKindChanged(DbKind value) { ResetTest(); RefreshErrors(); }
    partial void OnNameChanged(string value) => RefreshErrors();
    partial void OnHostChanged(string value) { ResetTest(); RefreshErrors(); }
    partial void OnPortTextChanged(string value) { ResetTest(); RefreshErrors(); }
    partial void OnDatabaseChanged(string value) { ResetTest(); RefreshErrors(); }
    partial void OnUserChanged(string value) => ResetTest();
    partial void OnPasswordChanged(string value) => ResetTest();
    partial void OnClearSavedPasswordChanged(bool value) => ResetTest();
    partial void OnTrustServerCertificateChanged(bool value) => ResetTest();

    partial void OnEncryptChanged(bool value)
    {
        ResetTest();
        if (!value) TrustServerCertificate = false;   // trusting a certificate means nothing without encryption
    }

    /// <summary>A test result must not stay on screen once the settings it described changed.</summary>
    private void ResetTest()
    {
        if (IsTesting) return;
        TestMessage = null;
        TestSucceeded = false;
    }

    private string? NameProblem(bool includeEmpty)
    {
        var name = Name.Trim();
        if (name.Length == 0) return includeEmpty ? "Name is required." : null;
        return _takenNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            ? $"A connection named '{name}' already exists."
            : null;
    }

    private string? HostProblem() => IsNetwork && string.IsNullOrWhiteSpace(Host) ? "Host is required." : null;

    private string? DatabaseProblem() =>
        string.IsNullOrWhiteSpace(Database) ? (IsSqlite ? "Database file is required." : "Database is required.") : null;

    private string? PortProblem() =>
        IsNetwork && !string.IsNullOrWhiteSpace(PortText) && !(int.TryParse(PortText, out var port) && port is > 0 and <= 65535)
            ? "Port must be a number between 1 and 65535."
            : null;

    /// <summary>Recomputes the inline messages that are due.</summary>
    private void RefreshErrors()
    {
        NameError = NameProblem(includeEmpty: _showNameErrors);
        HostError = _showConnectionErrors ? HostProblem() : null;
        DatabaseError = _showConnectionErrors ? DatabaseProblem() : null;
        PortError = _showConnectionErrors ? PortProblem() : null;
        if (Error is not null && NameError is null && HostError is null && DatabaseError is null && PortError is null) Error = null;
    }

    /// <summary>Returns the first problem with the current input, or null when it is valid.</summary>
    public string? Validate() => NameProblem(includeEmpty: true) ?? HostProblem() ?? DatabaseProblem() ?? PortProblem();

    public bool TryConfirm()
    {
        _showNameErrors = _showConnectionErrors = true;
        RefreshErrors();
        Error = Validate();
        if (Error is not null) return false;
        Result = BuildConfig(ProtectedPasswordForSave());
        return true;
    }

    [RelayCommand]
    public async Task TestConnectionAsync()
    {
        // A test needs the connection fields only: the name (and a clash with another connection) is not its business.
        _showConnectionErrors = true;
        RefreshErrors();
        if ((HostProblem() ?? DatabaseProblem() ?? PortProblem()) is not null)
        {
            TestSucceeded = false;
            TestMessage = null;   // the field lines say what to fix
            return;
        }

        var plain = Password.Length > 0 ? Password : ClearSavedPassword ? null : ExistingPlainPassword();
        _testCts?.Dispose();
        _testCts = new CancellationTokenSource();
        IsTesting = true;
        TestMessage = "Testing connection…";
        TestSucceeded = false;
        var watch = Stopwatch.StartNew();
        try
        {
            await SessionManager.TestConnectionAsync(BuildConfig(null), plain, _testCts.Token);
            TestSucceeded = true;
            TestMessage = $"Connection succeeded in {watch.ElapsedMilliseconds} ms.";
        }
        catch (OperationCanceledException)
        {
            TestMessage = "Test cancelled.";
        }
        catch (Exception e)
        {
            TestMessage = ErrorText.Describe(e);
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void CancelTest() => _testCts?.Cancel();

    private string? ExistingPlainPassword()
    {
        if (string.IsNullOrEmpty(_existing?.ProtectedPassword)) return null;
        try
        {
            return _protector.Unprotect(_existing.ProtectedPassword);
        }
        catch (Exception)
        {
            return null; // undecryptable (other Windows user): the test then fails with the server's own message
        }
    }

    private string? ProtectedPasswordForSave()
    {
        if (IsSqlite) return null;
        if (Password.Length > 0) return _protector.Protect(Password);
        return ClearSavedPassword ? null : _existing?.ProtectedPassword;
    }

    private ConnectionConfig BuildConfig(string? protectedPassword)
    {
        var network = IsNetwork;
        return new ConnectionConfig(
            _existing?.Id ?? Guid.NewGuid(),
            Name.Trim(),
            Kind,
            string.IsNullOrWhiteSpace(Folder) ? null : Folder,
            network ? Host.Trim() : null,
            network && int.TryParse(PortText, out var port) ? port : null,
            Database.Trim(),
            network && !string.IsNullOrWhiteSpace(User) ? User.Trim() : null,
            protectedPassword,
            Encrypt: IsSqlServer && Encrypt,
            TrustServerCertificate: IsSqlServer && TrustServerCertificate,
            ReadOnly);
    }
}
