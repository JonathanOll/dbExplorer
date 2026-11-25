namespace DbExplorer.Core.Tests.Upgrade;

/// <summary>A throw-away dbupdate folder tree on disk for scanner tests.</summary>
internal sealed class TempDbUpdateTree : IDisposable
{
    public string Root { get; }

    public TempDbUpdateTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "dbx-dbupdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>Writes <paramref name="file"/> in <paramref name="folder"/> (empty = the root).</summary>
    public void Add(string folder, string file, string text = "SELECT 1")
    {
        var dir = folder.Length == 0 ? Root : Path.Combine(Root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), text);
    }

    public void AddDir(string name) => Directory.CreateDirectory(Path.Combine(Root, name));

    /// <summary>A tree shaped like the real dbupdate folder.</summary>
    public static TempDbUpdateTree Standard()
    {
        var t = new TempDbUpdateTree();
        foreach (var f in new[]
        {
            "v0002-sprint 23", "v0003-sprint-23-hotfix", "v0013-sprint-ocr", "v0122-Hotfix-Trads",
            "V0121-change brandvariant", "v0691-hotfix Deny var on payment accountant",
            "v0710-sprint 128", "v0710-sprint 129", "v0720-sprint 129",
            "v0861-sprint 143", "v0862-sprint 143",
        })
            t.Add(f, "abc - 001 - x.sql");

        const string s143 = "v0860-sprint 143";
        foreach (var file in new[]
        {
            "adj - 037 - x.sql",
            "adj - 003 - Add covering index on Suppliers AccountId Deleted.sql",
            "adj - 10 - x.sql",
            "adj - 2 - x.sql",
            "fbo - 001 - B.sql",
            "fbo - 001 - A.sql",
            "mch - 010 - Disable users - Production2.sql",
            "mch - 003 - Production notes.sql",
            "fbo - 001 - Delete Obsole Sync Triggers", // extension-less, as in the real folder
            "notes.txt",
        })
            t.Add(s143, file);

        t.Add("_views", "IndexDetails.sql");
        t.Add("", "cmi-01-service-broker-x.sql");
        return t;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
