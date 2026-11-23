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
    public string PasswordHint => IsEdit ? "Leave empty to keep the saved password" : "";

    [ObservableProperty] private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSqlite), nameof(IsNetwork), nameof(IsSqlServer), nameof(DatabaseLabel))]
    private DbKind _kind = DbKind.Postgres;

    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _portText = "5432";
    [ObservableProperty] private string _database = "";
    [ObservableProperty] private string _user = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFolderChoice))]
    private string? _folder;
    [ObservableProperty] private bool _encrypt;
    [ObservableProperty] private bool _trustServerCertificate;
    [ObservableProperty] private bool _readOnly;
    [ObservableProperty] private string? _error;

    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private bool _testSucceeded;
    [ObservableProperty] private string? _testMessage;

    public ConnectionConfig? Result { get; private set; }

    public bool IsSqlite => Kind == DbKind.Sqlite;
    public bool IsNetwork => Kind != DbKind.Sqlite;
    public bool IsSqlServer => Kind == DbKind.SqlServer;
    public string DatabaseLabel => IsSqlite ? "Database file" : "Database";

    partial void OnKindChanging(DbKind oldValue, DbKind newValue)
    {
        // Follow the default port of the kind unless the user typed a custom one.
        var oldDefault = DefaultPorts[oldValue]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(PortText) || PortText == oldDefault)
            PortText = DefaultPorts[newValue]?.ToString() ?? "";
    }

    /// <summary>Returns the first problem with the current input, or null when it is valid.</summary>
    public string? Validate()
    {
        var name = Name.Trim();
        if (name.Length == 0) return "Name is required.";
        if (_takenNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            return $"A connection named '{name}' already exists.";
        if (IsNetwork && string.IsNullOrWhiteSpace(Host)) return "Host is required.";
        if (string.IsNullOrWhiteSpace(Database)) return IsSqlite ? "Database file is required." : "Database is required.";
        if (IsNetwork && !string.IsNullOrWhiteSpace(PortText) && !(int.TryParse(PortText, out var port) && port is > 0 and <= 65535))
            return "Port must be a number between 1 and 65535.";
        return null;
    }

    public bool TryConfirm()
    {
        Error = Validate();
        if (Error is not null) return false;
        Result = BuildConfig(ProtectedPasswordForSave());
        return true;
    }

    [RelayCommand]
    public async Task TestConnectionAsync()
    {
        var problem = Validate();
        if (problem is not null)
        {
            TestSucceeded = false;
            TestMessage = problem;
            return;
        }

        var plain = Password.Length > 0 ? Password : ExistingPlainPassword();
        _testCts?.Dispose();
        _testCts = new CancellationTokenSource();
        IsTesting = true;
        TestMessage = "Testing connection...";
        TestSucceeded = false;
        try
        {
            await SessionManager.TestConnectionAsync(BuildConfig(null), plain, _testCts.Token);
            TestSucceeded = true;
            TestMessage = "Connection succeeded.";
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
        return Password.Length > 0 ? _protector.Protect(Password) : _existing?.ProtectedPassword;
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


