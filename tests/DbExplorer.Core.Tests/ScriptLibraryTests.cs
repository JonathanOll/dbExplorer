using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Tests;

public sealed class ScriptLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-lib-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptLibrary _lib;

    public ScriptLibraryTests() => _lib = new ScriptLibrary(Path.Combine(_dir, "library"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void List_FoldersFirstThenScripts_NaturalOrder_IgnoresOtherFiles()
    {
        _lib.EnsureRoot();
        _lib.CreateFolder(null, "b");
        _lib.CreateFolder(null, "a");
        _lib.SaveNew(null, "script 10", "x");
        _lib.SaveNew(null, "script 2", "x");
        File.WriteAllText(Path.Combine(_lib.Root, "notes.txt"), "no");

        var names = _lib.List().Select(e => e.Name).ToArray();

        Assert.Equal(["a", "b", "script 2.sql", "script 10.sql"], names);
    }

    [Fact]
    public void List_MissingRoot_IsEmpty() => Assert.Empty(_lib.List());

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" x")]
    [InlineData("x ")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData("..\\evil")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("CON")]
    [InlineData("nul.sql")]
    [InlineData("name.")]
    public void ValidateName_RefusesBadNames(string name) => Assert.NotNull(ScriptLibrary.ValidateName(name));

    [Theory]
    [InlineData("report")]
    [InlineData("report (2)")]
    [InlineData("Rapport été")]
    [InlineData("a.b.sql")]
    public void ValidateName_AcceptsGoodNames(string name) => Assert.Null(ScriptLibrary.ValidateName(name));

    [Fact]
    public void CreateFolder_TraversalName_Refused()
    {
        _lib.EnsureRoot();
        var e = Assert.Throws<ScriptLibraryException>(() => _lib.CreateFolder(null, "..\\outside"));
        Assert.NotEmpty(e.Message);
        Assert.False(Directory.Exists(Path.Combine(_dir, "outside")));
    }

    [Fact]
    public void Operations_OnAPathOutsideTheRoot_Refused()
    {
        _lib.EnsureRoot();
        var outside = Path.Combine(_dir, "outside.sql");
        File.WriteAllText(outside, "keep");
        var sibling = Path.Combine(_dir, "library-evil");   // shares the prefix of the root name
        Directory.CreateDirectory(sibling);

        Assert.Throws<ScriptLibraryException>(() => _lib.Delete(outside));
        Assert.Throws<ScriptLibraryException>(() => _lib.Rename(outside, "x"));
        Assert.Throws<ScriptLibraryException>(() => _lib.Move(outside, null));
        Assert.Throws<ScriptLibraryException>(() => _lib.Duplicate(outside));
        Assert.Throws<ScriptLibraryException>(() => _lib.CreateFolder(sibling, "x"));
        Assert.Throws<ScriptLibraryException>(() => _lib.SaveNew(Path.Combine(_lib.Root, "..", "library-evil"), "x", ""));
        Assert.Throws<ScriptLibraryException>(() => _lib.List(sibling));
        Assert.True(File.Exists(outside));
        Assert.False(_lib.Contains(sibling));
        Assert.True(_lib.Contains(Path.Combine(_lib.Root, "a", "b.sql")));
    }

    [Fact]
    public void Rename_ScriptKeepsExtension_AndRefusesExistingName()
    {
        var a = _lib.SaveNew(null, "a", "1");
        _lib.SaveNew(null, "b", "2");

        var renamed = _lib.Rename(a, "c");

        Assert.Equal(Path.Combine(_lib.Root, "c.sql"), renamed);
        Assert.False(File.Exists(a));
        var e = Assert.Throws<ScriptLibraryException>(() => _lib.Rename(renamed, "b"));
        Assert.Contains("already exists", e.Message);
        Assert.Equal("1", File.ReadAllText(renamed));
    }

    [Fact]
    public void Rename_CaseOnlyChange_Allowed()
    {
        var a = _lib.SaveNew(null, "alpha", "1");
        var renamed = _lib.Rename(a, "Alpha");
        Assert.Equal("Alpha.sql", Path.GetFileName(Directory.GetFiles(_lib.Root).Single()));
        Assert.Equal(Path.Combine(_lib.Root, "Alpha.sql"), renamed);
    }

    [Fact]
    public void Rename_Folder_KeepsContent()
    {
        var f = _lib.CreateFolder(null, "old");
        _lib.SaveNew(f, "s", "x");
        var renamed = _lib.Rename(f, "new");
        Assert.True(File.Exists(Path.Combine(renamed, "s.sql")));
    }

    [Fact]
    public void SaveNew_SameNameTwice_GetsNumberedNames_NothingOverwritten()
    {
        var first = _lib.SaveNew(null, "q", "one");
        var second = _lib.SaveNew(null, "q", "two");
        var third = _lib.SaveNew(null, "q.sql", "three");

        Assert.Equal("q.sql", Path.GetFileName(first));
        Assert.Equal("q (2).sql", Path.GetFileName(second));
        Assert.Equal("q (3).sql", Path.GetFileName(third));
        Assert.Equal("one", File.ReadAllText(first));
        Assert.Equal("two", File.ReadAllText(second));
        Assert.Equal("q (4).sql", _lib.PreviewNewName(null, "q"));
    }

    [Fact]
    public void SaveNew_BadName_Refused_AndNothingCreated()
    {
        Assert.Throws<ScriptLibraryException>(() => _lib.SaveNew(null, "..\\x", "t"));
        Assert.Empty(_lib.List());
    }

    [Fact]
    public void Move_ScriptIntoFolder_AndRefusesOverwrite()
    {
        var folder = _lib.CreateFolder(null, "f");
        var script = _lib.SaveNew(null, "s", "root");
        var other = _lib.SaveNew(folder, "s", "inside");

        var e = Assert.Throws<ScriptLibraryException>(() => _lib.Move(script, folder));
        Assert.Contains("already exists", e.Message);
        Assert.Equal("inside", File.ReadAllText(other));
        Assert.True(File.Exists(script));

        File.Delete(other);
        var moved = _lib.Move(script, folder);
        Assert.Equal(Path.Combine(folder, "s.sql"), moved);
        Assert.Equal("root", File.ReadAllText(moved));
    }

    [Fact]
    public void Move_ToTopLevel_AndToSameFolder()
    {
        var folder = _lib.CreateFolder(null, "f");
        var script = _lib.SaveNew(folder, "s", "x");
        Assert.Equal(script, _lib.Move(script, folder));   // already there: nothing happens
        var moved = _lib.Move(script, null);
        Assert.Equal(Path.Combine(_lib.Root, "s.sql"), moved);
    }

    [Fact]
    public void Move_FolderIntoItselfOrDescendant_Refused()
    {
        var a = _lib.CreateFolder(null, "a");
        var b = _lib.CreateFolder(a, "b");
        var c = _lib.CreateFolder(b, "c");

        Assert.Throws<ScriptLibraryException>(() => _lib.Move(a, a));
        Assert.Throws<ScriptLibraryException>(() => _lib.Move(a, b));
        Assert.Throws<ScriptLibraryException>(() => _lib.Move(a, c));
        Assert.True(Directory.Exists(c));
        // a sibling that only shares a name prefix is not a descendant
        var a2 = _lib.CreateFolder(null, "a2");
        Assert.Equal(Path.Combine(a2, "a"), _lib.Move(a, a2));
    }

    [Fact]
    public void Move_RootItself_Refused() =>
        Assert.Throws<ScriptLibraryException>(() => { _lib.EnsureRoot(); _lib.Move(_lib.Root, null); });

    [Fact]
    public void Delete_Script_AndFolderOnlyRecursively()
    {
        var folder = _lib.CreateFolder(null, "f");
        var script = _lib.SaveNew(folder, "s", "x");

        var e = Assert.Throws<ScriptLibraryException>(() => _lib.Delete(folder));
        Assert.Contains("not empty", e.Message);
        Assert.True(File.Exists(script));
        Assert.Equal(1, _lib.CountScripts(folder));

        _lib.Delete(script);
        Assert.False(File.Exists(script));
        _lib.Delete(folder);   // empty now
        Assert.False(Directory.Exists(folder));

        var f2 = _lib.CreateFolder(null, "g");
        _lib.SaveNew(_lib.CreateFolder(f2, "deep"), "s", "x");
        _lib.Delete(f2, recursive: true);
        Assert.False(Directory.Exists(f2));
    }

    [Fact]
    public void Delete_RootOrMissing_Refused()
    {
        _lib.EnsureRoot();
        Assert.Throws<ScriptLibraryException>(() => _lib.Delete(_lib.Root, recursive: true));
        Assert.Throws<ScriptLibraryException>(() => _lib.Delete(Path.Combine(_lib.Root, "nope.sql")));
        Assert.True(Directory.Exists(_lib.Root));
    }

    [Fact]
    public void Duplicate_MakesACopyWithANumberedName()
    {
        var s = _lib.SaveNew(null, "orig", "select 1;");
        var copy = _lib.Duplicate(s);
        Assert.Equal("orig (2).sql", Path.GetFileName(copy));
        Assert.Equal("select 1;", File.ReadAllText(copy));
        Assert.Equal("orig (3).sql", Path.GetFileName(_lib.Duplicate(s)));
    }

    [Fact]
    public void AllFolders_ListsNestedFoldersWithDepth()
    {
        var a = _lib.CreateFolder(null, "a");
        _lib.CreateFolder(a, "b");
        _lib.CreateFolder(null, "c");

        var folders = _lib.AllFolders();

        Assert.Equal(["a", "a/b", "c"], folders.Select(f => f.RelativeName).ToArray());
        Assert.Equal([0, 1, 0], folders.Select(f => f.Depth).ToArray());
    }

    [Fact]
    public void ReserveNew_InAFile_Refused()
    {
        var s = _lib.SaveNew(null, "s", "x");
        Assert.Throws<ScriptLibraryException>(() => _lib.ReserveNew(s, "y"));
    }
}
