using System.Text;
using DbExplorer.Core.Upgrade;

namespace DbExplorer.Core.Tests.Upgrade;

public class UpgradePlannerTests
{
    private const string Sprint143 = "v0860-sprint 143";

    private static UpgradePlan BuildOf(TempDbUpdateTree tree, params string[] folderNames)
    {
        var catalog = DbUpdateCatalog.Scan(tree.Root);
        var folders = folderNames.Select(n => catalog.Folders.Single(f => f.Name == n)).ToList();
        return UpgradePlanner.Build("Sprint 143", "143", folders);
    }

    [Fact]
    public void Build_ItemsFollowCatalogOrder_AndCarryLabelAndToken()
    {
        using var tree = TempDbUpdateTree.Standard();
        var plan = BuildOf(tree, Sprint143, "v0861-sprint 143");

        Assert.Equal("Sprint 143", plan.Label);
        Assert.Equal("143", plan.ConfirmToken);
        Assert.Equal(9, plan.Items.Count);
        Assert.Equal($"{Sprint143}/adj - 2 - x.sql", plan.Items[0].DisplayName);
        Assert.Equal($"{Sprint143}/mch - 010 - Disable users - Production2.sql", plan.Items[7].DisplayName);
        Assert.Equal("v0861-sprint 143/abc - 001 - x.sql", plan.Items[8].DisplayName);
    }

    [Fact]
    public void Build_BomAndCrlf_TextHasNoBom()
    {
        using var tree = new TempDbUpdateTree();
        var dir = Path.Combine(tree.Root, Sprint143);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.sql"), [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("SELECT 1\r\nGO\r\nSELECT 2")]);

        var item = BuildOf(tree, Sprint143).Items.Single();
        Assert.Null(item.LoadError);
        Assert.Equal("SELECT 1\r\nGO\r\nSELECT 2", item.Text);
    }

    [Fact]
    public void Build_Windows1252Accent_Survives()
    {
        using var tree = new TempDbUpdateTree();
        var dir = Path.Combine(tree.Root, Sprint143);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.sql"), [.. "PRINT '"u8, 0xE9, .. "'"u8]);

        Assert.Equal("PRINT 'é'", BuildOf(tree, Sprint143).Items.Single().Text);
    }

    [Fact]
    public void Build_WarningsComeFromTheAnalyzer()
    {
        using var tree = new TempDbUpdateTree();
        tree.Add(Sprint143, "adj - 003 - idx.sql",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Invoices_PublicId')\r\nBEGIN\r\n" +
            "  CREATE NONCLUSTERED INDEX IX_Invoices_PublicId ON [dbo].[Invoices] ([PublicId])\r\nEND");
        tree.Add(Sprint143, "adj - 004 - view.sql", "CREATE VIEW v AS SELECT 1 AS a");

        var items = BuildOf(tree, Sprint143).Items;
        var idx = Assert.Single(items[0].Warnings);
        Assert.Equal(ScriptWarningKind.Index, idx.Kind);
        Assert.Empty(items[1].Warnings);
    }

    [Fact]
    public void Build_GoRepeat_BecomesLoadErrorWithLine_TextNull()
    {
        using var tree = new TempDbUpdateTree();
        tree.Add(Sprint143, "a.sql", "SELECT 1\nGO 3\nSELECT 2");

        var item = BuildOf(tree, Sprint143).Items.Single();
        Assert.Null(item.Text);
        Assert.NotNull(item.LoadError);
        Assert.Contains("2", item.LoadError);
        Assert.Empty(item.Warnings);
    }

    [Theory]
    [InlineData("SELECT 1\nGO;\nSELECT 2", 2)]
    [InlineData("SELECT 1\nGO SELECT 2", 2)]
    public void Build_GoLikeLine_BecomesLoadError_NeverMerged(string script, int line)
    {
        using var tree = new TempDbUpdateTree();
        tree.Add(Sprint143, "a.sql", script);

        var item = BuildOf(tree, Sprint143).Items.Single();
        Assert.Null(item.Text);
        Assert.Contains($"line {line}", item.LoadError);
        Assert.Contains("GO", item.LoadError);
        Assert.Empty(item.Warnings);
    }

    [Theory]
    [InlineData("USE [other]\nGO\nUPDATE t SET a = 1 WHERE id = 1", 1)]
    [InlineData("SELECT 1\nUSE other;", 2)]
    [InlineData("SELECT 1\n  use master\nGO", 2)]
    [InlineData("EXEC('USE other; DELETE FROM t WHERE id = 1')", 1)]
    public void Build_UseStatement_BecomesLoadError_TextNull(string script, int line)
    {
        using var tree = new TempDbUpdateTree();
        tree.Add(Sprint143, "a.sql", script);

        var item = BuildOf(tree, Sprint143).Items.Single();
        Assert.Null(item.Text);
        Assert.Equal($"USE <db> would redirect the run to a database that was not confirmed (line {line}).", item.LoadError);
        Assert.Empty(item.Warnings);
    }

    [Fact]
    public void Build_LockedFile_LoadErrorOnThatItemOnly()
    {
        using var tree = new TempDbUpdateTree();
        tree.Add(Sprint143, "a.sql", "SELECT 1");
        tree.Add(Sprint143, "b.sql", "SELECT 2");
        var catalog = DbUpdateCatalog.Scan(tree.Root);

        UpgradePlan plan;
        using (new FileStream(Path.Combine(tree.Root, Sprint143, "a.sql"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            plan = UpgradePlanner.Build("L", "143", catalog.Folders);

        Assert.Null(plan.Items[0].Text);
        Assert.NotNull(plan.Items[0].LoadError);
        Assert.Equal("SELECT 2", plan.Items[1].Text);
        Assert.Null(plan.Items[1].LoadError);
    }

    [Fact]
    public void Build_VanishedFile_LoadError_NoException()
    {
        using var tree = new TempDbUpdateTree();
        tree.Add(Sprint143, "a.sql", "SELECT 1");
        var catalog = DbUpdateCatalog.Scan(tree.Root);
        File.Delete(Path.Combine(tree.Root, Sprint143, "a.sql"));

        var item = UpgradePlanner.Build("L", "143", catalog.Folders).Items.Single();
        Assert.NotNull(item.LoadError);
    }

    [Fact]
    public void Build_EmptyFolder_NoItems()
    {
        using var tree = new TempDbUpdateTree();
        tree.AddDir(Sprint143);

        var plan = BuildOf(tree, Sprint143);
        Assert.Empty(plan.Items);
    }

    [Fact]
    public void Build_IgnoredListsFolderSlashName()
    {
        using var tree = TempDbUpdateTree.Standard();
        var plan = BuildOf(tree, Sprint143);

        Assert.Contains($"{Sprint143}/fbo - 001 - Delete Obsole Sync Triggers", plan.Ignored);
        Assert.Contains($"{Sprint143}/notes.txt", plan.Ignored);
    }
}
