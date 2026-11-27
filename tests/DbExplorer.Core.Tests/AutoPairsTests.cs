using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Tests;

/// <summary>When the editor adds the closing bracket or quote by itself. '|' marks the caret.</summary>
public class AutoPairsTests
{
    [Theory]
    [InlineData("select |", '(', true)]
    [InlineData("select f|", '(', true)]
    [InlineData("select (|)", '(', true)]
    [InlineData("in (|, 2)", '\'', true)]
    [InlineData("select |abc", '(', false)]
    [InlineData("select |1", '(', false)]
    [InlineData("select |'a'", '(', false)]
    [InlineData("select abc|", '\'', false)]
    [InlineData("select abc|", '"', false)]
    [InlineData("select ''|", '\'', false)]
    [InlineData("select N|", '\'', true)]
    [InlineData("select 'a |", '"', false)]
    [InlineData("select 'a| '", '\'', false)]
    [InlineData("-- l |", '"', false)]
    [InlineData("-- x |", '\'', false)]
    [InlineData("/* x |", '\'', false)]
    [InlineData("/* x */ |", '\'', true)]
    [InlineData("-- x\n|", '\'', true)]
    [InlineData("select \"a |\"", '\'', false)]
    [InlineData("select 'a' |", '\'', true)]
    [InlineData("select |", 'x', false)]
    public void ShouldClose(string marked, char opener, bool expected) =>
        Assert.Equal(expected, Check(marked, opener, DbKind.SqlServer));

    [Theory]
    [InlineData(DbKind.SqlServer, true)]
    [InlineData(DbKind.Sqlite, true)]
    [InlineData(DbKind.Postgres, false)]   // no bracket identifiers: [ is an array subscript there
    public void SquareBrackets_FollowTheEngine(DbKind kind, bool expected) =>
        Assert.Equal(expected, Check("select |", '[', kind));

    [Fact]
    public void Quotes_InsideABracketIdentifier_AreNotClosed() =>
        Assert.False(Check("select [l |]", '\'', DbKind.SqlServer));

    [Fact]
    public void ACompletionInsideAClosedQuote_StillSeesTheRestOfTheStatement()
    {
        const string text = "SELECT \"i\" FROM t";
        var schema = new TableListSchema(
        [
            new TableInfo(new TableRef(null, "t"), false, [new ColumnInfo("id", "integer", typeof(object), true, true, 0)], [], []),
        ]);
        var items = SqlCompletion.Complete(text, "SELECT \"i".Length, schema, DbKind.Sqlite);
        Assert.Contains(items, i => i.Label == "id");
    }

    private static bool Check(string marked, char opener, DbKind kind)
    {
        var caret = marked.IndexOf('|');
        return AutoPairs.ShouldClose(marked.Remove(caret, 1), caret, opener, kind);
    }
}
