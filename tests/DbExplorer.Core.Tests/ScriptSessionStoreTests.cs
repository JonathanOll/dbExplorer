using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Tests;

public class ScriptSessionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-scripts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void SessionStore_RoundTrips_AndRestoresUnsavedText()
    {
        var store = new ScriptSessionStore(_dir);
        var tab = Guid.NewGuid();
        var conn = Guid.NewGuid();
        const string text = "SELECT 'é;日本' AS x;\r\n-- comment\n\tSELECT 2";

        store.Save(tab, conn, text, null);

        // A fresh instance on the same folder (= the next app start) sees it.
        var loaded = Assert.Single(new ScriptSessionStore(_dir).LoadAll());
        Assert.Equal(tab, loaded.TabId);
        Assert.Equal(conn, loaded.ConnectionId);
        Assert.Equal(text, loaded.Text);
        Assert.Null(loaded.FilePath);
    }

    [Fact]
    public void Save_KeepsFilePath_AndOverwritesTheSameTab()
    {
        var store = new ScriptSessionStore(_dir);
        var tab = Guid.NewGuid();
        var conn = Guid.NewGuid();

        store.Save(tab, conn, "v1", @"C:\scripts\a.sql");
        store.Save(tab, conn, "v2", @"C:\scripts\a.sql");

        var loaded = Assert.Single(store.LoadAll());
        Assert.Equal("v2", loaded.Text);
        Assert.Equal(@"C:\scripts\a.sql", loaded.FilePath);
    }

    [Fact]
    public void Delete_RemovesTheScript_AndIgnoresUnknownIds()
    {
        var store = new ScriptSessionStore(_dir);
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        store.Save(keep, Guid.NewGuid(), "keep", null);
        store.Save(drop, Guid.NewGuid(), "drop", null);

        store.Delete(drop);
        store.Delete(Guid.NewGuid());

        Assert.Equal(keep, Assert.Single(store.LoadAll()).TabId);
    }

    [Fact]
    public void LoadAll_WithNoFolder_IsEmpty()
    {
        Assert.Empty(new ScriptSessionStore(Path.Combine(_dir, "missing")).LoadAll());
    }

    [Fact]
    public void LoadAll_SkipsCorruptFiles_WithoutDeletingThem()
    {
        var store = new ScriptSessionStore(_dir);
        var good = Guid.NewGuid();
        store.Save(good, Guid.NewGuid(), "ok", null);
        var corrupt = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(corrupt, "{ not json");

        Assert.Equal(good, Assert.Single(store.LoadAll()).TabId);
        Assert.True(File.Exists(corrupt));
    }

    [Fact]
    public void Save_LeavesNoTemporaryFilesBehind()
    {
        var store = new ScriptSessionStore(_dir);
        var tab = Guid.NewGuid();
        for (var i = 0; i < 5; i++) store.Save(tab, Guid.NewGuid(), "v" + i, null);

        Assert.Single(Directory.GetFiles(_dir));
    }
}
