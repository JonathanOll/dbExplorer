using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;

namespace DbExplorer.Core.Tests;

public class EditApplierRowCountTests
{
    private static readonly IDbDriver Sqlite = new SqliteDriver();

    private static TableInfo Items() => EditTestData.Table(null, "items", false,
        EditTestData.Col("id", typeof(long), pk: true, nullable: false),
        EditTestData.Col("name", typeof(string), ordinal: 1));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Apply_ServerReportsMinusOne_RollsBackWithDedicatedError(bool update)
    {
        RowEdit edit = update
            ? new UpdateRow(EditTestData.Row(("id", 1L)), EditTestData.Row(("name", "x")))
            : new DeleteRow(EditTestData.Row(("id", 1L)));
        var plan = EditPlanner.Plan(Sqlite, Items(), ["id"], [edit]);
        var connection = new FakeDbConnection(affected: -1); // SqlClient with SET NOCOUNT ON

        var ex = await Assert.ThrowsAsync<RowCountUnknownException>(
            () => EditApplier.ApplyAsync(connection, plan, CancellationToken.None));

        Assert.Same(plan[0], ex.Statement);
        Assert.True(connection.RolledBack);
        Assert.False(connection.Committed);
        Assert.Contains("does not report rows affected", ex.Message);
    }

    [Fact]
    public async Task Apply_InsertWithMinusOne_StillCommits()
    {
        var plan = EditPlanner.Plan(Sqlite, Items(), ["id"], [new InsertRow(EditTestData.Row(("id", 5L)))]);
        var connection = new FakeDbConnection(affected: -1);

        await EditApplier.ApplyAsync(connection, plan, CancellationToken.None);

        Assert.True(connection.Committed);
    }

    [Fact]
    public async Task Apply_ExactlyOneRow_Commits()
    {
        var plan = EditPlanner.Plan(Sqlite, Items(), ["id"], [new DeleteRow(EditTestData.Row(("id", 1L)))]);
        var connection = new FakeDbConnection(affected: 1);

        await EditApplier.ApplyAsync(connection, plan, CancellationToken.None);

        Assert.True(connection.Committed);
    }
}

public class SqlPreviewLiteralTests
{
    private static TableInfo People() => EditTestData.Table("dbo", "people", false,
        EditTestData.Col("id", typeof(int), pk: true, nullable: false),
        EditTestData.Col("name", typeof(string), ordinal: 1));

    [Fact]
    public void SqlServer_StringLiterals_GetTheNPrefix()
    {
        var plan = EditPlanner.Plan(new SqlServerDriver(), People(), ["id"],
        [
            new UpdateRow(EditTestData.Row(("id", 1)), EditTestData.Row(("name", "Zoë O'Brien"))),
            new InsertRow(EditTestData.Row(("id", 2), ("name", "日本語"))),
        ]);

        Assert.Equal("UPDATE [dbo].[people] SET [name] = N'Zoë O''Brien' WHERE [id] = 1", plan[0].PreviewSql);
        Assert.Equal("INSERT INTO [dbo].[people] ([id], [name]) VALUES (2, N'日本語')", plan[1].PreviewSql);
    }

    [Fact]
    public void SqlServer_KeyStringLiteral_GetsTheNPrefix_AndTheExecutedCommandIsUntouched()
    {
        var table = EditTestData.Table("dbo", "people", false, EditTestData.Col("name", typeof(string), pk: true, nullable: false));

        var stmt = Assert.Single(EditPlanner.Plan(new SqlServerDriver(), table, ["name"], [new DeleteRow(EditTestData.Row(("name", "Zoë")))]));

        Assert.Equal("DELETE FROM [dbo].[people] WHERE [name] = N'Zoë'", stmt.PreviewSql);
        Assert.Equal("DELETE FROM [dbo].[people] WHERE [name] = @p0", stmt.Command.Sql);
    }

    [Fact]
    public void OtherEngines_KeepPlainLiterals()
    {
        var table = EditTestData.Table("public", "people", false,
            EditTestData.Col("id", typeof(int), pk: true, nullable: false), EditTestData.Col("name", typeof(string), ordinal: 1));

        var pg = Assert.Single(EditPlanner.Plan(new PostgresDriver(), table, ["id"],
            [new UpdateRow(EditTestData.Row(("id", 1)), EditTestData.Row(("name", "Zoë")))]));
        var lite = Assert.Single(EditPlanner.Plan(new SqliteDriver(), table, ["id"],
            [new UpdateRow(EditTestData.Row(("id", 1)), EditTestData.Row(("name", "Zoë")))]));

        Assert.Contains("= 'Zoë'", pg.PreviewSql);
        Assert.DoesNotContain("N'", pg.PreviewSql);
        Assert.Contains("= 'Zoë'", lite.PreviewSql);
        Assert.DoesNotContain("N'", lite.PreviewSql);
    }
}

public class KeyResolverStarTests
{
    private static readonly Guid ConnId = Guid.NewGuid();

    private static AppConfig Star()
    {
        var cfg = AppConfig.Empty();
        cfg.VirtualKeys[ConnId] = new Dictionary<string, string[]> { ["dbo.t"] = ["*"] };
        return cfg;
    }

    private static ColumnInfo Typed(string name, string dataType, Type clr, int ordinal) =>
        new(name, dataType, clr, true, false, ordinal);

    private static ConnectionConfig Conn(DbKind kind) => EditTestData.Conn(ConnId) with { Kind = kind };

    [Fact]
    public void Star_OnSqlServer_LeavesOutColumnsWithoutEquality()
    {
        var t = EditTestData.Table("dbo", "t", false,
            Typed("a", "int", typeof(int), 0), Typed("notes", "ntext", typeof(string), 1),
            Typed("b", "nvarchar(20)", typeof(string), 2), Typed("doc", "xml", typeof(string), 3),
            Typed("pic", "image", typeof(byte[]), 4));

        Assert.Equal(new[] { "a", "b" }, KeyResolver.Resolve(t, Conn(DbKind.SqlServer), Star()));
    }

    [Fact]
    public void Star_OnSqlServer_WithNoUsableColumn_IsReadOnly()
    {
        var t = EditTestData.Table("dbo", "t", false, Typed("notes", "text", typeof(string), 0));

        Assert.Null(KeyResolver.Resolve(t, Conn(DbKind.SqlServer), Star()));
    }

    [Fact]
    public void Star_OnPostgres_LeavesOutJson_AndSqliteKeepsEverything()
    {
        var t = EditTestData.Table("dbo", "t", false,
            Typed("a", "integer", typeof(int), 0), Typed("j", "json", typeof(string), 1));

        Assert.Equal(new[] { "a" }, KeyResolver.Resolve(t, Conn(DbKind.Postgres), Star()));
        Assert.Equal(new[] { "a", "j" }, KeyResolver.Resolve(t, Conn(DbKind.Sqlite), Star()));
    }
}
