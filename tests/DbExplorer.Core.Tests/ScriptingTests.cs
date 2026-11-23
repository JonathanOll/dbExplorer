using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

public class ScriptSplitterTests
{
    private static string[] Texts(string script, DbKind kind) =>
        ScriptSplitter.Split(script, kind).Select(s => s.Text).ToArray();

    [Fact]
    public void Split_Semicolons()
    {
        var script = "SELECT 1; SELECT 2;";
        var result = ScriptSplitter.Split(script, DbKind.Sqlite);

        Assert.Equal(2, result.Count);
        Assert.Equal("SELECT 1", result[0].Text);
        Assert.Equal(0, result[0].Start);
        Assert.Equal(8, result[0].End);
        Assert.Equal("SELECT 2", result[1].Text);
        Assert.Equal(10, result[1].Start);
        Assert.Equal(18, result[1].End);
        Assert.All(result, s => Assert.Equal(s.Text, script.Substring(s.Start, s.End - s.Start)));
    }

    [Theory]
    [InlineData(DbKind.Sqlite, "SELECT 'a;b'; SELECT 2", "SELECT 'a;b'")]
    [InlineData(DbKind.Sqlite, "SELECT 'it''s;ok'; SELECT 2", "SELECT 'it''s;ok'")]
    [InlineData(DbKind.Sqlite, "SELECT \"x;y\"; SELECT 2", "SELECT \"x;y\"")]
    [InlineData(DbKind.Sqlite, "SELECT 1 -- a;b\n; SELECT 2", "SELECT 1")]
    [InlineData(DbKind.Sqlite, "SELECT /* a;b */ 1; SELECT 2", "SELECT /* a;b */ 1")]
    [InlineData(DbKind.SqlServer, "SELECT [a;b]; SELECT 2", "SELECT [a;b]")]
    [InlineData(DbKind.Postgres, "DO $$ BEGIN PERFORM 1; END $$; SELECT 2", "DO $$ BEGIN PERFORM 1; END $$")]
    [InlineData(DbKind.Postgres, "SELECT $tag$ a;$$;b $tag$; SELECT 2", "SELECT $tag$ a;$$;b $tag$")]
    [InlineData(DbKind.Postgres, "SELECT 1 /* a /* nested; */ b; */; SELECT 2", "SELECT 1")]
    [InlineData(DbKind.Postgres, "SELECT E'it\\'s;ok'; SELECT 2", "SELECT E'it\\'s;ok'")]
    public void Split_IgnoresSemicolonsInStringsCommentsAndDollarQuotes(DbKind kind, string script, string expectedFirst)
    {
        var texts = Texts(script, kind);

        Assert.Equal(2, texts.Length);
        Assert.Equal(expectedFirst, texts[0]);
        Assert.Equal("SELECT 2", texts[1]);
    }

    [Fact]
    public void Split_SqlServerNestedBlockComments()
    {
        Assert.Equal(new[] { "SELECT 1", "SELECT 2" },
            Texts("SELECT 1 /* a /* b */ ; c */; SELECT 2", DbKind.SqlServer));

        // SQLite comments do not nest: the first */ ends the comment.
        Assert.Equal(new[] { "SELECT 1", "c */", "SELECT 2" },
            Texts("SELECT 1 /* a /* b */ ; c */; SELECT 2", DbKind.Sqlite));
    }

    [Fact]
    public void Split_PostgresParameterIsNotADollarQuote()
    {
        Assert.Equal(new[] { "SELECT $1", "SELECT $2" }, Texts("SELECT $1; SELECT $2", DbKind.Postgres));
    }

    [Fact]
    public void Split_KeepsLeadingCommentWithItsStatement_AndDropsCommentOnlyTail()
    {
        var texts = Texts("-- first\nSELECT 1;\n-- done\n", DbKind.Sqlite);

        Assert.Equal(new[] { "-- first\nSELECT 1" }, texts);
    }

    [Fact]
    public void Split_SqlServerGo_OnOwnLineOnly()
    {
        Assert.Equal(new[] { "SELECT 1", "SELECT 2", "SELECT 3" },
            Texts("SELECT 1\nGO\nSELECT 2\r\n  go  \r\nSELECT 3\nGo", DbKind.SqlServer));

        // GO with a repeat count and a trailing comment is still a separator.
        Assert.Equal(new[] { "SELECT 1", "SELECT 2" },
            Texts("SELECT 1\nGO 5 -- repeat\nSELECT 2", DbKind.SqlServer));

        // Not a separator: inside a string, as an identifier, as an alias, or not alone on its line.
        Assert.Single(Texts("SELECT 'a\nGO\nb'", DbKind.SqlServer));
        Assert.Single(Texts("SELECT 1 AS [GO]", DbKind.SqlServer));
        Assert.Single(Texts("SELECT 1 AS go", DbKind.SqlServer));
        Assert.Single(Texts("SELECT 1\nGO SELECT 2", DbKind.SqlServer));
        Assert.Single(Texts("SELECT 1\n/*\nGO\n*/", DbKind.SqlServer));
        Assert.Single(Texts("SELECT 1\nGOTO label", DbKind.SqlServer));
    }

    [Fact]
    public void Split_Go_IsNotASeparatorOutsideSqlServer()
    {
        Assert.Single(Texts("SELECT 1\nGO\nSELECT 2", DbKind.Postgres));
        Assert.Single(Texts("SELECT 1\nGO\nSELECT 2", DbKind.Sqlite));
    }

    [Fact]
    public void Split_TrailingWithoutSemicolon()
    {
        var result = ScriptSplitter.Split("SELECT 1;\nSELECT 2  \n", DbKind.Sqlite);

        Assert.Equal(2, result.Count);
        Assert.Equal("SELECT 2", result[1].Text);
        Assert.Equal(10, result[1].Start);
        Assert.Equal(18, result[1].End);
    }

    [Fact]
    public void Split_EmptyAndSeparatorOnly_YieldsNothing()
    {
        Assert.Empty(ScriptSplitter.Split("", DbKind.Sqlite));
        Assert.Empty(ScriptSplitter.Split("  ;;\n ; -- c\n", DbKind.Sqlite));
    }

    [Fact]
    public void StatementAt_CursorInsideAndBetween()
    {
        //            0         1         2
        //            012345678901234567890123
        var script = "SELECT 1;  SELECT 22;\n\n";
        var stmts = ScriptSplitter.Split(script, DbKind.Sqlite);
        Assert.Equal(2, stmts.Count);

        // Inside the statements, including their boundaries.
        Assert.Equal("SELECT 1", ScriptSplitter.StatementAt(stmts, 0)!.Text);
        Assert.Equal("SELECT 1", ScriptSplitter.StatementAt(stmts, 4)!.Text);
        Assert.Equal("SELECT 1", ScriptSplitter.StatementAt(stmts, 8)!.Text);
        Assert.Equal("SELECT 22", ScriptSplitter.StatementAt(stmts, 12)!.Text);
        Assert.Equal("SELECT 22", ScriptSplitter.StatementAt(stmts, 20)!.Text);

        // Between statements and after the last one: the statement before the cursor.
        Assert.Equal("SELECT 1", ScriptSplitter.StatementAt(stmts, 9)!.Text);
        Assert.Equal("SELECT 1", ScriptSplitter.StatementAt(stmts, 10)!.Text);
        Assert.Equal("SELECT 22", ScriptSplitter.StatementAt(stmts, 21)!.Text);
        Assert.Equal("SELECT 22", ScriptSplitter.StatementAt(stmts, script.Length)!.Text);
    }

    [Fact]
    public void StatementAt_BeforeFirst_ReturnsFirst_AndEmptyReturnsNull()
    {
        var stmts = ScriptSplitter.Split("\n\n  SELECT 1", DbKind.Sqlite);

        Assert.Equal("SELECT 1", ScriptSplitter.StatementAt(stmts, 0)!.Text);
        Assert.Null(ScriptSplitter.StatementAt(Array.Empty<ScriptStatement>(), 0));
    }
}

public class ReadOnlyGuardTests
{
    [Theory]
    [InlineData("SELECT * FROM t")]
    [InlineData("  select 1")]
    [InlineData("(SELECT 1) UNION (SELECT 2)")]
    [InlineData("-- hi\nSELECT 1")]
    [InlineData("/* hi */ SELECT 1")]
    [InlineData("SELECT 'delete me', \"update\" FROM t")]
    [InlineData("SELECT * FROM t FOR UPDATE")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
    [InlineData("WITH x AS (SELECT 'delete') SELECT * FROM x")]
    [InlineData("EXPLAIN SELECT 1")]
    [InlineData("EXPLAIN QUERY PLAN SELECT 1")]
    [InlineData("SHOW search_path")]
    [InlineData("VALUES (1), (2)")]
    [InlineData("PRAGMA table_info(t)")]
    [InlineData("PRAGMA user_version")]
    [InlineData("SELECT 1;")]
    public void Guard_Select_Allowed(string sql) => Assert.False(ReadOnlyGuard.IsWrite(sql));

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("update t set a = 1")]
    [InlineData("DELETE FROM t")]
    [InlineData("DROP TABLE t")]
    [InlineData("CREATE TABLE t (a int)")]
    [InlineData("ALTER TABLE t ADD b int")]
    [InlineData("TRUNCATE t")]
    [InlineData("MERGE INTO t USING s ON 1=1 WHEN MATCHED THEN DELETE")]
    [InlineData("EXEC sp_foo")]
    [InlineData("CALL p()")]
    [InlineData("PRAGMA user_version = 3")]
    [InlineData("SELECT 1\nDELETE FROM t")]
    [InlineData("SELECT 1 UPDATE t SET a = 1")]
    [InlineData("SELECT 1 DROP TABLE t")]
    [InlineData("SELECT 1 EXEC sp_x")]
    [InlineData("SELECT 1 INSERT INTO t VALUES (1)")]
    [InlineData("VALUES (1) DELETE FROM t")]
    [InlineData("SHOW a DELETE FROM t")]
    [InlineData("SELECT * INTO copy FROM t")]
    [InlineData("EXPLAIN ANALYZE DELETE FROM t")]
    [InlineData("")]
    [InlineData("-- nothing but a comment")]
    public void Guard_Insert_Update_Delete_Drop_Refused(string sql) => Assert.True(ReadOnlyGuard.IsWrite(sql));

    [Theory]
    [InlineData("-- harmless\nDELETE FROM t")]
    [InlineData("/* harmless */ DELETE FROM t")]
    [InlineData("/* a */ -- b\n /* c */ update t set a = 1")]
    public void Guard_CommentThenDelete_Refused(string sql) => Assert.True(ReadOnlyGuard.IsWrite(sql));

    [Theory]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM t WHERE a IN (SELECT * FROM x)")]
    [InlineData("WITH x AS (DELETE FROM t RETURNING *) SELECT * FROM x")]
    [InlineData("with x as (select 1) update t set a = 1")]
    [InlineData("WITH x AS (SELECT 1) INSERT INTO t SELECT * FROM x")]
    public void Guard_WithCteFollowedByDelete_Refused(string sql) => Assert.True(ReadOnlyGuard.IsWrite(sql));

    [Theory]
    [InlineData("PRAGMA table_info(t)")]
    [InlineData("PRAGMA main.table_info('t')")]
    [InlineData("PRAGMA index_list(t)")]
    [InlineData("PRAGMA foreign_key_list(t)")]
    [InlineData("PRAGMA database_list")]
    [InlineData("PRAGMA integrity_check")]
    [InlineData("PRAGMA integrity_check(10)")]
    [InlineData("PRAGMA user_version")]
    [InlineData("PRAGMA main.journal_mode")]
    public void Guard_ReadPragmas_Allowed(string sql) => Assert.False(ReadOnlyGuard.IsWrite(sql));

    [Theory]
    [InlineData("PRAGMA user_version(5)")]
    [InlineData("PRAGMA user_version = 5")]
    [InlineData("PRAGMA journal_mode(DELETE)")]
    [InlineData("PRAGMA writable_schema(1)")]
    [InlineData("PRAGMA writable_schema")]
    [InlineData("PRAGMA main.user_version(5)")]
    [InlineData("PRAGMA wal_checkpoint")]
    [InlineData("PRAGMA optimize")]
    [InlineData("PRAGMA")]
    public void Guard_WritePragmas_Refused(string sql) => Assert.True(ReadOnlyGuard.IsWrite(sql));

    [Fact]
    public void Guard_PostgresEscapeString_DoesNotHideInto()
    {
        // In Postgres E'\'' is a one-character string; the quote after the backslash does not close it.
        const string sql = "SELECT E'\\'' INTO x FROM t";

        Assert.True(ReadOnlyGuard.IsWrite(sql, DbKind.Postgres));
        Assert.True(ReadOnlyGuard.IsWrite(sql));   // kind-agnostic: the union of all dialects
    }

    [Fact]
    public void Guard_KindAgnostic_NeverTreatsAsOpaqueWhatAnyDialectSees()
    {
        // Dollar quotes exist only in Postgres, nested comments only in Postgres/SQL Server.
        Assert.True(ReadOnlyGuard.IsWrite("SELECT $$; DELETE FROM t; $$"));
        Assert.True(ReadOnlyGuard.IsWrite("SELECT 1 /* a /* b */ DELETE FROM t */"));
    }

    [Fact]
    public void Guard_SqlServerNestedBlockComment_IsOneComment()
    {
        const string sql = "SELECT 1 /* a /* b */ DELETE FROM t */";

        Assert.False(ReadOnlyGuard.IsWrite(sql, DbKind.SqlServer));
        Assert.False(ReadOnlyGuard.IsWrite(sql, DbKind.Postgres));
        Assert.True(ReadOnlyGuard.IsWrite(sql, DbKind.Sqlite));   // SQLite comments do not nest
    }

    [Fact]
    public void Guard_WriteHiddenAfterSemicolon_Refused()
    {
        Assert.True(ReadOnlyGuard.IsWrite("SELECT 1; DELETE FROM t"));
        Assert.True(ReadOnlyGuard.IsWrite("SELECT 'a;b'; /* c */ DROP TABLE t;"));
        Assert.False(ReadOnlyGuard.IsWrite("SELECT 'a;b'; SELECT 2;"));
    }

    [Theory]
    [InlineData("SELECT pg_terminate_backend(123)", DbKind.Postgres)]
    [InlineData("SELECT pg_catalog.pg_cancel_backend(1)", DbKind.Postgres)]
    [InlineData("select PG_ADVISORY_LOCK(1)", DbKind.Postgres)]
    [InlineData("SELECT pg_try_advisory_lock_shared (1)", DbKind.Postgres)]
    [InlineData("SELECT dblink_exec('c','DELETE FROM t')", DbKind.Postgres)]
    [InlineData("SELECT * FROM dblink('c','select 1') AS t(a int)", DbKind.Postgres)]
    [InlineData("SELECT lo_export(1,'/tmp/x')", DbKind.Postgres)]
    [InlineData("SELECT \"pg_terminate_backend\"(1)", DbKind.Postgres)]
    [InlineData("SELECT NEXT VALUE FOR dbo.seq", DbKind.SqlServer)]
    [InlineData("SELECT * FROM OPENQUERY(srv,'SELECT 1')", DbKind.SqlServer)]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI','x','SELECT 1')", DbKind.SqlServer)]
    [InlineData("SELECT * FROM [OPENDATASOURCE]('a','b').d.s.t", DbKind.SqlServer)]
    public void Guard_SideEffectFunctions_Refused(string sql, DbKind kind)
    {
        Assert.True(ReadOnlyGuard.IsWrite(sql, kind));
        Assert.True(ReadOnlyGuard.IsWrite(sql));   // kind-agnostic: also refused
    }

    [Theory]
    [InlineData("pg_stat_reset_subscription_stats")]
    [InlineData("pg_stat_reset_backend_stats")]
    [InlineData("pg_copy_physical_replication_slot")]
    [InlineData("pg_copy_logical_replication_slot")]
    [InlineData("pg_logical_slot_get_changes")]
    [InlineData("pg_logical_slot_get_binary_changes")]
    public void Guard_MoreSideEffectFunctions_Refused(string function)
    {
        Assert.True(ReadOnlyGuard.IsWrite($"SELECT {function}(1)", DbKind.Postgres));
        Assert.True(ReadOnlyGuard.IsWrite($"SELECT * FROM {function}('s', NULL, NULL)"));
    }

    [Theory]
    [InlineData("SELECT * FROM pg_logical_slot_peek_changes('s', NULL, NULL)")]
    [InlineData("SELECT * FROM pg_logical_slot_peek_binary_changes('s', NULL, NULL)")]
    public void Guard_PeekingALogicalSlot_Allowed(string sql) => Assert.False(ReadOnlyGuard.IsWrite(sql, DbKind.Postgres));

    [Theory]
    [InlineData("SELECT pg_terminate_backend /*x*/ (1)")]
    [InlineData("SELECT pg_terminate_backend -- x\n(1)")]
    [InlineData("SELECT pg_terminate_backend\n  /* a */ -- b\n  (1)")]
    public void Guard_CommentBetweenNameAndParenthesis_StillRefused(string sql)
    {
        Assert.True(ReadOnlyGuard.IsWrite(sql, DbKind.Postgres));
        Assert.True(ReadOnlyGuard.IsWrite(sql));
    }

    [Theory]
    [InlineData("SELECT dblink FROM t", DbKind.Postgres)]
    [InlineData("SELECT 'pg_terminate_backend(1)'", DbKind.Postgres)]
    [InlineData("SELECT pid FROM pg_stat_activity", DbKind.Postgres)]
    [InlineData("SELECT pg_try_advisory_xact_lock(1)", DbKind.Postgres)]
    [InlineData("SELECT * FROM t ORDER BY id OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY", DbKind.SqlServer)]
    [InlineData("SELECT * FROM t ORDER BY id OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY", DbKind.Postgres)]
    [InlineData("SELECT next FROM t", DbKind.SqlServer)]
    [InlineData("SELECT next, value, for_x FROM t", DbKind.Postgres)]
    public void Guard_SideEffectNamesUsedAsPlainText_Allowed(string sql, DbKind kind)
    {
        Assert.False(ReadOnlyGuard.IsWrite(sql, kind));
        Assert.False(ReadOnlyGuard.IsWrite(sql));
    }
}

public class ScriptRunnerTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");

    public ScriptRunnerTests()
    {
        _conn.Open();
        Exec("CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT)");
        Exec("INSERT INTO t (id, name) VALUES (1, 'alpha'), (2, NULL), (3, 'gamma')");
    }

    public void Dispose() => _conn.Dispose();

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private async Task<List<StatementResult>> RunAsync(string script, ScriptOptions? options = null, CancellationToken ct = default)
    {
        var results = new List<StatementResult>();
        var stmts = ScriptSplitter.Split(script, DbKind.Sqlite);
        await foreach (var r in ScriptRunner.RunAsync(_conn, stmts, options ?? new ScriptOptions(), ct))
            results.Add(r);
        return results;
    }

    [Fact]
    public async Task Run_ReturnsRowsAndColumns()
    {
        var results = await RunAsync("SELECT id, name FROM t ORDER BY id");

        var r = Assert.Single(results);
        Assert.Null(r.Error);
        Assert.Equal("SELECT id, name FROM t ORDER BY id", r.Statement.Text);
        var set = Assert.Single(r.Sets);
        Assert.Equal(new[] { "id", "name" }, set.Columns);
        Assert.False(set.Truncated);
        Assert.Equal(3, set.Rows.Count);
        Assert.Equal(1L, set.Rows[0][0]);
        Assert.Equal("alpha", set.Rows[0][1]);
        Assert.Null(set.Rows[1][1]);   // DBNull is surfaced as null
        Assert.True(r.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Run_MultipleResultSets()
    {
        var results = await RunAsync("SELECT 1 AS a; SELECT 2 AS b, 3 AS c; SELECT name FROM t WHERE id = 3");

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Null(r.Error));
        Assert.Equal(new[] { "a" }, results[0].Sets[0].Columns);
        Assert.Equal(new[] { "b", "c" }, results[1].Sets[0].Columns);
        Assert.Equal("gamma", results[2].Sets[0].Rows[0][0]);
    }

    [Fact]
    public async Task Run_Dml_ReportsRowsAffectedAndNoSets()
    {
        var results = await RunAsync("UPDATE t SET name = 'x' WHERE id IN (1, 3); CREATE TABLE u (a int); SELECT 1");

        Assert.Equal(3, results.Count);
        Assert.Empty(results[0].Sets);
        Assert.Equal(2, results[0].RowsAffected);
        Assert.Empty(results[1].Sets);
        Assert.Null(results[2].RowsAffected);
        Assert.Single(results[2].Sets);
    }

    [Fact]
    public async Task Run_TruncatesAtMaxRows()
    {
        const string cte = "WITH RECURSIVE c(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM c WHERE n < {0}) SELECT n FROM c";

        var truncated = await RunAsync(string.Format(cte, 10), new ScriptOptions(MaxRows: 3));
        var set = Assert.Single(Assert.Single(truncated).Sets);
        Assert.Equal(3, set.Rows.Count);
        Assert.True(set.Truncated);

        var exact = await RunAsync(string.Format(cte, 3), new ScriptOptions(MaxRows: 3));
        set = Assert.Single(Assert.Single(exact).Sets);
        Assert.Equal(3, set.Rows.Count);
        Assert.False(set.Truncated);
    }

    [Fact]
    public async Task Run_ErrorStopsScript_AndReportsMessage()
    {
        var results = await RunAsync("SELECT 1; SELECT * FROM no_such_table; SELECT 3");

        Assert.Equal(2, results.Count);
        Assert.Null(results[0].Error);
        Assert.Contains("no_such_table", results[1].Error);
        Assert.Empty(results[1].Sets);
        Assert.Equal("SELECT * FROM no_such_table", results[1].Statement.Text);
    }

    [Fact]
    public async Task Run_ReadOnly_RefusesWrites_WithoutExecuting()
    {
        var results = await RunAsync(
            "SELECT 1; DELETE FROM t; SELECT 3",
            new ScriptOptions(ReadOnly: true));

        Assert.Equal(2, results.Count);
        Assert.Null(results[0].Error);
        Assert.NotNull(results[1].Error);
        Assert.Contains("read-only", results[1].Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, Scalar("SELECT COUNT(*) FROM t"));
    }

    [Fact]
    public async Task Run_ReadOnly_WithKnownKind_UsesThatDialect()
    {
        var results = await RunAsync("SELECT E'\\'' INTO x FROM t", new ScriptOptions(ReadOnly: true, Kind: DbKind.Postgres));

        var r = Assert.Single(results);
        Assert.Contains("read-only", r.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_ReadOnly_StillAllowsSelects()
    {
        var results = await RunAsync("SELECT 1; SELECT name FROM t", new ScriptOptions(ReadOnly: true));

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Null(r.Error));
    }

    [Fact]
    public async Task Run_Cancel_DoesNotThrow()
    {
        // Endless recursive CTE: only cancellation can end it.
        const string endless = "WITH RECURSIVE c(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM c) SELECT n FROM c";
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        var run = RunAsync(endless + "; SELECT 2", new ScriptOptions(MaxRows: int.MaxValue), cts.Token);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.Same(run, finished);
        var results = await run;   // must not throw
        Assert.DoesNotContain(results, r => r.Error is not null);
        Assert.DoesNotContain(results, r => r.Statement.Text == "SELECT 2");
    }

    [Fact]
    public async Task Run_AlreadyCancelled_RunsNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var results = await RunAsync("SELECT 1", ct: cts.Token);

        Assert.Empty(results);
    }
}
