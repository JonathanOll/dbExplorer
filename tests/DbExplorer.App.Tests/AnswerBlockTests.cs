using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Tests;

public class AnswerBlockTests
{
    [Fact]
    public void Split_CutsAtFences_DropsTheFencesAndTheLanguage()
    {
        var blocks = AnswerBlock.Split("Use an index:\n\n```sql\nCREATE INDEX ix ON t (a);\nSELECT 1;\n```\n\nThat is all.");

        Assert.Equal(
            [new AnswerBlock("Use an index:", false), new AnswerBlock("CREATE INDEX ix ON t (a);\nSELECT 1;", true), new AnswerBlock("That is all.", false)],
            blocks);
    }

    [Fact]
    public void Split_AnUnclosedFence_RunsToTheEndAsCode()
    {
        // the answer is still streaming
        var blocks = AnswerBlock.Split("Here:\r\n```\nSELECT");

        Assert.Equal([new AnswerBlock("Here:", false), new AnswerBlock("SELECT", true)], blocks);
    }

    [Fact]
    public void Split_PlainText_IsOneBlock()
    {
        Assert.Equal([new AnswerBlock("Just words.", false)], AnswerBlock.Split("Just words."));
        Assert.Empty(AnswerBlock.Split(""));
    }
}
