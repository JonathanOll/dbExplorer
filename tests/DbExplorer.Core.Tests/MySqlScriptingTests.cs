using DbExplorer.Core.Model;
using DbExplorer.Core.Plans;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Tests;

public class MySqlSplitterTests
{
    private static string[] Texts(string script) => ScriptSplitter.Split(script, DbKind.MySql).Select(s => s.Text).ToArray();

    [Theory]
    [InlineData("SELECT 'a;b'; SELECT 2", "SELECT 'a;b'")]
    [InlineData("SELECT 'it\\'s;ok'; SELECT 2", "SELECT 'it\\'s;ok'")]
    [InlineData("SELECT \"a\\\";b\"; SELECT 2", "SELECT \"a\\\";b\"")]
    [InlineData("SELECT `a;``b`; SELECT 2", "SELECT `a;``b`")]
    [InlineData("SELECT 1 # a;b\n; SELECT 2", "SELECT 1")]
    [InlineData("SELECT 1 -- a;b\n; SELECT 2", "SELECT 1")]
    [InlineData("SELECT 1 /* a;b */; SELECT 2", "SELECT 1")]
    [InlineData("SELECT 1--1; SELECT 2", "SELECT 1--1")]
    [InlineData("SELECT 1 # x\r; still comment\n; SELECT 2", "SELECT 1")]
    [InlineData("SELECT a#;\n; SELECT 2", "SELECT a")]
    [InlineData("SELECT 1 /* a /* b */; SELECT 2", "SELECT 1")]
    public void Split_FollowsMySqlLexing(string script, string first) => Assert.Equal([first, "SELECT 2"], Texts(script));

    [Fact]
    public void Split_ExecutableCommentAloneIsAStatement()
    {
        Assert.Equal(["/*!40101 SET NAMES utf8mb4 */", "SELECT 2"], Texts("/*!40101 SET NAMES utf8mb4 */;\nSELECT 2;"));
        Assert.Equal(["/*M!100100 SET @a = ';' */"], Texts("/*M!100100 SET @a = ';' */;"));
    }

    [Theory]
    [InlineData("$$")]
    [InlineData("//")]
    [InlineData(";;")]
    public void Split_Delimiter_KeepsABodyWhole(string delimiter)
    {
        var script = $"DELIMITER {delimiter}\nCREATE PROCEDURE p()\nBEGIN\n  SELECT 1;\n\n  SELECT '{delimiter}';\nEND{delimiter}\nDELIMITER ;\nCALL p();";

        var expected = new[] { $"CREATE PROCEDURE p()\nBEGIN\n  SELECT 1;\n\n  SELECT '{delimiter}';\nEND", "CALL p()" };
        Assert.Equal(expected, Texts(script));
        // A blank line inside the body does not cut it either (Ctrl+Enter).
        Assert.Equal(expected, ScriptSplitter.SplitAtBlankLines(script, DbKind.MySql).Select(s => s.Text).ToArray());
        Assert.All(ScriptSplitter.Split(script, DbKind.MySql), s => Assert.Equal(s.Text, script[s.Start..s.End]));
    }

    [Fact]
    public void Split_DelimiterWordInsideAStatementIsAName()
    {
        Assert.Equal(["SELECT\ndelimiter x\nFROM t", "SELECT 2"], Texts("SELECT\ndelimiter x\nFROM t; SELECT 2"));
    }

    [Fact]
    public void Split_OtherEnginesIgnoreDelimiterLines()
    {
        Assert.Single(ScriptSplitter.Split("DELIMITER $$\nSELECT 1$$", DbKind.Postgres));
    }

    [Fact]
    public void BlankLine_EndsAStatement_ButNotInAHashComment()
    {
        var texts = ScriptSplitter.SplitAtBlankLines("SELECT 1 # c\n\nSELECT 2", DbKind.MySql).Select(s => s.Text);
        Assert.Equal(["SELECT 1", "SELECT 2"], texts);
    }
}

/// <summary>Security: what a read-only or production MySQL / MariaDB connection must refuse or confirm.</summary>
public class MySqlGuardTests
{
    private static bool IsWrite(string sql) => ReadOnlyGuard.IsWrite(sql, DbKind.MySql);

    [Theory]
    [InlineData("SELECT * FROM t")]
    [InlineData("select `delete`, 'drop table x', \"update\" from t # delete\n")]
    [InlineData("SELECT 1 -- DELETE FROM t")]
    [InlineData("SELECT 1 /* DELETE FROM t */")]
    [InlineData("SHOW TABLES")]
    [InlineData("SHOW CREATE TABLE t")]
    [InlineData("SHOW CREATE PROCEDURE p")]
    [InlineData("DESCRIBE t")]
    [InlineData("DESC t")]
    [InlineData("TABLE t")]
    [InlineData("EXPLAIN SELECT * FROM t")]
    [InlineData("EXPLAIN FORMAT=JSON SELECT * FROM t")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
    [InlineData("SELECT * FROM t FOR UPDATE")]
    [InlineData("SELECT * FROM t LOCK IN SHARE MODE")]
    [InlineData("SELECT @a := 1")]
    [InlineData("(SELECT 1) UNION (SELECT 2)")]
    [InlineData("SELECT 1; SELECT 2;")]
    public void Reads(string sql) => Assert.False(IsWrite(sql));

    [Theory]
    // plain writes and session / server changes
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("REPLACE INTO t VALUES (1)")]
    [InlineData("UPDATE t SET a = 1")]
    [InlineData("DELETE FROM t")]
    [InlineData("DROP TABLE t")]
    [InlineData("TRUNCATE t")]
    [InlineData("RENAME TABLE a TO b")]
    [InlineData("ALTER TABLE t ADD c int")]
    [InlineData("SET GLOBAL max_connections = 1")]
    [InlineData("SET @@global.read_only = 0")]
    [InlineData("SET SESSION TRANSACTION READ WRITE")]
    [InlineData("SET autocommit = 1")]
    [InlineData("START TRANSACTION READ WRITE")]
    [InlineData("COMMIT")]
    [InlineData("USE other")]
    [InlineData("CALL p()")]
    [InlineData("DO release_all_locks()")]
    [InlineData("LOAD DATA INFILE '/tmp/x' INTO TABLE t")]
    [InlineData("LOAD XML LOCAL INFILE 'x' INTO TABLE t")]
    [InlineData("LOCK TABLES t WRITE")]
    [InlineData("UNLOCK TABLES")]
    [InlineData("FLUSH PRIVILEGES")]
    [InlineData("KILL 12")]
    [InlineData("SHUTDOWN")]
    [InlineData("RESET MASTER")]
    [InlineData("PURGE BINARY LOGS BEFORE NOW()")]
    [InlineData("HANDLER t OPEN")]
    [InlineData("OPTIMIZE TABLE t")]
    [InlineData("ANALYZE TABLE t")]
    [InlineData("ANALYZE DELETE FROM t")]
    [InlineData("EXPLAIN ANALYZE DELETE FROM t USING t")]
    [InlineData("PREPARE s FROM 'DELETE FROM t'")]
    [InlineData("EXECUTE s")]
    [InlineData("EXECUTE IMMEDIATE 'DELETE FROM t'")]
    [InlineData("BEGIN NOT ATOMIC DELETE FROM t; END")]
    [InlineData("INSTALL PLUGIN x SONAME 'y'")]
    [InlineData("GRANT ALL ON *.* TO u")]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM t")]
    // files on the server
    [InlineData("SELECT * FROM t INTO OUTFILE '/tmp/x'")]
    [InlineData("SELECT * INTO DUMPFILE '/tmp/x' FROM t")]
    [InlineData("SELECT 1e1into outfile '/tmp/x'")]
    [InlineData("SELECT 1 INTO/**/OUTFILE '/tmp/x'")]
    [InlineData("SELECT a INTO @v FROM t")]
    [InlineData("TABLE t INTO OUTFILE '/tmp/x'")]
    [InlineData("SHOW TABLES INTO OUTFILE '/tmp/x'")]
    // executable comments: the server runs them (or not) by its version
    [InlineData("/*! DELETE FROM t */")]
    [InlineData("SELECT 1 /*!; DELETE FROM t */")]
    [InlineData("SELECT * FROM t /*!50000 INTO OUTFILE '/tmp/x' */")]
    [InlineData("SELECT 1 /*M! ; DELETE FROM t */")]
    [InlineData("SELECT 1 /*m!100100 ; DELETE FROM t */")]
    [InlineData("SELECT 1 /*!99999 ' */ ; DELETE FROM t; /* ' */")]
    // comment and quote traps: each hides a statement from a lexer that reads like another database
    [InlineData("SELECT 1--1; DELETE FROM t")]
    [InlineData("SELECT 1 # x\r'\n; DELETE FROM t; -- '")]
    [InlineData("SELECT 1 -- x\r'\n; DELETE FROM t; -- '")]
    [InlineData("SELECT a#'\n; DELETE FROM t; # '")]
    [InlineData("SELECT 1 /* a /* b */ ; DELETE FROM t")]
    [InlineData("SELECT $$; DELETE FROM t; SELECT $$")]
    [InlineData("SELECT $$'$$; DELETE FROM t; -- '")]
    [InlineData("SELECT $a$ \" $a$; DELETE FROM t; -- \"")]
    [InlineData("SELECT 1 --\u0085; DELETE FROM t")]
    [InlineData("SELECT 1 --\u00a0; DELETE FROM t")]
    [InlineData("SELECT [a; DELETE FROM t; SELECT b]")]
    // sql_mode: NO_BACKSLASH_ESCAPES, ANSI_QUOTES, MariaDB MSSQL brackets
    [InlineData("SELECT 'a\\'; DELETE FROM t; -- '")]
    [InlineData("SELECT \"a\\\"; DELETE FROM t; -- \"")]
    [InlineData("SELECT 'x\\'y', \"a\\\"; DELETE FROM t; -- \"")]
    [InlineData("SELECT [x'] ; DELETE FROM t ; -- ']")]
    [InlineData("SHOW TABLES WHERE 'a\\' = 1; DELETE FROM t; -- '")]
    // a client command the server does not know must not hide what follows
    [InlineData("SHOW TABLES;\nDELIMITER $$\nDELETE FROM t$$")]
    [InlineData("SELECT 1\0; DELETE FROM t")]
    // functions with effects
    [InlineData("SELECT GET_LOCK('a', 10)")]
    [InlineData("SELECT `release_all_locks` ()")]
    [InlineData("SELECT release_lock /* */ ('a')")]
    [InlineData("SELECT nextval(s)")]
    [InlineData("SELECT NEXT VALUE FOR s")]
    [InlineData("SELECT sys_exec('rm -rf /')")]
    [InlineData("SHOW TABLES WHERE get_lock('a', 1)")]
    [InlineData("SELECT spider_direct_sql('delete from t', '', 'srv')")]
    [InlineData("")]
    public void Writes(string sql)
    {
        Assert.True(IsWrite(sql));
        Assert.True(ProductionWrites.MayWrite(sql, DbKind.MySql));
        Assert.True(ReadOnlyGuard.IsWrite(sql));
    }

    [Theory]
    [InlineData("SELECT 1; SET GLOBAL x = 1")]
    [InlineData("SELECT 1 /*! , sleep(1) */")]
    [InlineData("CALL p()")]
    public void Production_ConfirmsEveryStatementThatMayWrite(string script)
    {
        var statements = ScriptSplitter.Split(script, DbKind.MySql).Select(s => s.Text);
        Assert.NotEmpty(ProductionWrites.In(statements, DbKind.MySql));
    }

    [Theory]
    [InlineData("1=1; DELETE FROM t")]
    [InlineData("1=1 INTO OUTFILE '/tmp/x'")]
    [InlineData("1=1 /*! ) ; DELETE FROM t */")]
    [InlineData("a = 'x\\'; DELETE FROM t; -- '")]
    [InlineData("DELIMITER $$\n1=1; DELETE FROM t$$")]
    public void RawWhere_Refused(string where) =>
        Assert.Throws<Core.Querying.RawWhereException>(() => Core.Querying.TableDataLoader.PrepareRawWhere(where, DbKind.MySql));
}

public class MySqlPlanCaptureTests
{
    [Fact]
    public void Wrap_PerEngineAndMode()
    {
        Assert.Equal("EXPLAIN FORMAT=JSON SELECT 1", PlanCapture.Wrap("SELECT 1", DbKind.MySql, PlanMode.Estimated));
        Assert.Equal("EXPLAIN FORMAT=JSON SELECT 1", PlanCapture.Wrap("SELECT 1", DbKind.MySql, PlanMode.Estimated, mariaDb: true));
        Assert.Equal("EXPLAIN ANALYZE FORMAT=TREE SELECT 1", PlanCapture.Wrap("SELECT 1", DbKind.MySql, PlanMode.Actual));
        Assert.Equal("ANALYZE FORMAT=JSON SELECT 1", PlanCapture.Wrap("SELECT 1", DbKind.MySql, PlanMode.Actual, mariaDb: true));
        Assert.Equal(PlanMode.Actual, PlanCapture.Effective(DbKind.MySql, PlanMode.Actual));
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("  # c\n(SELECT 1) UNION SELECT 2")]
    [InlineData("DELETE FROM t")]
    [InlineData("SELECT ';'")]
    public void Estimated_Accepts(string sql) => Assert.Null(PlanCapture.CannotPlan(sql, DbKind.MySql, PlanMode.Estimated));

    // An estimated plan skips the read-only and production checks: nothing but the explained statement may reach the server.
    [Theory]
    [InlineData("SELECT 1; DELETE FROM t")]
    [InlineData("SELECT 'a\\'; DELETE FROM t; -- '")]
    [InlineData("/*! ANALYZE */ DELETE FROM t")]
    [InlineData("SELECT 1 /*!; DELETE FROM t */")]
    [InlineData("ANALYZE DELETE FROM t")]
    [InlineData("ANALYZE SELECT 1")]
    [InlineData("--x\nANALYZE DELETE FROM t")]
    [InlineData("SET GLOBAL x = 1")]
    [InlineData("CALL p()")]
    [InlineData("SELECT 1\0")]
    [InlineData("SELECT $$'$$; DELETE FROM t; -- '")]
    [InlineData("SELECT 1 --\u0085; DELETE FROM t")]
    public void Estimated_Refuses(string sql) => Assert.NotNull(PlanCapture.CannotPlan(sql, DbKind.MySql, PlanMode.Estimated));

    [Fact]
    public void Actual_MySqlOnlyMeasuresReads_MariaDbAlsoChanges()
    {
        Assert.Contains("MySQL only measures", PlanCapture.CannotPlan("DELETE FROM t", DbKind.MySql, PlanMode.Actual));
        Assert.Null(PlanCapture.CannotPlan("DELETE FROM t", DbKind.MySql, PlanMode.Actual, mariaDb: true));
        Assert.Null(PlanCapture.CannotPlan("SELECT 1", DbKind.MySql, PlanMode.Actual));
    }
}

public class MySqlEditorTests
{
    private static readonly TableInfo Orders = new(new TableRef(null, "orders"), false,
        [new ColumnInfo("id", "int", typeof(int), false, true, 0), new ColumnInfo("rank", "int", typeof(int), true, false, 1),
         new ColumnInfo("Order Date", "date", typeof(DateTime), true, false, 2)], [], []);

    private static IReadOnlyList<CompletionItem> Complete(string text) =>
        SqlCompletion.Complete(text, text.Length, new TableListSchema([Orders]), DbKind.MySql);

    [Fact]
    public void Completion_QuotesWithBackticks()
    {
        var columns = Complete("SELECT * FROM orders o WHERE o.");
        Assert.Contains(columns, i => i.Text == "`rank`");
        Assert.Contains(columns, i => i.Text == "`Order Date`");
        Assert.Contains(columns, i => i.Text == "id");
    }

    [Fact]
    public void Completion_CompletesAnOpenBacktick_NotADoubleQuotedString()
    {
        Assert.Contains(Complete("SELECT * FROM `ord"), i => i.Text == "`orders`");
        Assert.Empty(Complete("SELECT \"ord"));
        Assert.Empty(Complete("SELECT 1 # ord"));
    }

    [Fact]
    public void Completion_OffersMySqlWords() => Assert.Contains(Complete("SHO"), i => i.Text == "SHOW");

    [Theory]
    [InlineData("select a from t where b = 'x\\'y' # c\n;")]
    [InlineData("select `A B`, \"s\" from t -- c\nwhere 1--1 = 2;")]
    public void Format_KeepsStringsAndComments(string sql)
    {
        var formatted = SqlFormatter.Format(sql, DbKind.MySql);
        Assert.StartsWith("SELECT", formatted);
        foreach (var kept in new[] { "'x\\'y'", "# c", "`A B`", "-- c", "1--1" }.Where(sql.Contains))
            Assert.Contains(kept.Replace("1--1", "1"), formatted);
        Assert.Equal(ScriptSplitter.Split(sql, DbKind.MySql).Count, ScriptSplitter.Split(formatted, DbKind.MySql).Count);
    }

    [Theory]
    [InlineData("DELIMITER $$\ncreate procedure p() begin select 1; end$$\nDELIMITER ;")]
    [InlineData("/*!40101 set names utf8 */;")]
    public void Format_LeavesDelimiterScriptsAndExecutableCommentsAlone(string sql) =>
        Assert.Equal(sql, SqlFormatter.Format(sql, DbKind.MySql));

    [Fact]
    public void AutoPairs_Backtick_OnlyForMySql()
    {
        Assert.True(AutoPairs.ShouldClose("SELECT ", 7, '`', DbKind.MySql));
        Assert.False(AutoPairs.ShouldClose("SELECT ", 7, '`', DbKind.Postgres));
        Assert.False(AutoPairs.ShouldClose("SELECT 1 # ", 11, '`', DbKind.MySql));
    }
}
