using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Tests.Upgrade;

public class SplitBatchesTests
{
    [Fact]
    public void Split_GoOnly_KeepsSemicolonsInsideTheBatch()
    {
        const string script =
            "IF NOT EXISTS (SELECT 1) BEGIN\n DROP INDEX IF EXISTS [IX_a] ON [dbo].[T];\n CREATE NONCLUSTERED INDEX [IX_a] ON [dbo].[T] ([c]);\nEND\nGO\nSELECT 3";

        var batches = ScriptSplitter.SplitBatches(script);

        Assert.Equal(2, batches.Count);
        Assert.Contains("DROP INDEX IF EXISTS [IX_a] ON [dbo].[T];", batches[0].Text);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_a] ON [dbo].[T] ([c]);", batches[0].Text);
        Assert.EndsWith("END", batches[0].Text);
        Assert.Equal("SELECT 3", batches[1].Text);
    }

    [Fact]
    public void Split_TheExistingSplit_StillCutsAtSemicolons()
    {
        // The regression net of the new flag: Split keeps cutting at ';', SplitBatches does not.
        const string script = "SELECT 1; SELECT 2";

        Assert.Equal(2, ScriptSplitter.Split(script, DbKind.SqlServer).Count);
        Assert.Single(ScriptSplitter.SplitBatches(script));
    }

    [Theory]
    [InlineData("SELECT 1\ngo\nSELECT 2")]
    [InlineData("SELECT 1\nGO -- next part\nSELECT 2")]
    [InlineData("SELECT 1\r\nGO\r\nSELECT 2")]
    [InlineData("SELECT 1\n  Go  \nSELECT 2")]
    public void Split_GoLine_Separates(string script)
    {
        var batches = ScriptSplitter.SplitBatches(script);

        Assert.Equal(["SELECT 1", "SELECT 2"], batches.Select(b => b.Text));
    }

    [Theory]
    [InlineData("SELECT 'a\nGO\nb'")]
    [InlineData("SELECT 1 /* a\nGO\nb */")]
    [InlineData("SELECT 1 -- a\n-- GO\nSELECT 2")]
    [InlineData("SELECT [GO]")]
    [InlineData("SELECT 1 AS GO")]
    public void Split_GoInsideStringCommentOrIdentifier_DoesNotSplit(string script)
    {
        Assert.Single(ScriptSplitter.SplitBatches(script));
    }

    [Fact]
    public void Split_NoTrailingNewlineAfterTheLastGo()
    {
        var batches = ScriptSplitter.SplitBatches("SELECT 1\nGO");

        Assert.Equal("SELECT 1", Assert.Single(batches).Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    [InlineData("-- only a comment")]
    [InlineData("/* only */\nGO\n-- another\n")]
    public void Split_EmptyOrCommentOnlyScript_HasNoBatch(string script)
    {
        Assert.Empty(ScriptSplitter.SplitBatches(script));
    }

    [Fact]
    public void Split_BatchOffsetsPointIntoTheScript()
    {
        const string script = "SELECT 1\nGO\n  SELECT 2;\nGO\n";

        foreach (var b in ScriptSplitter.SplitBatches(script))
            Assert.Equal(b.Text, script[b.Start..b.End]);
    }

    [Fact]
    public void FindGoRepeat_ReportsTheLineOfAGoWithACountAboveOne()
    {
        Assert.Equal(2, ScriptSplitter.FindGoRepeat("SELECT 1\nGO 5\n"));
        Assert.Equal(3, ScriptSplitter.FindGoRepeat("SELECT 1\r\nGO\r\ngo   2 -- twice\r\n"));
        Assert.Equal(1, ScriptSplitter.FindGoRepeat("GO 99999999999\nSELECT 1"));
    }

    [Theory]
    [InlineData("SELECT 1\nGO\nSELECT 2")]
    [InlineData("SELECT 1\nGO 1\nSELECT 2")]
    [InlineData("SELECT 'x\nGO 5\n'")]
    [InlineData("-- GO 5\nSELECT 1")]
    [InlineData("SELECT 1 /*\nGO 5\n*/")]
    [InlineData("")]
    public void FindGoRepeat_IsNullWhenThereIsNoRepeat(string script)
    {
        Assert.Null(ScriptSplitter.FindGoRepeat(script));
    }

    [Theory]
    [InlineData("SELECT 1\nGO /* c */\nSELECT 2")]
    [InlineData("SELECT 1\n/* c */ GO\nSELECT 2")]
    [InlineData("SELECT 1\n/* a */ /* b */ GO /* c */ -- d\nSELECT 2")]
    [InlineData("SELECT 1\nGO 1 /* c */\nSELECT 2")]
    [InlineData("SELECT 1\nGO /* multi\nline */\nSELECT 2")]
    [InlineData("SELECT 1\r\nGO/* c */\r\nSELECT 2")]
    public void Split_GoWithBlockComments_Separates(string script)
    {
        Assert.Equal(["SELECT 1", "SELECT 2"], ScriptSplitter.SplitBatches(script).Select(b => b.Text));
        Assert.Null(ScriptSplitter.FindUnsupportedGo(script));
    }

    [Theory]
    [InlineData("SELECT 1\nGO;\nSELECT 2", 2)]
    [InlineData("SELECT 1\nGO ;\nSELECT 2", 2)]
    [InlineData("SELECT 1\nGO SELECT 2", 2)]
    [InlineData("SELECT 1\nGO 5x\nSELECT 2", 2)]
    [InlineData("SELECT 1\nGO /* c */ SELECT 2", 2)]
    [InlineData("SELECT 1\nGO,\nSELECT 2", 2)]
    [InlineData("SELECT 1\n/* c */ GO;\nSELECT 2", 2)]
    [InlineData("SELECT 1\r\nGO\r\nGO foo", 3)]
    public void GoLikeLineThatIsNotASeparator_IsUnsupported_AndSplitBatchesNeverMergesIt(string script, int line)
    {
        var found = ScriptSplitter.FindUnsupportedGo(script);

        Assert.NotNull(found);
        Assert.Equal(line, found!.Line);
        Assert.Throws<InvalidOperationException>(() => ScriptSplitter.SplitBatches(script));
    }

    [Fact]
    public void FindUnsupportedGo_ReportsARepeatCountWithTheGoNWording()
    {
        var found = ScriptSplitter.FindUnsupportedGo("SELECT 1\nGO 3\n");

        Assert.Equal(2, found!.Line);
        Assert.Contains("GO n", found.Reason);
        Assert.Equal(2, ScriptSplitter.FindGoRepeat("SELECT 1\nGO 0\n"));
    }

    [Theory]
    [InlineData("GOTO label")]
    [InlineData("SELECT 1 AS GO")]
    [InlineData("SELECT 1 /* c */ GO")]
    [InlineData("SELECT 1\ngo -- c\nSELECT 2")]
    [InlineData("SELECT 'a\nGO;\nb'")]
    [InlineData("SELECT 1 /* a\nGO;\nb */")]
    [InlineData("SELECT 1 -- a\n-- GO;\nSELECT 2")]
    [InlineData("SELECT [GO;]")]
    [InlineData("SELECT x,\n  [GO]\nFROM t")]
    [InlineData("SELECT 1\n\nGO\r\nSELECT 2")]
    [InlineData("SELECT 1\rGO\rSELECT 2")]
    public void FindUnsupportedGo_IsNullForEverythingThatIsNotAGoLike(string script)
    {
        Assert.Null(ScriptSplitter.FindUnsupportedGo(script));
    }

    [Fact]
    public void Split_EditorSplit_StillOnlyDropsPlainGoLines_AndNeverThrows()
    {
        Assert.Equal(["SELECT 1", "SELECT 2"], ScriptSplitter.Split("SELECT 1\nGO\nSELECT 2", DbKind.SqlServer).Select(b => b.Text));
        Assert.Null(Record.Exception(() => ScriptSplitter.Split("SELECT 1\nGO;\nSELECT 2", DbKind.SqlServer)));
    }
}
