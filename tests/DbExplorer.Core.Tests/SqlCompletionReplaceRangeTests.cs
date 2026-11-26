using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Tests;

/// <summary>What an accepted item replaces: offsets in the whole script, never more than the word being typed.</summary>
public class SqlCompletionReplaceRangeTests
{
    private static readonly ICompletionSchema Schema = new TableListSchema(
    [
        new TableInfo(new TableRef(null, "orders"), false,
            [new ColumnInfo("total", "int", typeof(int), true, false, 0)], [], []),
    ]);

    /// <summary>'|' marks the caret; <paramref name="typed"/> is the text that must be replaced.</summary>
    [Theory]
    [InlineData("SELECT 1;\nSELECT * FROM ord|", DbKind.Sqlite, "ord")]
    [InlineData("SELECT 1;\r\n\r\nSELECT * FROM ord|", DbKind.Sqlite, "ord")]
    [InlineData("SELECT 1\n\nSELECT tot| FROM orders", DbKind.Postgres, "tot")]
    [InlineData("SELECT 1\r\nGO\r\nSELECT * FROM ord|\r\nGO\r\n", DbKind.SqlServer, "ord")]
    [InlineData("SELECT 1; SELECT o.to| FROM orders o", DbKind.Postgres, "to")]
    [InlineData("SELECT 1;\nSELECT * FROM |", DbKind.Sqlite, "")]
    [InlineData("SELECT 1;\n\nsel|", DbKind.Sqlite, "sel")]
    [InlineData("SELECT 1;\nSELECT * FROM \"ord|", DbKind.Postgres, "\"ord")]
    [InlineData("SELECT 1;\nSELECT * FROM [ord|", DbKind.SqlServer, "[ord")]
    [InlineData("SELECT '[' FROM ord|", DbKind.SqlServer, "ord")]
    [InlineData("SELECT 1 -- [\nFROM ord|", DbKind.SqlServer, "ord")]
    [InlineData("SELECT [a], 2 /* [ */ FROM ord|", DbKind.SqlServer, "ord")]
    public void TheReplacedRange_IsTheTypedWordOnly(string sqlWithCaret, DbKind kind, string typed)
    {
        var caret = sqlWithCaret.IndexOf('|');
        var sql = sqlWithCaret.Remove(caret, 1);

        var items = SqlCompletion.Complete(sql, caret, Schema, kind);

        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.Equal((caret - typed.Length, typed.Length), (i.ReplaceStart, i.ReplaceLength)));
    }

    // A stray opening quote or bracket on an earlier line is not an identifier being typed: the lines after it are not replaced.
    [Theory]
    [InlineData("SELECT [\nFROM ord|", DbKind.SqlServer)]
    [InlineData("SELECT \"\nFROM ord|", DbKind.Postgres)]
    public void AStrayOpeningOnAnEarlierLine_NeverReplacesAcrossLines(string sqlWithCaret, DbKind kind)
    {
        var caret = sqlWithCaret.IndexOf('|');
        var sql = sqlWithCaret.Remove(caret, 1);

        var items = SqlCompletion.Complete(sql, caret, Schema, kind);

        Assert.All(items, i => Assert.True(i.ReplaceStart >= sql.IndexOf('\n')));
    }
}
