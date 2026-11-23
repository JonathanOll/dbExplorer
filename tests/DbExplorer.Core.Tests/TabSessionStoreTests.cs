using DbExplorer.Core.Config;

namespace DbExplorer.Core.Tests;

public class TabSessionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-tabsession-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "session.json");

    public TabSessionStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static SavedTabs Sample(Guid a, Guid b) => new(1,
    [
        new SavedTab(SavedTabKind.Table, a, "dbo", "Orders",
            Filters: new() { ["Status"] = "=A", ["Total"] = ">10" }, RawWhere: "id > 3", SortColumn: "Total", SortDescending: true),
        new SavedTab(SavedTabKind.Script, b, ScriptId: Guid.NewGuid(), FilePath: @"C:\tmp\x.sql"),
        new SavedTab(SavedTabKind.Table, b, null, "users"),
    ]);

    [Fact]
    public void Save_ThenLoad_RoundTripsEverythingInOrder()
    {
        var store = new TabSessionStore(File_);
        var saved = Sample(Guid.NewGuid(), Guid.NewGuid());

        Assert.True(store.Save(saved));
        var loaded = store.Load();

        Assert.Null(loaded.Notice);
        var session = Assert.IsType<SavedTabs>(loaded.Session);
        Assert.Equal(1, session.ActiveIndex);
        Assert.Equal(saved.Tabs.Select(t => (t.Kind, t.ConnectionId, t.Schema, t.Table, t.RawWhere, t.SortColumn, t.SortDescending, t.ScriptId, t.FilePath)),
            session.Tabs.Select(t => (t.Kind, t.ConnectionId, t.Schema, t.Table, t.RawWhere, t.SortColumn, t.SortDescending, t.ScriptId, t.FilePath)));
        Assert.Equal(saved.Tabs[0].Filters, session.Tabs[0].Filters);
    }

    [Fact]
    public void Load_MissingFile_IsNoSession_AndNoNotice()
    {
        var loaded = new TabSessionStore(File_).Load();

        Assert.Null(loaded.Session);
        Assert.Null(loaded.Notice);
    }

    [Fact]
    public void Load_EmptySession_IsASession_NotMissing()
    {
        var store = new TabSessionStore(File_);
        store.Save(new SavedTabs(-1, []));

        var loaded = store.Load();

        Assert.NotNull(loaded.Session);
        Assert.Empty(loaded.Session!.Tabs);
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{ "ActiveIndex": 0, "Tabs": null }""")]
    [InlineData("""{ "ActiveIndex": 0, "Tabs": [null] }""")]
    [InlineData("""{ "ActiveIndex": 0, "Tabs": [{ "Kind": "Nope", "ConnectionId": "00000000-0000-0000-0000-000000000001" }] }""")]
    public void Load_CorruptFile_IsMovedAside_NoSession_WithANotice(string content)
    {
        File.WriteAllText(File_, content);

        var loaded = new TabSessionStore(File_).Load();

        Assert.Null(loaded.Session);
        Assert.Contains("session.json", loaded.Notice);
        Assert.False(File.Exists(File_));
        Assert.Equal(content, File.ReadAllText(Assert.Single(Directory.GetFiles(_dir, "session.json.bad-*"))));
    }

    [Fact]
    public void Load_LockedFile_IsNoSession_AndLeftInPlace()
    {
        File.WriteAllText(File_, "{ }");
        using var locked = new FileStream(File_, FileMode.Open, FileAccess.Read, FileShare.None);

        var loaded = new TabSessionStore(File_).Load();

        Assert.Null(loaded.Session);
        Assert.Empty(Directory.GetFiles(_dir, "session.json.bad-*"));
    }

    [Fact]
    public void Save_NeverThrows_AndReportsFailure()
    {
        File.WriteAllText(File_, "{ }");
        using var locked = new FileStream(File_, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.False(new TabSessionStore(File_).Save(new SavedTabs(-1, [])));
        Assert.False(new TabSessionStore(Path.Combine(_dir, "a\0b", "session.json")).Save(new SavedTabs(-1, [])));
    }

    [Fact]
    public void Save_IsAtomic_NoTempFileLeftBehind_AndCreatesTheFolder()
    {
        var nested = Path.Combine(_dir, "new", "session.json");

        Assert.True(new TabSessionStore(nested).Save(Sample(Guid.NewGuid(), Guid.NewGuid())));
        Assert.True(new TabSessionStore(nested).Save(Sample(Guid.NewGuid(), Guid.NewGuid())));   // overwrite

        Assert.Equal(["session.json"], Directory.GetFiles(Path.GetDirectoryName(nested)!).Select(Path.GetFileName));
    }
}
