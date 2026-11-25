using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Tests;

public class SqlCompletionTests
{
    private static ColumnInfo Col(string name, string type, int ordinal, bool pk = false) =>
        new(name, type, typeof(object), true, pk, ordinal);

    private static TableInfo Table(string? schema, string name, bool view = false, params ColumnInfo[] columns) =>
        new(new TableRef(schema, name), view, columns, [], []);

    private static readonly TableInfo Orders = new(
        new TableRef("public", "orders"), false,
        [Col("id", "integer", 0, pk: true), Col("customer_id", "integer", 1), Col("total", "numeric", 2), Col("Status", "text", 3)],
        [new ForeignKeyInfo("fk", ["customer_id"], new TableRef("public", "customers"), ["id"])], []);

    private static readonly ICompletionSchema Pg = new TableListSchema(
    [
        Orders,
        Table("public", "customers", false, Col("id", "integer", 0, true), Col("name", "text", 1)),
        Table("public", "order_view", true, Col("order_id", "integer", 0)),
        Table("sales", "invoices", false, Col("id", "integer", 0, true), Col("amount", "numeric", 1)),
        Table("public", "Mixed Case", false, Col("a b", "text", 0)),
        Table("public", "user", false, Col("user", "text", 0)),
    ]);

    private static readonly ICompletionSchema Ss = new TableListSchema(
    [
        Table("dbo", "Orders", false, Col("Id", "int", 0, true), Col("Total", "decimal", 1)),
        Table("dbo", "Order Details", false, Col("Qty", "int", 0)),
        Table("hr", "Staff", false, Col("Name", "nvarchar", 0)),
    ]);

    private static readonly ICompletionSchema Lite = new TableListSchema(
    [
        Table(null, "orders", false, Col("id", "INTEGER", 0, true), Col("total", "REAL", 1)),
        Table(null, "Order Details", false, Col("qty", "INTEGER", 0)),
    ]);

    /// <summary>'|' marks the caret.</summary>
    private static IReadOnlyList<CompletionItem> Run(string sqlWithCaret, DbKind kind = DbKind.Postgres, ICompletionSchema? schema = null, bool noSchema = false)
    {
        var caret = sqlWithCaret.IndexOf('|');
        var sql = sqlWithCaret.Remove(caret, 1);
        schema ??= noSchema ? null : kind switch { DbKind.SqlServer => Ss, DbKind.Sqlite => Lite, _ => Pg };
        return SqlCompletion.Complete(sql, caret, schema, kind);
    }

    private static string[] Labels(IReadOnlyList<CompletionItem> items, CompletionKind? kind = null) =>
        items.Where(i => kind is null || i.Kind == kind).Select(i => i.Label).ToArray();

    [Fact]
    public void AfterFrom_OffersTablesViewsAndSchemas_ObjectsBeforeKeywords()
    {
        var items = Run("SELECT * FROM |");

        Assert.Contains(items, i => i.Kind == CompletionKind.Table && i.Label == "orders" && i.Text == "orders" && i.Detail == "public");
        Assert.Contains(items, i => i.Kind == CompletionKind.View && i.Label == "order_view");
        Assert.Contains(items, i => i.Kind == CompletionKind.Schema && i.Label == "sales");
        var lastObject = items.ToList().FindLastIndex(i => i.Kind is CompletionKind.Table or CompletionKind.View or CompletionKind.Schema);
        var firstKeyword = items.ToList().FindIndex(i => i.Kind == CompletionKind.Keyword);
        Assert.True(lastObject < firstKeyword);
    }

    [Theory]
    [InlineData("SELECT * FROM x JOIN |")]
    [InlineData("UPDATE |")]
    [InlineData("INSERT INTO |")]
    [InlineData("DELETE FROM |")]
    [InlineData("DROP TABLE |")]
    [InlineData("SELECT * FROM a, |")]
    public void ObjectContexts_OfferTables(string sql) =>
        Assert.Contains(Run(sql), i => i.Kind == CompletionKind.Table && i.Label == "orders");

    [Fact]
    public void TableOutsideTheDefaultSchema_IsInsertedQualified()
    {
        var items = Run("SELECT * FROM inv|");

        var invoices = Assert.Single(items, i => i.Label == "invoices");
        Assert.Equal("sales.invoices", invoices.Text);
        Assert.Equal("sales", invoices.Detail);
    }

    [Fact]
    public void AfterSchemaDot_OffersTablesOfThatSchemaOnly()
    {
        var items = Run("SELECT * FROM sales.|");

        Assert.Equal(["invoices"], Labels(items));
        Assert.Equal("invoices", items[0].Text);
    }

    [Fact]
    public void AfterAliasDot_OffersColumnsOfTheAliasedTable_DeclaredAfterTheCaret()
    {
        var items = Run("SELECT o.| FROM orders o");

        Assert.Equal(["id", "customer_id", "total", "Status"], Labels(items));
        Assert.All(items, i => Assert.Equal(CompletionKind.Column, i.Kind));
        Assert.Equal("integer · PK", items[0].Detail);
        Assert.Equal("integer · FK", items[1].Detail);
    }

    [Fact]
    public void AliasWithAs_AndJoinAliases_AreResolved()
    {
        var items = Run("SELECT c.n| FROM orders AS o JOIN customers c ON o.customer_id = c.id");

        Assert.Equal("name", items[0].Label);
        Assert.Equal(["id", "name"], Labels(Run("SELECT * FROM orders o JOIN customers c ON c.|")));
    }

    [Fact]
    public void TableNameDot_OffersItsColumns()
    {
        Assert.Equal(["id", "name"], Labels(Run("SELECT customers.| FROM customers")));
        Assert.Equal(["id", "amount"], Labels(Run("SELECT sales.invoices.| FROM sales.invoices")));
    }

    [Fact]
    public void UnknownQualifier_OffersNothing() => Assert.Empty(Run("SELECT zz.| FROM orders o"));

    [Fact]
    public void ColumnContext_OffersColumnsOfTheStatementTables_QualifiedWhenNamesClash()
    {
        var items = Run("SELECT | FROM orders o JOIN customers c ON 1 = 1");

        var columns = Labels(items, CompletionKind.Column);
        Assert.Contains("total", columns);
        Assert.Contains("name", columns);
        Assert.Contains("o.id", columns);
        Assert.Contains("c.id", columns);
        Assert.DoesNotContain("id", columns);
        Assert.Equal("o.id", items.First(i => i.Label == "o.id").Text);
        Assert.Equal("id", items.First(i => i.Label == "o.id").Filter);
        Assert.Contains(items, i => i.Kind == CompletionKind.Alias && i.Label == "o" && i.Detail == "public.orders");
        Assert.Contains(items, i => i.Kind == CompletionKind.Function && i.Label == "COUNT");
        Assert.Contains(items, i => i.Kind == CompletionKind.Keyword && i.Label == "DISTINCT");
    }

    [Theory]
    [InlineData("SELECT * FROM orders WHERE |")]
    [InlineData("SELECT * FROM orders o JOIN customers c ON |")]
    [InlineData("SELECT * FROM orders GROUP BY |")]
    [InlineData("SELECT * FROM orders ORDER BY |")]
    [InlineData("SELECT count(*) FROM orders GROUP BY id HAVING |")]
    [InlineData("UPDATE orders SET |")]
    [InlineData("DELETE FROM orders WHERE |")]
    [InlineData("INSERT INTO orders (|")]
    [InlineData("SELECT * FROM orders WHERE id IN (SELECT |")]
    [InlineData("SELECT * FROM orders WHERE id IN (SELECT 1) AND |")]
    public void ColumnContexts_OfferTheColumns(string sql) =>
        Assert.Contains(Run(sql), i => i.Kind == CompletionKind.Column && i.Label == "total");

    [Fact]
    public void StartOfStatement_OffersStatementKeywordsOnly()
    {
        var items = Run("|");

        Assert.All(items, i => Assert.Equal(CompletionKind.Keyword, i.Kind));
        Assert.Contains(items, i => i.Label == "SELECT");
        Assert.Contains(items, i => i.Label == "WITH");
        Assert.DoesNotContain(items, i => i.Label == "WHERE");
        Assert.Equal("SELECT", Run("sel|")[0].Label);
    }

    [Fact]
    public void AfterACompleteTable_OffersKeywordsNotTables()
    {
        var items = Run("SELECT * FROM orders |");

        Assert.DoesNotContain(items, i => i.Kind == CompletionKind.Table);
        Assert.Contains(items, i => i.Label == "WHERE");
    }

    [Fact]
    public void OnlyTheStatementUnderTheCaretIsAnalysed()
    {
        var semicolon = Run("SELECT * FROM customers;\nSELECT | FROM orders");
        var blank = Run("SELECT * FROM customers\n\nSELECT | FROM orders");
        var go = Run("SELECT * FROM customers\nGO\nSELECT | FROM orders", DbKind.SqlServer, Ss);

        foreach (var items in new[] { semicolon, blank })
        {
            Assert.Contains("total", Labels(items, CompletionKind.Column));
            Assert.DoesNotContain("name", Labels(items, CompletionKind.Column));
            Assert.DoesNotContain("amount", Labels(items, CompletionKind.Column));
        }
        Assert.Contains("Total", Labels(go, CompletionKind.Column));
        Assert.DoesNotContain("Name", Labels(go, CompletionKind.Column));
    }

    [Fact]
    public void AfterASemicolon_ANewStatementStarts() =>
        Assert.All(Run("SELECT 1; |"), i => Assert.Equal(CompletionKind.Keyword, i.Kind));

    [Fact]
    public void ANewLineOfTheSameStatement_KeepsTheContext() =>
        Assert.Contains(Run("SELECT *\nFROM |"), i => i.Kind == CompletionKind.Table);

    [Theory]
    [InlineData("SELECT * FROM orders WHERE name = 'ab|'")]
    [InlineData("SELECT * FROM orders WHERE name = 'ab|")]
    [InlineData("SELECT * FROM orders -- note |")]
    [InlineData("SELECT * FROM orders /* note |")]
    [InlineData("SELECT $$ some o| text $$")]
    public void InsideAStringOrAComment_ReturnsNothing(string sql) => Assert.Empty(Run(sql));

    [Fact]
    public void AfterAClosedStringOrComment_CompletesAgain()
    {
        Assert.NotEmpty(Run("SELECT * FROM orders WHERE name = 'x' AND |"));
        Assert.NotEmpty(Run("SELECT * FROM orders /* c */ WH|"));
        Assert.NotEmpty(Run("SELECT * -- c\nFROM |"));
    }

    [Fact]
    public void WithoutSchema_OffersKeywordsAndFunctionsOnly()
    {
        var items = Run("SELECT | FROM orders", noSchema: true);
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.True(i.Kind is CompletionKind.Keyword or CompletionKind.Function));

        var from = Run("SELECT * FROM |", noSchema: true);
        Assert.NotEmpty(from);
        Assert.All(from, i => Assert.Equal(CompletionKind.Keyword, i.Kind));
    }

    [Fact]
    public void QuotingPerDialect_OnlyWhenNeeded()
    {
        // PostgreSQL: lower-case plain names stay bare; upper case, spaces and reserved words are quoted.
        Assert.Equal("\"Mixed Case\"", Run("SELECT * FROM Mix|").Single(i => i.Kind == CompletionKind.Table).Text);
        Assert.Equal("\"user\"", Run("SELECT * FROM us|").First(i => i.Kind == CompletionKind.Table).Text);
        Assert.Equal("orders", Run("SELECT * FROM ord|").First(i => i.Kind == CompletionKind.Table).Text);
        Assert.Equal("\"Status\"", Run("SELECT o.St| FROM orders o").Single(i => i.Label == "Status").Text);
        Assert.Equal("\"a b\"", Run("SELECT m.| FROM \"Mixed Case\" m").Single().Text);

        // SQL Server: brackets; upper case is fine; the default schema (dbo) is left out, others are kept.
        var ss = Run("SELECT * FROM Or|", DbKind.SqlServer);
        Assert.Equal("Orders", ss.First(i => i.Label == "Orders").Text);
        Assert.Equal("[Order Details]", ss.First(i => i.Label == "Order Details").Text);
        Assert.Equal("hr.Staff", Run("SELECT * FROM Sta|", DbKind.SqlServer).First(i => i.Kind == CompletionKind.Table).Text);

        // SQLite: double quotes.
        var lite = Run("SELECT * FROM Or|", DbKind.Sqlite);
        Assert.Equal("orders", lite.First(i => i.Label == "orders").Text);
        Assert.Equal("\"Order Details\"", lite.First(i => i.Label == "Order Details").Text);
    }

    [Fact]
    public void AnOpenedQuote_IsKeptAndTheReplaceRangeStartsOnIt()
    {
        var sql = "SELECT * FROM \"Mix";
        var items = SqlCompletion.Complete(sql, sql.Length, Pg, DbKind.Postgres);

        var item = Assert.Single(items, i => i.Kind == CompletionKind.Table);
        Assert.Equal("\"Mixed Case\"", item.Text);
        Assert.Equal(sql.IndexOf('"'), item.ReplaceStart);
        Assert.Equal(4, item.ReplaceLength);

        var bracket = "SELECT * FROM [Order D";
        var ss = SqlCompletion.Complete(bracket, bracket.Length, Ss, DbKind.SqlServer);
        Assert.Equal("[Order Details]", ss.First(i => i.Kind == CompletionKind.Table).Text);
        Assert.Equal(bracket.IndexOf('['), ss[0].ReplaceStart);
    }

    [Fact]
    public void ReplaceRange_CoversTheTypedWord()
    {
        var items = Run("SELECT * FROM cu|");

        Assert.All(items, i => Assert.Equal((14, 2), (i.ReplaceStart, i.ReplaceLength)));
    }

    [Fact]
    public void Ranking_PrefixFirst_ThenFuzzy()
    {
        var items = Run("SELECT * FROM or|");
        var tables = items.Where(i => i.Kind is CompletionKind.Table or CompletionKind.View).Select(i => i.Label).ToArray();

        Assert.Equal(["order_view", "orders"], tables.Take(2).OrderBy(x => x).ToArray());   // both start with "or"
        Assert.True(Array.IndexOf(tables, "customers") > Array.IndexOf(tables, "orders"));     // "customers" only matches as a subsequence
        Assert.Equal("customers", Run("SELECT * FROM cstm|")[0].Label);
        Assert.DoesNotContain(Run("SELECT * FROM zzz|"), i => i.Kind == CompletionKind.Table);
    }

    [Fact]
    public void KeywordPrefix_ComesBeforeAFuzzyColumn()
    {
        var items = Run("SELECT * FROM orders WHERE or|");

        Assert.Equal(["OR", "ORDER BY"], items.Where(i => i.Kind == CompletionKind.Keyword).Take(2).Select(i => i.Label));
        Assert.True(items.ToList().FindIndex(i => i.Label == "OR") < items.ToList().FindIndex(i => i.Label == "customer_id"));
    }

    [Fact]
    public void ListIsCapped()
    {
        var many = new TableListSchema(Enumerable.Range(0, 500).Select(i => Table("public", "t" + i)).ToList());
        var sql = "SELECT * FROM ";

        Assert.Equal(SqlCompletion.MaxItems, SqlCompletion.Complete(sql, sql.Length, many, DbKind.Postgres).Count);
        Assert.Equal(5, SqlCompletion.Complete(sql, sql.Length, many, DbKind.Postgres, max: 5).Count);
    }

    [Fact]
    public void Functions_AreEngineSpecific()
    {
        Assert.Contains(Run("SELECT now|"), i => i.Label == "NOW");
        Assert.DoesNotContain(Run("SELECT getd|"), i => i.Label == "GETDATE");
        Assert.Contains(Run("SELECT getd|", DbKind.SqlServer), i => i.Label == "GETDATE" && i.Kind == CompletionKind.Function && i.Text == "GETDATE(");
        Assert.Contains(Run("SELECT strf|", DbKind.Sqlite), i => i.Label == "STRFTIME");
        Assert.Contains(Run("SELECT * FROM orders lim|"), i => i.Label == "LIMIT");
        Assert.Contains(Run("SELECT * FROM orders lim|", DbKind.Sqlite), i => i.Label == "LIMIT");
        Assert.DoesNotContain(Run("SELECT * FROM orders lim|", DbKind.SqlServer), i => i.Label == "LIMIT");
        Assert.Contains(Run("SELECT to|", DbKind.SqlServer), i => i.Label == "TOP");
    }

    [Fact]
    public void NumbersAndVariables_ReturnNothing()
    {
        Assert.Empty(Run("SELECT * FROM orders WHERE id = 12|"));
        Assert.Empty(Run("SELECT @na|", DbKind.SqlServer));
    }
}
