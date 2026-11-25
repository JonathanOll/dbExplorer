using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Tests;

public sealed class RecentScriptsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-recent-" + Guid.NewGuid().ToString("N"));
    private string JsonFile => Path.Combine(_dir, "recent-scripts.json");

    public RecentScriptsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Script(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "select 1;");
        return path;
    }

    [Fact]
    public void Add_MostRecentFirst_NoDuplicates_CaseInsensitive()
    {
        var recent = new RecentScripts(JsonFile);
        var a = Script("a.sql");
        var b = Script("b.sql");

        recent.Add(a);
        recent.Add(b);
        recent.Add(Path.Combine(_dir, "A.SQL"));   // same file, other case: moves up, not duplicated

        Assert.Equal(2, recent.Items.Count);
        Assert.Equal("a.sql", Path.GetFileName(recent.Items[0]), ignoreCase: true);
        Assert.Equal(b, recent.Items[1]);
    }

    [Fact]
    public void Add_CapsAtTwenty_DroppingTheOldest()
    {
        var recent = new RecentScripts(JsonFile);
        var paths = Enumerable.Range(1, 25).Select(i => Script($"s{i}.sql")).ToList();
        foreach (var p in paths) recent.Add(p);

        var items = recent.Items;

        Assert.Equal(RecentScripts.Max, items.Count);
        Assert.Equal(paths[24], items[0]);
        Assert.Equal(paths[5], items[19]);
        Assert.DoesNotContain(paths[0], items);
    }

    [Fact]
    public void Items_DropsMissingFiles_AndRewritesTheFile()
    {
        var recent = new RecentScripts(JsonFile);
        var keep = Script("keep.sql");
        var gone = Script("gone.sql");
        recent.Add(gone);
        recent.Add(keep);
        File.Delete(gone);

        Assert.Equal([keep], recent.Items.ToArray());
        Assert.DoesNotContain("gone.sql", File.ReadAllText(JsonFile));
    }

    [Fact]
    public void ANewInstance_ReadsWhatWasSaved()
    {
        var a = Script("a.sql");
        new RecentScripts(JsonFile).Add(a);

        Assert.Equal([a], new RecentScripts(JsonFile).Items.ToArray());
    }

    [Fact]
    public void CorruptFile_IsIgnored_NotDeleted_AndReplacedByTheNextChange()
    {
        File.WriteAllText(JsonFile, "{ not json [[[");
        var recent = new RecentScripts(JsonFile);

        Assert.Empty(recent.Items);
        Assert.Equal("{ not json [[[", File.ReadAllText(JsonFile));

        var a = Script("a.sql");
        recent.Add(a);
        Assert.Equal([a], new RecentScripts(JsonFile).Items.ToArray());
    }

    [Fact]
    public void WrongJsonShape_IsIgnored()
    {
        File.WriteAllText(JsonFile, "{\"a\":1}");
        Assert.Empty(new RecentScripts(JsonFile).Items);
    }

    [Fact]
    public void Add_WritesAtomically_NoTempFileLeft()
    {
        var recent = new RecentScripts(JsonFile);
        recent.Add(Script("a.sql"));
        Assert.True(File.Exists(JsonFile));
        Assert.False(File.Exists(JsonFile + ".tmp"));
    }

    [Fact]
    public void Remove_AndClear()
    {
        var recent = new RecentScripts(JsonFile);
        var a = Script("a.sql");
        var b = Script("b.sql");
        recent.Add(a);
        recent.Add(b);

        recent.Remove(a);
        Assert.Equal([b], recent.Items.ToArray());
        recent.Clear();
        Assert.Empty(recent.Items);
        Assert.Empty(new RecentScripts(JsonFile).Items);
    }

    [Fact]
    public void UnwritableLocation_NeverThrows()
    {
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "a file where a folder is needed");
        var recent = new RecentScripts(Path.Combine(blocker, "recent.json"));
        var a = Script("a.sql");

        recent.Add(a);   // cannot be saved
        Assert.Equal([a], recent.Items.ToArray());   // the in-memory list still works
    }
}
