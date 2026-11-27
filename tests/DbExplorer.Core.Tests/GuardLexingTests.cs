using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

// Hostile texts where the lexer and the engine could disagree about what is code. A disagreement that hides a statement
// from the guard is a bypass. SQLite is checked against a real file.
public sealed class GuardLexingTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"dbx-guard-{Guid.NewGuid():N}.db");

    public GuardLexingTests() => Execute("CREATE TABLE t(a); INSERT INTO t VALUES (1), (2)");

    public void Dispose() => File.Delete(_file);

    private long Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_file};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using (var reader = command.ExecuteReader())
            do { while (reader.Read()) { } } while (reader.NextResult());
        command.CommandText = "SELECT count(*) FROM t";
        return (long)command.ExecuteScalar()!;
    }

    // A comment ends at '\n' only in SQLite: ending it at a lone '\r' opens a string there that hides the real DELETE.
    public static TheoryData<string> SqliteHidingDeletes => new()
    {
        "SELECT 1 --x\r'\n; DELETE FROM t --'",
        "SELECT 1 --x\r/*\n; DELETE FROM t --*/",
        "SELECT 1 --x\r\"\n; DELETE FROM t --\"",
        "SELECT 1 --x\r[\n; DELETE FROM t --]",
        "SELECT 1 --x\r`\n; DELETE FROM t --`",
    };

    [Theory]
    [MemberData(nameof(SqliteHidingDeletes))]
    public void Sqlite_StatementHiddenBehindALoneCr_IsAWrite(string sql)
    {
        Assert.True(ReadOnlyGuard.IsWrite(sql, DbKind.Sqlite));
        Assert.True(ReadOnlyGuard.IsWrite(sql));
        Assert.True(ProductionWrites.MayWrite(sql, DbKind.Sqlite));
        var statements = ScriptSplitter.Split(sql, DbKind.Sqlite);
        Assert.Contains(statements, s => s.Text.StartsWith("DELETE", StringComparison.Ordinal));
        Assert.Equal(0, Execute(sql));   // the engine really runs the DELETE
    }

    [Fact]
    public void Sqlite_Splitter_KeepsWhatTheEngineReadsAsAComment()
    {
        const string sql = "SELECT 1 --x\r; DELETE FROM t";
        Assert.Single(ScriptSplitter.Split(sql, DbKind.Sqlite));
        Assert.Equal(2, Execute(sql));   // nothing was deleted: all of it after "--" is a comment
        // The guard stays as strict as before: it still refuses the text.
        Assert.True(ReadOnlyGuard.IsWrite(sql, DbKind.Sqlite));
    }

    [Fact]
    public void Sqlite_CrLfScripts_SplitAsBefore()
    {
        var statements = ScriptSplitter.Split("SELECT 1; -- one\r\nSELECT 2 -- two\r\n;\r\n", DbKind.Sqlite);
        Assert.Equal(["SELECT 1", "-- one\r\nSELECT 2"], statements.Select(s => s.Text));
        Assert.False(ReadOnlyGuard.IsWrite("SELECT 1 -- delete\r\n", DbKind.Sqlite));
    }

    [Theory]
    [InlineData("SELECT $a('x) ; DELETE FROM t --'")]   // Tcl-style variable: SQLite stops at the unbound parameter
    [InlineData("SELECT 1 AS [a]]; DELETE FROM t --]")]   // no "]]" escape in SQLite: a syntax error
    public void Sqlite_OtherLexingGaps_NeverReachTheDelete(string sql)
    {
        Assert.ThrowsAny<Exception>(() => Execute(sql));
        Assert.Equal(2, Execute("SELECT 1"));
    }

    [Fact]
    public void Sqlite_Nul_IsAWrite()
    {
        // Microsoft.Data.Sqlite never returns from a text with a NUL inside a comment: such a text must not be sent.
        Assert.True(ReadOnlyGuard.IsWrite("SELECT 1 --\0", DbKind.Sqlite));
        Assert.True(ReadOnlyGuard.IsWrite("SELECT 1\0; DELETE FROM t", DbKind.Sqlite));
    }

    // PostgreSQL (scan.l): every character above 0x7F is part of an identifier, '@' and '#' are operators, and a quote
    // continued on a later line keeps the escape rules of an E'' string.
    [Theory]
    [InlineData("SELECT @$$'$$; DELETE FROM t; --'")]
    [InlineData("SELECT #$$'$$; DELETE FROM t; --'")]
    [InlineData("SELECT 1$$'$$; DELETE FROM t; --'")]
    [InlineData("SELECT 1 AS a€$$; DELETE FROM t; --$$")]
    [InlineData("SELECT 1 AS a $$; DELETE FROM t; --$$")]
    [InlineData("SELECT $é$'$é$; DELETE FROM t; --'")]
    [InlineData("SELECT E'a'\n'\\''; DELETE FROM t; --'")]
    [InlineData("SELECT E'a' -- c\n  '\\''; DELETE FROM t; --'")]
    [InlineData("SELECT @E'\\''; DELETE FROM t; --'")]
    [InlineData("SELECT U&\"pg_terminate_b\\0061ckend\"(1)")]
    [InlineData("SELECT U&\"pg_terminate_b!0061ckend\" UESCAPE '!' (1)")]
    public void Postgres_HiddenWrites_AreWrites(string sql)
    {
        Assert.True(ReadOnlyGuard.IsWrite(sql, DbKind.Postgres));
        Assert.True(ProductionWrites.MayWrite(sql, DbKind.Postgres));
    }

    [Theory]
    [InlineData("SELECT @$$'$$; DELETE FROM t")]
    [InlineData("SELECT E'a'\n'\\''; DELETE FROM t")]
    public void Postgres_Splitter_FindsTheHiddenStatement(string sql) =>
        Assert.Equal("DELETE FROM t", ScriptSplitter.Split(sql, DbKind.Postgres)[^1].Text);

    [Theory]
    [InlineData("SELECT $$delete$$, $tag$ drop $tag$ FROM t")]
    [InlineData("SELECT E'a\\'delete' FROM été WHERE a$1 = $1")]
    [InlineData("SELECT 'a'\n'delete'")]
    [InlineData("SELECT U&\"d\\0061ta\" FROM t")]
    public void Postgres_PlainReads_StayReads(string sql) => Assert.False(ReadOnlyGuard.IsWrite(sql, DbKind.Postgres));

    [Fact]
    public void Postgres_LoneCr_EndsAComment() =>
        Assert.True(ReadOnlyGuard.IsWrite("SELECT 1 --x\rDELETE FROM t", DbKind.Postgres));

    // SQL Server (checked on 17.0): a lone CR ends a "--" comment.
    [Theory]
    [InlineData("SELECT 1 --x\rDELETE FROM t")]
    // Refused wherever they appear: a COMMIT would end the transaction that protects a read-only connection.
    [InlineData("SELECT 1 COMMIT DISABLE TRIGGER tr ON t")]
    [InlineData("SELECT 1 ENABLE TRIGGER ALL ON DATABASE")]
    [InlineData("SELECT 1 RECONFIGURE")]
    [InlineData("SELECT 1 WRITETEXT t.a @p 'x'")]
    [InlineData("SELECT 1 UPDATETEXT t.a @p 0 0 'x'")]
    [InlineData("SELECT 1 SETUSER 'x'")]
    public void SqlServer_HiddenWrites_AreWrites(string sql) => Assert.True(ReadOnlyGuard.IsWrite(sql, DbKind.SqlServer));

    [Fact]
    public void SqlServer_ColumnsNamedEnable_StayReadable() =>
        Assert.False(ReadOnlyGuard.IsWrite("SELECT enable, disable FROM t", DbKind.SqlServer));
}
