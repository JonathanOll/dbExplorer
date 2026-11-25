using System.Text.RegularExpressions;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Tests;

public class SqlFormatterTests
{
    private static string F(string sql, DbKind kind = DbKind.Postgres, SqlFormatOptions? o = null) => SqlFormatter.Format(sql, kind, o);

    private static void Same(string expected, string sql, DbKind kind = DbKind.Postgres, SqlFormatOptions? o = null)
    {
        var actual = F(sql, kind, o);
        Assert.Equal(expected.ReplaceLineEndings("\n"), actual);
        // Every expectation is also an idempotence check.
        Assert.Equal(actual, F(actual, kind, o));
    }

    // ------------------------------------------------------------------ clauses

    [Fact]
    public void Select_ListGoesOneItemPerLine()
    {
        Same("""
            SELECT
                a,
                b,
                c
            FROM t
            WHERE a = 1
                AND b IN (1, 2, 3)
            ORDER BY a DESC
            LIMIT 10
            """, "select a,b,c from t where a=1 and b in (1,2,3) order by a desc limit 10");
    }

    [Fact]
    public void Select_SingleItemStaysOnTheKeywordLine()
    {
        Same("SELECT *\nFROM t", "select * from t");
        Same("SELECT count(*)\nFROM t\nWHERE x > 1", "select count(*) from t where x>1");
    }

    [Fact]
    public void Select_Distinct_And_DistinctOn_StayOnTheKeywordLine()
    {
        Same("SELECT DISTINCT\n    a,\n    b\nFROM t", "select distinct a, b from t");
        Same("SELECT DISTINCT ON (a)\n    a,\n    b\nFROM t\nORDER BY a, b\nLIMIT 5\nOFFSET 10", "select distinct on (a) a, b from t order by a, b limit 5 offset 10");
    }

    [Fact]
    public void Select_TSqlTop()
    {
        Same("SELECT TOP 10\n    a,\n    b\nFROM t", "select top 10 a, b from t", DbKind.SqlServer);
        Same("SELECT TOP (5) PERCENT\n    a,\n    b\nFROM t", "select top (5) percent a, b from t", DbKind.SqlServer);
        Same("SELECT TOP 3 WITH TIES\n    a,\n    b\nFROM t\nORDER BY a", "select top 3 with ties a, b from t order by a", DbKind.SqlServer);
    }

    [Fact]
    public void Top_IsAnIdentifierOutsideSqlServer()
    {
        Same("SELECT top\nFROM t", "select top from t");
    }

    [Fact]
    public void GroupBy_Having_OrderBy_StayInlineWhenShort()
    {
        Same("""
            SELECT
                a,
                count(*) AS n
            FROM t
            GROUP BY a, b
            HAVING count(*) > 1
            ORDER BY n DESC, a
            """, "select a, count(*) as n from t group by a, b having count(*)>1 order by n desc, a");
    }

    [Fact]
    public void Joins_PutTheConditionOnAnIndentedLine()
    {
        Same("""
            SELECT 1
            FROM users u
            LEFT JOIN orders o
                ON o.user_id = u.id
                AND o.deleted = FALSE
            INNER JOIN items i
                ON i.order_id = o.id
            CROSS JOIN z
            """, "select 1 from users u left join orders o on o.user_id=u.id and o.deleted=false inner join items i on i.order_id=o.id cross join z");
    }

    [Fact]
    public void Joins_FullOuterAndNatural()
    {
        Same("SELECT 1\nFROM a\nFULL OUTER JOIN b\n    ON a.x = b.x\nNATURAL JOIN c", "select 1 from a full outer join b on a.x=b.x natural join c");
        Same("SELECT 1\nFROM a\nJOIN b\n    USING (id)", "select 1 from a join b using (id)");
    }

    [Fact]
    public void Joins_TSqlApply()
    {
        Same("""
            SELECT a.*
            FROM a
            CROSS APPLY f(a.id) AS q
            OUTER APPLY (
                SELECT TOP 1 *
                FROM w
                WHERE w.id = a.id
                ORDER BY 1
            ) AS ww
            """, "select a.* from a cross apply f(a.id) as q outer apply (select top 1 * from w where w.id=a.id order by 1) as ww", DbKind.SqlServer);
    }

    [Fact]
    public void From_CommaList_BreaksOnePerLine()
    {
        Same("SELECT 1\nFROM\n    a,\n    b\nWHERE a.x = b.x", "select 1 from a, b where a.x=b.x");
    }

    [Fact]
    public void Where_AndOrStartTheLine_AndBetweenKeepsItsAnd()
    {
        Same("""
            SELECT *
            FROM t
            WHERE a BETWEEN 1 AND 2
                AND b NOT BETWEEN 3 AND 4
                OR c IS DISTINCT FROM d
            """, "select * from t where a between 1 and 2 and b not between 3 and 4 or c is distinct from d");
    }

    [Fact]
    public void Where_ParenthesisedConditionStaysInlineWhenShort()
    {
        Same("SELECT *\nFROM t\nWHERE u.active\n    AND (u.age BETWEEN 18 AND 65 OR u.vip)", "select * from t where u.active and (u.age between 18 and 65 or u.vip)");
    }

    [Fact]
    public void Where_LongParenthesisedConditionBreaksAtAndOr()
    {
        var sql = "select * from t where (aaaaaaaaaaaaaaaaaaaa = 1 and bbbbbbbbbbbbbbbbbbbb = 2 and cccccccccccccccccccc = 3 and dddddddddddddddddddd = 4 and eeeeeeeeeeeeeeeeeeee = 5)";
        Same("""
            SELECT *
            FROM t
            WHERE (
                aaaaaaaaaaaaaaaaaaaa = 1
                AND bbbbbbbbbbbbbbbbbbbb = 2
                AND cccccccccccccccccccc = 3
                AND dddddddddddddddddddd = 4
                AND eeeeeeeeeeeeeeeeeeee = 5
            )
            """, sql);
    }

    [Fact]
    public void In_LongList_WrapsAtMaxLineWidth()
    {
        var items = string.Join(",", Enumerable.Range(1, 40));
        var actual = F($"select * from t where id in ({items})");
        var lines = actual.Split('\n');
        Assert.Equal("SELECT *", lines[0]);
        Assert.Equal("WHERE id IN (", lines[2]);
        Assert.Equal(")", lines[^1]);
        Assert.All(lines, l => Assert.True(l.Length <= 100, l));
        Assert.True(lines.Length > 4);
    }

    [Fact]
    public void Union_IntersectExcept_StartTheirOwnLine()
    {
        Same("SELECT a\nFROM t\nUNION ALL\nSELECT b\nFROM u\nEXCEPT\nSELECT c\nFROM v\nORDER BY 1", "select a from t union all select b from u except select c from v order by 1");
    }

    [Fact]
    public void Limit_Offset_Fetch()
    {
        Same("SELECT a\nFROM t\nORDER BY a\nOFFSET 10 ROWS\nFETCH NEXT 5 ROWS ONLY", "select a from t order by a offset 10 rows fetch next 5 rows only", DbKind.SqlServer);
    }

    // ------------------------------------------------------------------ CTE, subqueries

    [Fact]
    public void With_OneBlockPerCte()
    {
        Same("""
            WITH a AS (
                SELECT 1 AS x
            ),
            b AS (
                SELECT x
                FROM a
                WHERE x > 0
            )
            SELECT *
            FROM b
            UNION ALL
            SELECT 2
            """, "with a as (select 1 as x), b as (select x from a where x>0) select * from b union all select 2");
    }

    [Fact]
    public void With_Recursive_And_ColumnList()
    {
        Same("WITH RECURSIVE r (n) AS (\n    SELECT 1\n    UNION ALL\n    SELECT n + 1\n    FROM r\n    WHERE n < 5\n)\nSELECT n\nFROM r", "with recursive r(n) as (select 1 union all select n+1 from r where n<5) select n from r".Replace("r(n)", "r (n)"));
    }

    [Fact]
    public void Subqueries_AreIndentedInParentheses()
    {
        Same("""
            SELECT *
            FROM (
                SELECT a
                FROM t
            ) x
            JOIN (
                SELECT b
                FROM u
            ) y
                ON x.a = y.b
            """, "select * from (select a from t) x join (select b from u) y on x.a = y.b");
        Same("DELETE FROM t\nWHERE id IN (\n    SELECT id\n    FROM u\n    WHERE z = 1\n)", "delete from t where id in (select id from u where z = 1)");
        Same("SELECT *\nFROM t\nWHERE NOT EXISTS (\n    SELECT 1\n    FROM u\n)", "select * from t where not exists (select 1 from u)");
    }

    [Fact]
    public void ScalarSubquery_InAFunction_StaysReadable()
    {
        Same("""
            SELECT
                a.x,
                coalesce(
                    a.x,
                    (
                        SELECT max(y)
                        FROM z
                        WHERE z.id = a.id
                    ),
                    0
                ) c
            FROM a
            """, "select a.x, coalesce(a.x, (select max(y) from z where z.id = a.id), 0) c from a");
    }

    [Fact]
    public void NestedSubqueries_IndentOneLevelEach()
    {
        Same("SELECT *\nFROM (\n    SELECT *\n    FROM (\n        SELECT 1\n    ) a\n) b", "select * from (select * from (select 1) a) b");
    }

    // ------------------------------------------------------------------ CASE

    [Fact]
    public void Case_BranchesOnTheirOwnLines()
    {
        Same("""
            SELECT
                CASE x
                    WHEN 1 THEN 'a'
                    WHEN 2 THEN 'b'
                    ELSE 'c'
                END,
                CASE
                    WHEN a THEN CASE
                        WHEN b THEN 1
                        ELSE 2
                    END
                END AS z
            FROM t
            """, "select case x when 1 then 'a' when 2 then 'b' else 'c' end, case when a then case when b then 1 else 2 end end as z from t");
    }

    [Fact]
    public void Case_InsideAShortFunctionCall_StaysInline()
    {
        Same("SELECT\n    a,\n    sum(CASE WHEN o.status = 'x' THEN 1 ELSE 0 END) AS total\nFROM t", "select a, sum(case when o.status='x' then 1 else 0 end) as total from t");
    }

    [Fact]
    public void Case_InWhereAndOrderBy()
    {
        Same("SELECT a\nFROM t\nWHERE CASE\n    WHEN a > 1 AND b < 2 THEN 1\n    ELSE 0\nEND = 1\n    AND c = 2", "select a from t where case when a>1 and b<2 then 1 else 0 end = 1 and c = 2");
    }

    // ------------------------------------------------------------------ DML and DDL

    [Fact]
    public void Insert_Values_SingleAndMultiRow()
    {
        Same("INSERT INTO t (a, b)\nVALUES (1, 'x')", "insert into t (a,b) values (1,'x')");
        Same("INSERT INTO t (a, b)\nVALUES\n    (1, 'x'),\n    (2, 'y')", "insert into t (a,b) values (1,'x'), (2,'y')");
    }

    [Fact]
    public void Insert_Select_And_OnConflict_Returning()
    {
        Same("""
            INSERT INTO t (a, b)
            SELECT
                a,
                b
            FROM s
            ON CONFLICT (a) DO UPDATE
            SET b = excluded.b
            RETURNING *
            """, "insert into t (a, b) select a, b from s on conflict (a) do update set b = excluded.b returning *");
    }

    [Fact]
    public void Update_Set_OnePerLine()
    {
        Same("UPDATE t\nSET\n    a = 1,\n    b = b + 1\nWHERE id = 5", "update t set a=1, b=b+1 where id=5");
        Same("UPDATE t\nSET a = 1\nWHERE id = 5", "update t set a=1 where id=5");
    }

    [Fact]
    public void Update_SetWithCaseAndSubquery()
    {
        Same("""
            UPDATE t
            SET
                a = (
                    SELECT max(b)
                    FROM u
                ),
                c = CASE
                    WHEN d THEN 1
                END
            WHERE e = 1
            """, "update t set a = (select max(b) from u), c = case when d then 1 end where e = 1");
    }

    [Fact]
    public void Update_TSqlFromJoin()
    {
        Same("UPDATE t\nSET a = u.a\nFROM t\nJOIN u\n    ON u.id = t.id\nWHERE u.x = 1", "update t set a = u.a from t join u on u.id = t.id where u.x = 1", DbKind.SqlServer);
    }

    [Fact]
    public void Delete_Forms()
    {
        Same("DELETE FROM t\nWHERE id = 1", "delete from t where id=1");
        Same("DELETE a\nFROM a\nJOIN b\n    ON a.id = b.id", "delete a from a join b on a.id=b.id", DbKind.SqlServer);
        Same("DELETE FROM a\nUSING b\nWHERE a.id = b.id", "delete from a using b where a.id = b.id");
    }

    [Fact]
    public void Merge_Layout()
    {
        Same("""
            MERGE INTO tgt t
            USING src s
                ON t.id = s.id
            WHEN MATCHED AND s.x > 1 THEN
                UPDATE
                SET
                    t.a = s.a,
                    t.b = s.b
            WHEN NOT MATCHED BY TARGET THEN
                INSERT (id, a)
                VALUES (s.id, s.a)
            WHEN MATCHED THEN
                DELETE;
            """, "merge into tgt t using src s on t.id = s.id when matched and s.x > 1 then update set t.a = s.a, t.b = s.b when not matched by target then insert (id, a) values (s.id, s.a) when matched then delete;", DbKind.SqlServer);
    }

    [Fact]
    public void CreateTable_ColumnsOnePerLine_TypesUppercased()
    {
        Same("""
            CREATE TABLE t (
                id INTEGER PRIMARY KEY,
                name VARCHAR(20) NOT NULL DEFAULT 'x',
                created TIMESTAMP WITH TIME ZONE,
                price DOUBLE PRECISION,
                FOREIGN KEY (id) REFERENCES u(id)
            )
            """, "create table t (id integer primary key, name varchar(20) not null default 'x', created timestamp with time zone, price double precision, foreign key (id) references u(id))");
    }

    [Fact]
    public void CreateTable_AsSelect_And_View_And_Index()
    {
        Same("CREATE TABLE x AS\nSELECT a\nFROM t", "create table x as select a from t");
        Same("CREATE OR REPLACE VIEW v AS\nSELECT\n    a,\n    b\nFROM t\nWHERE x = 1", "create or replace view v as select a, b from t where x = 1");
        Same("CREATE INDEX ix ON t (a, b DESC)\nWHERE c > 1", "create index ix on t (a, b desc) where c > 1");
    }

    [Fact]
    public void ColumnNamedLikeAType_IsNotTouched()
    {
        Same("SELECT date, text, name, value\nFROM t".Replace("SELECT date, text, name, value", "SELECT\n    date,\n    text,\n    name,\n    value"), "select date, text, name, value from t");
    }

    [Fact]
    public void Explain_PrefixIsKept()
    {
        Same("EXPLAIN ANALYZE\nSELECT *\nFROM t", "explain analyze select * from t");
    }

    [Fact]
    public void SelectInto_And_ForUpdate()
    {
        Same("SELECT a INTO tmp\nFROM t", "select a into tmp from t");
        Same("SELECT a\nFROM t FOR UPDATE", "select a from t for update");
    }

    // ------------------------------------------------------------------ comments, literals, identifiers

    [Fact]
    public void Comments_KeepTheirPlace()
    {
        Same("""
            SELECT
                -- first
                a, /* two */
                b -- tail
            FROM t -- table
            WHERE a = 'it''s from where' -- c
            """, "select -- first\n a, /* two */ b -- tail\nfrom t -- table\nwhere a = 'it''s from where' -- c");
    }

    [Fact]
    public void Comment_BetweenAndConditions_StaysTrailing()
    {
        Same("SELECT a\nFROM t\nWHERE a = 1 -- note\n    AND b = 2", "select a from t where a = 1 -- note\n and b = 2");
        Same("SELECT a\nFROM t\nWHERE a = 1 /* inline */\n    AND b = 2", "select a from t where a=1 /* inline */ and b = 2");
    }

    [Fact]
    public void Comment_OnItsOwnLineAfterAKeyword_GoesBelowIt()
    {
        Same("SELECT\n    a,\n    b\nFROM t\nWHERE\n    -- why\n    c = 1", "select a,\n b\nfrom t\nwhere\n-- why\n c = 1");
    }

    [Fact]
    public void Comments_BetweenStatements_Stay()
    {
        Same("SELECT 1;\n\n-- trailing comment line\nSELECT 2; -- after\n\n-- end", "select 1;\n-- trailing comment line\nselect 2; -- after\n\n-- end");
    }

    [Fact]
    public void LineComment_BeforeSemicolon_PushesItToTheNextLine()
    {
        Same("SELECT 1 -- c\n;", "select 1 -- c\n;");
    }

    [Fact]
    public void Strings_WithKeywordsInside_AreUntouched()
    {
        Same("SELECT 'select  from   where -- not a comment'\nFROM t", "select 'select  from   where -- not a comment' from t");
        Same("SELECT *\nFROM t\nWHERE a = 'x /* y */ z'", "select * from t where a = 'x /* y */ z'");
        Same("SELECT 'a\n  b'", "select 'a\n  b'");
    }

    [Fact]
    public void QuotedIdentifiers_AreUntouched()
    {
        Same("SELECT\n    \"Order By\",\n    `from`,\n    [select]\nFROM t", "select \"Order By\", `from`, [select] from t", DbKind.Sqlite);
        Same("SELECT\n    \"x\"\"y\",\n    [a]]b]\nFROM t", "select \"x\"\"y\", [a]]b] from t", DbKind.SqlServer);
    }

    [Fact]
    public void Identifiers_KeepTheirCase_ExceptKeywords()
    {
        Same("SELECT MyCol\nFROM MyTable AS Alias", "select MyCol from MyTable as Alias");
    }

    [Fact]
    public void PostgresDollarQuotedBodies_AreVerbatim()
    {
        var sql = "create function f() returns int as $$ begin  return 1;   end $$ language plpgsql;";
        var actual = F(sql);
        Assert.Contains("$$ begin  return 1;   end $$", actual);
        Same("SELECT $tag$ a  b $tag$\nFROM t", "select $tag$ a  b $tag$ from t");
    }

    [Fact]
    public void Postgres_CastsArraysOperatorsAndParameters()
    {
        Same("""
            SELECT
                x::INT,
                arr[1],
                'a' || "Col",
                data ->> 'k',
                $1,
                :p,
                '{}'::INT[],
                a <@ b,
                e'a\n'
            FROM t
            """, "select x::int, arr[1], 'a' || \"Col\" , data->>'k', $1, :p, '{}'::int[], a<@b, e'a\\n' from t");
    }

    [Fact]
    public void Numbers_AndUnaryMinus()
    {
        Same("SELECT\n    -a,\n    a - b,\n    a - -b,\n    1e-5,\n    .5,\n    a = -1,\n    1.5,\n    0x1F\nFROM t", "select -a, a-b, a - -b, 1e-5, .5, a=-1, 1.5, 0x1F from t");
    }

    [Fact]
    public void SqlServer_Variables_BracketsAndNPrefix()
    {
        Same("SELECT TOP 10\n    [a],\n    b\nFROM [dbo].[t] WITH (nolock)\nWHERE x = N'it''s'\n    AND y > -1\n    AND z = @p", "select top 10 [a], b from [dbo].[t] with (nolock) where x = N'it''s' and y > -1 and z=@p", DbKind.SqlServer);
        Same("SELECT @@ROWCOUNT, #tmp.a\nFROM #tmp".Replace("SELECT @@ROWCOUNT, #tmp.a", "SELECT\n    @@ROWCOUNT,\n    #tmp.a"), "select @@ROWCOUNT, #tmp.a from #tmp", DbKind.SqlServer);
    }

    [Fact]
    public void Sqlite_BracketAndBacktickIdentifiers()
    {
        Same("SELECT\n    [a b],\n    `c d`\nFROM \"t\"\nWHERE x GLOB 'a*'", "select [a b], `c d` from \"t\" where x glob 'a*'", DbKind.Sqlite);
    }

    [Fact]
    public void FunctionNames_AreNotChanged()
    {
        Same("SELECT\n    COUNT(*),\n    count(*),\n    Sum(x)\nFROM t", "select COUNT(*), count(*), Sum(x) from t");
    }

    [Fact]
    public void Window_OverClause()
    {
        Same("SELECT\n    a,\n    row_number() OVER (PARTITION BY a.g ORDER BY a.d DESC) AS rn\nFROM a", "select a, row_number() over (partition by a.g order by a.d desc) as rn from a");
    }

    // ------------------------------------------------------------------ scripts

    [Fact]
    public void MultipleStatements_AreSeparated()
    {
        Same("INSERT INTO t (a, b)\nVALUES (1, 'x');\n\nUPDATE t\nSET a = 1\nWHERE id = 5;\n\nSELECT 1", "insert into t (a,b) values (1,'x'); update t set a=1 where id=5; select 1");
        Same("SELECT 1;\nSELECT 2;", "select 1; select 2;");
        Same("SELECT 1;\n\nSELECT 2;", "select 1;\n\n\n\nselect 2;");
    }

    [Fact]
    public void Go_LinesAreKept_OnSqlServer()
    {
        Same("SELECT 1\nGO\nSELECT 2\nGO 2", "select 1\ngo\nselect 2\ngo 2", DbKind.SqlServer);
        Same("SELECT 1\n\nGO\n\nSELECT 2", "select 1\n\ngo\n\nselect 2", DbKind.SqlServer);
    }

    [Fact]
    public void Go_IsNotASeparatorOutsideSqlServer()
    {
        var actual = F("select go from t", DbKind.Postgres);
        Assert.Equal("SELECT go\nFROM t", actual);
    }

    [Fact]
    public void UnknownStatements_AreOnlyLightlyNormalised()
    {
        Same("DECLARE @x int = 5\nSET nocount ON\nSELECT @x", "declare   @x   int =  5\nset nocount on\nselect    @x", DbKind.SqlServer);
        Same("DROP TABLE x;", "drop   table x ;");
        Same("ALTER TABLE t ADD COLUMN c int\n    DEFAULT 1", "alter table t add   column c int\n    default 1");
    }

    [Fact]
    public void Light_KeepsLineBreaksAndIndentation()
    {
        Assert.Equal("BEGIN\n\tUPDATE\n\t\tx", F("begin\n\tupdate\n\t\tx".Replace("update", "foo"), DbKind.SqlServer).Replace("foo", "UPDATE"));
    }

    [Fact]
    public void Unbalanced_Unterminated_OrUnknown_ReturnsTheStatementLightly()
    {
        Assert.Equal("SELECT 'unterminated", F("select 'unterminated"));
        Assert.Equal("SELECT a FROM t WHERE (a = 1", F("select a   from t where (a = 1"));
        Assert.Equal("SELECT 1 /* open", F("select 1 /* open"));
        // An unclassifiable character: the statement is untouched (even its keyword case), the next one is formatted.
        Assert.Equal("select  §  from t;\nSELECT 2", F("select  §  from t; select 2"));
    }

    [Fact]
    public void EmptyAndWhitespace_AreReturnedAsIs()
    {
        Assert.Equal("", F(""));
        Assert.Equal("  \n ", F("  \n "));
    }

    [Fact]
    public void LineEndings_AreKept()
    {
        var crlf = F("select a, b from t\r\nwhere x = 1\r\n");
        Assert.Equal("SELECT\r\n    a,\r\n    b\r\nFROM t\r\nWHERE x = 1\r\n", crlf);
        Assert.Equal("SELECT\n    a,\n    b\nFROM t\nWHERE x = 1\n", F("select a, b from t\nwhere x = 1\n"));
    }

    // ------------------------------------------------------------------ options

    [Fact]
    public void KeywordCase_LowerAndKeep()
    {
        Same("select\n    a,\n    b\nfrom t\nwhere x is not null", "SELECT a, b FROM t WHERE x IS NOT NULL", o: new SqlFormatOptions { KeywordCase = KeywordCase.Lower });
        Same("Select\n    a,\n    b\nfrom t\nWHERE x Is Not null", "Select a, b from t WHERE x Is Not null", o: new SqlFormatOptions { KeywordCase = KeywordCase.Keep });
    }

    [Fact]
    public void Indent_TwoSpacesAndTabs()
    {
        Same("SELECT\n  a,\n  b\nFROM t\nWHERE x = 1\n  AND y = 2", "select a, b from t where x=1 and y=2", o: new SqlFormatOptions { IndentSize = 2 });
        Same("SELECT\n\ta,\n\tb\nFROM t\nWHERE x = 1\n\tAND y = 2", "select a, b from t where x=1 and y=2", o: new SqlFormatOptions { UseTabs = true });
    }

    [Fact]
    public void LeadingCommas()
    {
        var o = new SqlFormatOptions { CommaPosition = CommaPosition.Leading };
        Same("SELECT\n    a\n    , b\n    , c\nFROM t", "select a, b, c from t", o: o);
        Same("WITH a AS (\n    SELECT 1\n)\n, b AS (\n    SELECT 2\n)\nSELECT *\nFROM b", "with a as (select 1), b as (select 2) select * from b", o: o);
        Same("INSERT INTO t (a)\nVALUES\n    (1)\n    , (2)", "insert into t (a) values (1), (2)", o: o);
    }

    [Fact]
    public void LeadingCommas_WithComments_AreStable()
    {
        var o = new SqlFormatOptions { CommaPosition = CommaPosition.Leading };
        var once = F("select a, -- one\n b /* x */, c from t", o: o);
        Assert.Equal(once, F(once, o: o));
    }

    [Fact]
    public void MaxLineWidth_ForcesWrapOfALongFunctionCall()
    {
        var o = new SqlFormatOptions { MaxLineWidth = 40 };
        var actual = F("select coalesce(aaaaaaaaaaaa, bbbbbbbbbbbb, cccccccccccc, dddddddddddd) from t", o: o);
        Assert.Contains("coalesce(\n", actual);
    }

    [Fact]
    public void Options_AreClamped()
    {
        var n = new SqlFormatOptions { IndentSize = 0, MaxLineWidth = 1, KeywordCase = (KeywordCase)99 }.Normalized();
        Assert.Equal(1, n.IndentSize);
        Assert.Equal(40, n.MaxLineWidth);
        Assert.Equal(KeywordCase.Upper, n.KeywordCase);
        Assert.Equal("SELECT 1", F("select 1", o: new SqlFormatOptions { IndentSize = 0, KeywordCase = (KeywordCase)99 }));
    }

    // ------------------------------------------------------------------ properties over a corpus

    private static readonly string[] Handwritten =
    [
        "select 1",
        "SELECT a.b, c FROM \"my table\" a WHERE a.x = 'a''b' AND y IN (1,2,3) -- c\nORDER BY 1",
        "select * from t where a = 1 and (b = 2 or c = 3) and d in (select e from f where g = h)",
        "with x as (select 1), y as (select 2) select * from x, y",
        "insert into t values (1), (2), (3)",
        "update t set a = a + 1, b = null where c like 'x%' escape '\\'",
        "delete from t",
        "create table t (a int not null, b varchar(10) default 'x', primary key (a), constraint fk foreign key (b) references u (id) on delete cascade)",
        "select cast(a as int), a::numeric(10,2), extract(year from d), substring(s from 1 for 2), trim(both ' ' from s) from t",
        "select a from t where b is distinct from c and d is not distinct from e",
        "select * from a join b using (id) left join c on c.x = b.x where a.y > -1 and a.z <> 2 and a.w != 3",
        "select a & b, a | b, a ^ b, ~a, a << 1, a >> 1, a % 2, a / 2, a * 2 from t",
        "select 1 -- one\n; select 2 /* two */ ;",
        "select a from t where a = :p and b = @v and c = $1 and d = ?",
        "select json_col->'a'->>'b', json_col#>'{a,b}', json_col @> '{}', a <-> b from t",
        "select array[1,2,3], arr[1], arr[1:2] from t",
        "select * from t where a ~ 'x' and b !~ 'y' and c ~* 'z' and d !~* 'w'",
        "select 'unterminated",
        "select a from t where (b = 1",
        "select 1 /* never closed",
        "SELECT\n\ta\nFROM\n\tt\nWHERE\n\t1 = 1",
        "create or replace function f() returns trigger as $body$ begin new.x := 1; return new; end $body$ language plpgsql",
        "do $$ begin perform 1; end $$",
        "begin; update t set a = 1; commit;",
        "select top 5 * from t order by a",
        "exec sp_who2; go\nselect 1\ngo\n",
        "declare @t table (a int, b varchar(10)); insert into @t select 1, 'x'; select * from @t",
        "select a from t where a between 1 and 2 and b between 3 and 4",
        "select * from t t1 cross join lateral (select * from u where u.a = t1.a limit 1) x",
        "select a, b from t group by rollup (a, b) having sum(c) > 1 order by a nulls last",
        "select count(*) filter (where a > 1) over (partition by b order by c rows between unbounded preceding and current row) from t",
        "merge into a using b on a.id = b.id when matched then update set x = b.x when not matched then insert (id) values (b.id)",
        "values (1, 'a'), (2, 'b')",
        "select 1;;;select 2",
        ";",
        "-- only a comment",
        "/* only a block */ select 1",
        "select\n\n\n a\n\n\n from\n\n t",
        "select a /* c1 */, /* c2 */ b from /* c3 */ t /* c4 */ where /* c5 */ x = 1 /* c6 */",
        "select a,-- c\n b from t",
        "select e'a\\'b', E'x', b'101', x'ff', n'x', U&'d\\0061t\\+000061' from t",
        "select \"a\"\"b\", `c``d`, [e]]f] from t",
        "select a from t where x = 1 order by case when a then 1 else 2 end desc, b",
        "alter table t add column c int not null default 0, drop column d",
        "explain (analyze, buffers) select * from t",
        "pragma table_info(t)",
        "select * from t where a = 1 limit 5 offset 2",
        "select (a), ((b)), (((c))) from t where ((a = 1)) and (((b = 2) or c = 3))",
        "select 1 where 1=1 union select 2 union all select 3 intersect select 4 except select 5",
    ];

    private static IEnumerable<string> RepoSql()
    {
        var root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "CLAUDE.md"))) root = Path.GetDirectoryName(root);
        if (root == null) yield break;

        var start = new Regex(@"^\s*(select|insert|update|delete|with|create|drop|alter|merge|values|explain|pragma)\b", RegexOptions.IgnoreCase);
        var literal = new Regex("\"((?:[^\"\\\\\\r\\n]|\\\\.)*)\"");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in literal.Matches(text))
            {
                var s = m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");
                if (s.Length > 6 && start.IsMatch(s)) yield return s;
            }
        }
        var block = new Regex("```sql\\r?\\n(.*?)```", RegexOptions.Singleline);
        var docsDir = Path.Combine(root, "docs");
        var mdFiles = Directory.Exists(docsDir) ? Directory.EnumerateFiles(docsDir, "*.md", SearchOption.AllDirectories) : [];
        foreach (var file in mdFiles.Append(Path.Combine(root, "README.md")).Where(File.Exists))
        {
            foreach (Match m in block.Matches(File.ReadAllText(file))) yield return m.Groups[1].Value.Trim();
        }
    }

    public static IEnumerable<object[]> Corpus() => Handwritten.Concat(RepoSql()).Distinct().Select(s => new object[] { s });

    private static readonly SqlFormatOptions[] Variants =
    [
        SqlFormatOptions.Default,
        new() { KeywordCase = KeywordCase.Lower, CommaPosition = CommaPosition.Leading, IndentSize = 2 },
        new() { KeywordCase = KeywordCase.Keep, UseTabs = true, MaxLineWidth = 50 },
    ];

    private static List<(FKind Kind, string Raw)> Tokens(string sql, DbKind kind) =>
        FTokenizer.Tokenize(sql, SqlLexOptions.For(kind)).Select(t => (t.Kind, t.Kind == FKind.Word ? t.Raw.ToLowerInvariant() : t.Raw)).ToList();

    [Fact]
    public void Corpus_IsNotEmpty_AndFindsRepoSql()
    {
        Assert.True(Corpus().Count() > Handwritten.Length + 20, "the repo SQL scan found nothing: is CLAUDE.md reachable from the test folder?");
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Corpus_TokensAreUnchanged_AndFormattingIsIdempotent(string sql)
    {
        foreach (var kind in new[] { DbKind.Postgres, DbKind.SqlServer, DbKind.Sqlite })
        {
            foreach (var o in Variants)
            {
                var once = F(sql, kind, o);
                Assert.Equal(Tokens(sql, kind), Tokens(once, kind));
                Assert.Equal(once, F(once, kind, o));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Corpus_Formatting_NeverAddsTrailingBlanksOrTabsInTheMiddleOfLines(string sql)
    {
        var once = F(sql, DbKind.Postgres);
        foreach (var line in once.Split('\n'))
        {
            // Trailing blanks only survive inside verbatim tokens (strings, block comments).
            if (line.Length > 0 && line[^1] is ' ' or '\t') Assert.True(sql.Contains(line), line);
        }
    }

    [Fact]
    public void Formatting_FromEveryPrefix_NeverThrows()
    {
        var sql = "with a as (select x, case when y then 1 end from t where z in (1,2) -- c\n) select 'q', \"w\", [e] from a join b on a.i = b.i group by 1 order by 2 limit 3; go\nselect $$x$$";
        for (var i = 0; i <= sql.Length; i++)
        {
            foreach (var kind in new[] { DbKind.Postgres, DbKind.SqlServer, DbKind.Sqlite })
            {
                var part = sql[..i];
                var once = F(part, kind);
                Assert.Equal(Tokens(part, kind), Tokens(once, kind));
            }
        }
    }
}
