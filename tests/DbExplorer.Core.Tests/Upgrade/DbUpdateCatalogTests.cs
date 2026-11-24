using DbExplorer.Core.Upgrade;

namespace DbExplorer.Core.Tests.Upgrade;

public class DbUpdateCatalogTests
{
    private static DbUpdateFolder Folder(DbUpdateCatalog c, string name) => c.Folders.Single(f => f.Name == name);

    [Fact]
    public void Scan_ListsOnlyVersionFolders_OrderedByVersionThenName()
    {
        using var t = TempDbUpdateTree.Standard();
        var names = DbUpdateCatalog.Scan(t.Root).Folders.Select(f => f.Name).ToList();

        Assert.Equal(new[]
        {
            "v0002-sprint 23", "v0003-sprint-23-hotfix", "v0013-sprint-ocr",
            "V0121-change brandvariant", "v0122-Hotfix-Trads",
            "v0691-hotfix Deny var on payment accountant",
            "v0710-sprint 128", "v0710-sprint 129", "v0720-sprint 129",
            "v0860-sprint 143", "v0861-sprint 143", "v0862-sprint 143",
        }, names);
        Assert.DoesNotContain("_views", names);
    }

    [Fact]
    public void Scan_ExposesCodeAndVersion()
    {
        using var t = TempDbUpdateTree.Standard();
        var c = DbUpdateCatalog.Scan(t.Root);
        var f = Folder(c, "v0860-sprint 143");
        Assert.Equal("v0860", f.Code);
        Assert.Equal(860, f.Version);
        Assert.Equal(121, Folder(c, "V0121-change brandvariant").Version);
        Assert.Equal("V0121", Folder(c, "V0121-change brandvariant").Code);
        Assert.Equal(Path.Combine(t.Root, "v0860-sprint 143"), f.FullPath);
        Assert.Equal(t.Root, c.Root);
    }

    [Fact]
    public void Scan_RootLevelScripts_AreNotListedAnywhere()
    {
        using var t = TempDbUpdateTree.Standard();
        var c = DbUpdateCatalog.Scan(t.Root);
        Assert.DoesNotContain(c.Folders.SelectMany(f => f.Scripts), s => s.FileName.StartsWith("cmi-01"));
        Assert.DoesNotContain(c.Folders.SelectMany(f => f.Scripts), s => s.FileName == "IndexDetails.sql");
    }

    [Fact]
    public void Sprint_ParsedFromName()
    {
        using var t = TempDbUpdateTree.Standard();
        t.Add("v0009-sprint 28", "a - 001 - x.sql");
        t.Add("v0011-Sprint   30", "a - 001 - x.sql");
        t.Add("v0012-sprint30", "a - 001 - x.sql");
        var c = DbUpdateCatalog.Scan(t.Root);

        Assert.Equal(143, Folder(c, "v0860-sprint 143").Sprint);
        Assert.Equal(23, Folder(c, "v0003-sprint-23-hotfix").Sprint);
        Assert.Equal(28, Folder(c, "v0009-sprint 28").Sprint);
        Assert.Equal(30, Folder(c, "v0011-Sprint   30").Sprint);
        Assert.Equal(30, Folder(c, "v0012-sprint30").Sprint);
        Assert.Null(Folder(c, "v0013-sprint-ocr").Sprint);
        Assert.Null(Folder(c, "v0122-Hotfix-Trads").Sprint);
        Assert.Null(Folder(c, "V0121-change brandvariant").Sprint);
        Assert.Null(Folder(c, "v0691-hotfix Deny var on payment accountant").Sprint);
    }

    [Fact]
    public void Sprint_TooLargeForInt_IsNull_AndHugeVersionIsMaxValue()
    {
        using var t = new TempDbUpdateTree();
        t.Add("v99999999999-sprint 99999999999", "a - 001 - x.sql");
        var f = DbUpdateCatalog.Scan(t.Root).Folders.Single();
        Assert.Null(f.Sprint);
        Assert.Equal(int.MaxValue, f.Version);
    }

    [Fact]
    public void Sprints_AreDistinctAndAscending()
    {
        using var t = TempDbUpdateTree.Standard();
        Assert.Equal(new[] { 23, 128, 129, 143 }, DbUpdateCatalog.Scan(t.Root).Sprints);
    }

    [Fact]
    public void FoldersOfSprint_143_IsThreeFoldersInOrder()
    {
        using var t = TempDbUpdateTree.Standard();
        var c = DbUpdateCatalog.Scan(t.Root);
        Assert.Equal(new[] { "v0860", "v0861", "v0862" }, c.FoldersOfSprint(143).Select(f => f.Code));
    }

    [Fact]
    public void FoldersOfSprint_129_IsV0710And0720_NotSprint128()
    {
        using var t = TempDbUpdateTree.Standard();
        var c = DbUpdateCatalog.Scan(t.Root);
        Assert.Equal(new[] { "v0710-sprint 129", "v0720-sprint 129" }, c.FoldersOfSprint(129).Select(f => f.Name));
        Assert.Equal(new[] { "v0710-sprint 128" }, c.FoldersOfSprint(128).Select(f => f.Name));
        Assert.Empty(c.FoldersOfSprint(9999));
    }

    [Fact]
    public void Scripts_AreInNaturalOrder()
    {
        using var t = TempDbUpdateTree.Standard();
        var scripts = Folder(DbUpdateCatalog.Scan(t.Root), "v0860-sprint 143").Scripts.Select(s => s.FileName);
        Assert.Equal(new[]
        {
            "adj - 2 - x.sql",
            "adj - 003 - Add covering index on Suppliers AccountId Deleted.sql",
            "adj - 10 - x.sql",
            "adj - 037 - x.sql",
            "fbo - 001 - A.sql",
            "fbo - 001 - B.sql",
            "mch - 003 - Production notes.sql",
            "mch - 010 - Disable users - Production2.sql",
        }, scripts);
    }

    [Fact]
    public void Script_CarriesPathAndFolderName()
    {
        using var t = TempDbUpdateTree.Standard();
        var s = Folder(DbUpdateCatalog.Scan(t.Root), "v0860-sprint 143").Scripts.First();
        Assert.Equal("v0860-sprint 143", s.FolderName);
        Assert.Equal(Path.Combine(t.Root, "v0860-sprint 143", s.FileName), s.FullPath);
    }

    [Fact]
    public void Production_FromFileNameSuffixOnly()
    {
        using var t = TempDbUpdateTree.Standard();
        var scripts = Folder(DbUpdateCatalog.Scan(t.Root), "v0860-sprint 143").Scripts;
        var prod = scripts.Single(s => s.FileName.StartsWith("mch - 010"));
        Assert.True(prod.IsProduction);
        Assert.Equal("Production2", prod.ProductionTag);
        var notes = scripts.Single(s => s.FileName.StartsWith("mch - 003"));
        Assert.False(notes.IsProduction);
        Assert.Null(notes.ProductionTag);
        Assert.All(scripts.Where(s => s != prod), s => Assert.False(s.IsProduction));
    }

    [Fact]
    public void Production_IsCaseInsensitive_AndNeedsADashBeforeIt()
    {
        using var t = new TempDbUpdateTree();
        t.Add("v0001-sprint 1", "a - 001 - x - production12.sql");
        t.Add("v0001-sprint 1", "a - 002 - xProduction1.sql");
        var scripts = DbUpdateCatalog.Scan(t.Root).Folders.Single().Scripts;
        Assert.True(scripts[0].IsProduction);
        Assert.Equal("production12", scripts[0].ProductionTag);
        Assert.False(scripts[1].IsProduction);
    }

    [Fact]
    public void Ignored_ListsNonSqlEntriesAndSubfolders()
    {
        using var t = TempDbUpdateTree.Standard();
        t.Add(Path.Combine("v0860-sprint 143", "old"), "zzz - 001 - nested.sql");
        t.Add("v0860-sprint 143", "backup.sqlx");
        var f = Folder(DbUpdateCatalog.Scan(t.Root), "v0860-sprint 143");

        Assert.Contains("fbo - 001 - Delete Obsole Sync Triggers", f.Ignored);
        Assert.Contains("notes.txt", f.Ignored);
        Assert.Contains("old", f.Ignored);
        Assert.Contains("backup.sqlx", f.Ignored);
        Assert.Equal(4, f.Ignored.Count);
        Assert.DoesNotContain(f.Scripts, s => s.FileName.Contains("nested"));
        Assert.DoesNotContain(f.Scripts, s => s.FileName.EndsWith(".sqlx"));
        Assert.Empty(Folder(DbUpdateCatalog.Scan(t.Root), "v0861-sprint 143").Ignored);
    }

    [Fact]
    public void Scan_UppercaseExtension_IsAScript()
    {
        using var t = new TempDbUpdateTree();
        t.Add("v0001-sprint 1", "a - 001 - x.SQL");
        var f = DbUpdateCatalog.Scan(t.Root).Folders.Single();
        Assert.Single(f.Scripts);
        Assert.Empty(f.Ignored);
    }

    [Fact]
    public void Scan_DoesNotReadFileContents()
    {
        using var t = new TempDbUpdateTree();
        t.Add("v0001-sprint 1", "a - 001 - x.sql");
        using var locked = new FileStream(Path.Combine(t.Root, "v0001-sprint 1", "a - 001 - x.sql"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Single(DbUpdateCatalog.Scan(t.Root).Folders.Single().Scripts);
    }

    [Fact]
    public void Scan_AcceptsTrailingBackslashAndSpaces()
    {
        using var t = TempDbUpdateTree.Standard();
        var c = DbUpdateCatalog.Scan("  " + t.Root + Path.DirectorySeparatorChar + " ");
        Assert.Equal(12, c.Folders.Count);
    }

    [Fact]
    public void NearestSprints_Unknown_ReturnsTheClosestOnes()
    {
        using var t = new TempDbUpdateTree();
        t.Add("v0001-sprint 128", "a.sql");
        t.Add("v0002-sprint 129", "a.sql");
        t.Add("v0003-sprint 143", "a.sql");
        var c = DbUpdateCatalog.Scan(t.Root);

        Assert.Equal(new[] { 129, 143 }, c.NearestSprints(140));
        Assert.Equal(new[] { 129, 143 }, c.NearestSprints(999));
        Assert.Equal(new[] { 128, 129 }, c.NearestSprints(1));
        Assert.Equal(new[] { 128, 129, 143 }, c.NearestSprints(140, 3));
    }

    [Fact]
    public void NearestSprints_EmptyCatalog_IsEmpty()
    {
        using var t = new TempDbUpdateTree();
        t.AddDir("v0001-hotfix");
        Assert.Empty(DbUpdateCatalog.Scan(t.Root).NearestSprints(5));
    }

    [Fact]
    public void Validate_BlankOrNull_GivesAMessage()
    {
        Assert.False(string.IsNullOrEmpty(DbUpdateCatalog.Validate(null)));
        Assert.False(string.IsNullOrEmpty(DbUpdateCatalog.Validate("")));
        Assert.False(string.IsNullOrEmpty(DbUpdateCatalog.Validate("   ")));
    }

    [Fact]
    public void Validate_MissingFolder_NamesThePath()
    {
        var path = Path.Combine(Path.GetTempPath(), "dbx-missing-" + Guid.NewGuid().ToString("N"));
        var msg = DbUpdateCatalog.Validate(path);
        Assert.NotNull(msg);
        Assert.Contains(path, msg);
    }

    [Fact]
    public void Validate_AFile_GivesAMessage()
    {
        using var t = new TempDbUpdateTree();
        t.Add("", "x.sql");
        var file = Path.Combine(t.Root, "x.sql");
        var msg = DbUpdateCatalog.Validate(file);
        Assert.NotNull(msg);
        Assert.Contains(file, msg);
    }

    [Fact]
    public void Validate_FolderWithoutVersionSubfolder_GivesAMessage()
    {
        using var t = new TempDbUpdateTree();
        t.AddDir("_views");
        t.AddDir("random");
        t.Add("", "cmi-01-x.sql");
        var msg = DbUpdateCatalog.Validate(t.Root);
        Assert.NotNull(msg);
        Assert.Contains(t.Root, msg);
    }

    [Fact]
    public void Validate_TrailingBackslashAndSpaces_AreAccepted()
    {
        using var t = TempDbUpdateTree.Standard();
        Assert.Null(DbUpdateCatalog.Validate(t.Root));
        Assert.Null(DbUpdateCatalog.Validate(t.Root + Path.DirectorySeparatorChar));
        Assert.Null(DbUpdateCatalog.Validate("  " + t.Root + "  "));
    }

    [Theory]
    [InlineData("\0bad")]
    [InlineData("C:\\<>|?*")]
    public void Validate_InvalidPaths_AreMessagesNeverExceptions(string path)
    {
        Assert.NotNull(DbUpdateCatalog.Validate(path));
    }

    [Fact]
    public void Scan_AnUnreadableVersionFolder_IsSkippedAndNamed_TheOthersAreKept()
    {
        using var t = TempDbUpdateTree.Standard();

        var catalog = DbUpdateCatalog.Scan(t.Root, dir =>
            Path.GetFileName(dir) == "v0710-sprint 128"
                ? throw new UnauthorizedAccessException("Access to the path is denied.")
                : Directory.EnumerateFileSystemEntries(dir));

        Assert.Equal(12, catalog.Folders.Count);
        var bad = Folder(catalog, "v0710-sprint 128");
        Assert.Empty(bad.Scripts);
        var note = Assert.Single(bad.Ignored);
        Assert.Contains("cannot be read", note);
        Assert.Contains("Access to the path is denied.", note);
        Assert.Equal(128, bad.Sprint);
        Assert.NotEmpty(Folder(catalog, "v0860-sprint 143").Scripts);
    }

    [Fact]
    public void Scan_AnUnreadableRoot_StillThrows()
    {
        var missing = Path.Combine(Path.GetTempPath(), "dbx-missing-" + Guid.NewGuid().ToString("N"));

        Assert.ThrowsAny<IOException>(() => DbUpdateCatalog.Scan(missing));
        Assert.ThrowsAny<IOException>(() => DbUpdateCatalog.Scan(missing, Directory.EnumerateFileSystemEntries));
    }
}
