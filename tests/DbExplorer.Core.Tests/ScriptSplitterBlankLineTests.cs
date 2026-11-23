using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Tests;

/// <summary>Ctrl+Enter without a selection also ends a statement at a blank line (T-SQL needs no ';').</summary>
public class ScriptSplitterBlankLineTests
{
    private static string[] Texts(string script, DbKind kind = DbKind.SqlServer) =>
        ScriptSplitter.SplitAtBlankLines(script, kind).Select(s => s.Text).ToArray();

    [Fact]
    public void BlankLine_SeparatesStatementsWithoutSemicolons()
    {
        var script = "select * from Users where id = 5\n\nupdate Users set name='x' where id=5";

        Assert.Equal(["select * from Users where id = 5", "update Users set name='x' where id=5"], Texts(script));
        // F5 / a selection keep the old splitting: one batch.
        Assert.Single(ScriptSplitter.Split(script, DbKind.SqlServer));
    }

    [Fact]
    public void CaretOnTheSelect_RunsOnlyTheSelect()
    {
        var script = "select * from Users where id = 5\n\nupdate Users set name='x' where id=5";

        var at = ScriptSplitter.StatementAtCaret(script, DbKind.SqlServer, 3);

        Assert.Equal("select * from Users where id = 5", at!.Text);
        Assert.Equal("update Users set name='x' where id=5",
            ScriptSplitter.StatementAtCaret(script, DbKind.SqlServer, script.Length - 2)!.Text);
    }

    [Theory]
    [InlineData("select 1\r\n\r\nselect 2")]
    [InlineData("select 1\n   \t \nselect 2")]       // whitespace-only line
    [InlineData("select 1\n\n\n\nselect 2")]         // several blank lines
    [InlineData("select 1;\n\nselect 2")]            // with a semicolon too
    public void BlankLineVariants(string script)
    {
        Assert.Equal(["select 1", "select 2"], Texts(script).Select(t => t.TrimEnd(';')).ToArray());
    }

    [Fact]
    public void ConsecutiveLines_AreOneStatement()
    {
        var script = "select a,\n       b\nfrom t\nwhere x = 1";

        Assert.Equal([script], Texts(script));
    }

    [Fact]
    public void BlankLineInsideStringLiteral_DoesNotSplit()
    {
        var script = "select 'first\n\nsecond' as v\n\nselect 2";

        Assert.Equal(["select 'first\n\nsecond' as v", "select 2"], Texts(script));
    }

    [Fact]
    public void BlankLineInsideQuotedIdentifier_DoesNotSplit()
    {
        var script = "select [a\n\nb] from t";

        Assert.Equal([script], Texts(script));
    }

    [Fact]
    public void BlankLineInsideBlockComment_DoesNotSplit()
    {
        var script = "select 1 /* a\n\nb */ from t";

        Assert.Equal([script], Texts(script));
    }

    [Fact]
    public void LineCommentLine_IsNotABlankLine()
    {
        var script = "select 1\n-- note\nfrom t";

        Assert.Equal([script], Texts(script));
    }

    [Fact]
    public void BlankLineInsideNestedBlockComment_DoesNotSplit()
    {
        var script = "select 1 /* a /* b\n\nc */ d */ from t";

        Assert.Equal([script], Texts(script));
    }

    [Fact]
    public void BlankLineInsideDollarQuotedBody_DoesNotSplit()
    {
        var script = "create function f() returns int as $$\nbegin\n\n  return 1;\nend;\n$$ language plpgsql\n\nselect f()";

        Assert.Equal(
            ["create function f() returns int as $$\nbegin\n\n  return 1;\nend;\n$$ language plpgsql", "select f()"],
            Texts(script, DbKind.Postgres));
    }

    [Fact]
    public void CommentBeforeBlankLine_IsNotPartOfTheNextStatement_ButAdjacentCommentIs()
    {
        var script = "-- header\n\n-- explains the select\nselect 1\n\nselect 2";

        Assert.Equal(["-- explains the select\nselect 1", "select 2"], Texts(script));
    }

    [Fact]
    public void GoSeparator_StillWorks()
    {
        var script = "select 1\nGO\n\nselect 2\n\nselect 3";

        Assert.Equal(["select 1", "select 2", "select 3"], Texts(script));
    }

    [Fact]
    public void CaretOnABlankLine_RunsTheStatementAbove()
    {
        var script = "select 1\n\nselect 2";
        var blank = script.IndexOf("\n\n", StringComparison.Ordinal) + 1;   // the empty line

        Assert.Equal("select 1", ScriptSplitter.StatementAtCaret(script, DbKind.SqlServer, blank)!.Text);
    }

    [Fact]
    public void CaretOnLeadingBlankLines_RunsTheFirstStatement()
    {
        var script = "\n\nselect 1\n\nselect 2";

        Assert.Equal("select 1", ScriptSplitter.StatementAtCaret(script, DbKind.SqlServer, 0)!.Text);
    }

    [Fact]
    public void CaretAtTheStartOfTheNextStatement_RunsThatStatement()
    {
        var script = "select 1\n\nselect 2";

        Assert.Equal("select 2", ScriptSplitter.StatementAtCaret(script, DbKind.SqlServer, script.IndexOf("select 2", StringComparison.Ordinal))!.Text);
    }

    [Fact]
    public void EmptyOrCommentOnlyScript_HasNoStatement()
    {
        Assert.Null(ScriptSplitter.StatementAtCaret("", DbKind.SqlServer, 0));
        Assert.Null(ScriptSplitter.StatementAtCaret("-- nothing\n\n/* here */", DbKind.Postgres, 3));
    }

    // ---- safety rule: an UPDATE/DELETE never runs without the WHERE that a blank line separated from it ----

    [Theory]
    [InlineData("update t set a = 1\n\nwhere id = 5")]
    [InlineData("DELETE FROM t\n\n-- only this one\nWHERE id = 5")]
    [InlineData("update t set a = 1\r\n\r\nwhere id = 5 and b = 2")]
    public void UpdateOrDeleteWithoutWhere_FollowedByAWhereSegment_IsOneStatement(string script)
    {
        foreach (var caret in new[] { 3, script.Length - 2 })
        {
            var at = ScriptSplitter.StatementAtCaret(script, DbKind.SqlServer, caret)!;

            // from the first statement's text up to the end of the WHERE segment
            Assert.Equal(script, at.Text);
            Assert.Equal(0, at.Start);
            Assert.Equal(script.Length, at.End);
        }
    }

    [Fact]
    public void UpdateWithItsOwnWhere_StaysAlone()
    {
        var script = "update t set a = 1 where id = 5;\n\nselect 2";

        Assert.Equal("update t set a = 1 where id = 5", ScriptSplitter.StatementAtCaret(script, DbKind.SqlServer, 3)!.Text);
        Assert.Equal("select 2", ScriptSplitter.StatementAtCaret(script, DbKind.SqlServer, script.Length)!.Text);
    }

    [Fact]
    public void DeleteFollowedByAnotherStatement_StaysSplit()
    {
        var script = "delete from t\n\nselect 1";

        Assert.Equal("delete from t", ScriptSplitter.StatementAtCaret(script, DbKind.SqlServer, 3)!.Text);
        Assert.Equal("select 1", ScriptSplitter.StatementAtCaret(script, DbKind.SqlServer, script.Length)!.Text);
    }

    [Fact]
    public void ASemicolonBetweenThem_KeepsThemApart()
    {
        var script = "update t set a = 1;\n\nwhere id = 5";

        Assert.Equal("update t set a = 1", ScriptSplitter.StatementAtCaret(script, DbKind.SqlServer, 3)!.Text);
    }

    [Fact]
    public void WhereInsideAStringOrComment_DoesNotCountAsTheStatementsOwn()
    {
        var script = "update t set note = 'where' -- where\n\nwhere id = 5";

        Assert.Equal(script, ScriptSplitter.StatementAtCaret(script, DbKind.SqlServer, 3)!.Text);
    }

    [Fact]
    public void SelectFollowedByWhere_IsNotJoined()
    {
        var script = "select * from t\n\nwhere id = 5";

        Assert.Equal("select * from t", ScriptSplitter.StatementAtCaret(script, DbKind.SqlServer, 3)!.Text);
    }

    [Fact]
    public void StatementOffsetsPointIntoTheScript()
    {
        var script = "select 1\n\n  select 2  \n\n";
        var all = ScriptSplitter.SplitAtBlankLines(script, DbKind.Sqlite);

        Assert.All(all, s => Assert.Equal(s.Text, script[s.Start..s.End]));
    }
}
