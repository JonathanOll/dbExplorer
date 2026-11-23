using DbExplorer.App.ViewModels;
using DbExplorer.Core.Config;
using DbExplorer.Core.Metadata;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace DbExplorer.App.Tests;

internal sealed class PlainProtector : ISecretProtector
{
    public string Protect(string plain) => "p:" + plain;
    public string Unprotect(string protectedValue) => protectedValue[2..];
}

internal sealed class FakeDialogs : IDialogService
{
    public bool ConnectionDialogResult { get; set; } = true;
    public Action<ConnectionDialogViewModel>? OnConnectionDialog { get; set; }
    public string? PromptResult { get; set; }
    public bool ConfirmResult { get; set; } = true;

    public Task<bool> ShowConnectionDialogAsync(ConnectionDialogViewModel viewModel)
    {
        OnConnectionDialog?.Invoke(viewModel);
        // Like the real dialog: OK validates and builds the result.
        return Task.FromResult(ConnectionDialogResult && viewModel.TryConfirm());
    }

    public Task<string?> PromptTextAsync(string title, string label, string initial) => Task.FromResult(PromptResult);

    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(ConfirmResult);
}

/// <summary>A temp config dir with a real ConfigStore, SessionManager and tree view model.</summary>
internal sealed class TreeFixture : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "dbx-app-tests-" + Guid.NewGuid().ToString("N"));
    public string DBeaverDir => Path.Combine(Dir, "dbeaver");
    public string ConfigPath => Path.Combine(Dir, "config.json");
    public PlainProtector Protector { get; } = new();
    public ConfigStore Store { get; }
    public MetadataCache Cache { get; }
    public SessionManager Sessions { get; }
    public StatusModel Status { get; } = new();
    public FakeDialogs Dialogs { get; } = new();

    public TreeFixture()
    {
        Directory.CreateDirectory(Dir);
        Store = new ConfigStore(ConfigPath, Protector);
        Cache = new MetadataCache(Path.Combine(Dir, "cache"));
        Sessions = new SessionManager(Store, Cache, Protector);
    }

    public ConnectionTreeViewModel CreateTree(AppConfig config) =>
        new(Store, config, Sessions, Status, Dialogs, DBeaverDir);

    public string CreateSqliteFile(string name = "test.db")
    {
        var file = Path.Combine(Dir, name);
        using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT); CREATE VIEW v_users AS SELECT * FROM users;";
            cmd.ExecuteNonQuery();
        }

        return file;
    }

    public static ConnectionConfig Conn(string name, string? folder = null, string database = "x.db", DbKind kind = DbKind.Sqlite) =>
        new(Guid.NewGuid(), name, kind, folder, kind == DbKind.Sqlite ? null : "localhost", null, database, null, null, false, false, false);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        // A cancelled script run may still be releasing its connection on a pool thread (the tab returns at once on cancel):
        // give it a moment instead of failing the test in its teardown.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 200)
            {
                Thread.Sleep(50);
            }
        }
    }
}

