using System.Data;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;

namespace DbExplorer.Core.Tests;

/// <summary>
/// Date/time parameters get the exact type of their column. Only the parameter objects are inspected (no server needed);
/// that the servers then behave as intended is still to be confirmed with the DBX_MSSQL / DBX_PG integration tests.
/// </summary>
public class ParameterTypingTests
{
    private static ColumnInfo Col(string name, string dataType, Type clr) => new(name, dataType, clr, true, false, 0);

    private static SqlParameter Sql(object value, string dataType)
    {
        var p = new SqlParameter { ParameterName = "@p0", Value = value };
        ParameterTyping.Configure(p, Col("c", dataType, value.GetType()));
        return p;
    }

    private static NpgsqlParameter Pg(object value, string dataType)
    {
        var p = new NpgsqlParameter { ParameterName = "@p0", Value = value };
        ParameterTyping.Configure(p, Col("c", dataType, value.GetType()));
        return p;
    }

    [Theory]
    [InlineData("datetime2(7)", 7)]
    [InlineData("datetime2(3)", 3)]
    [InlineData("datetime2(0)", 0)]
    public void SqlServer_Datetime2_KeepsColumnScale(string dataType, int scale)
    {
        var p = Sql(new DateTime(2024, 3, 5, 14, 7, 9, 123), dataType);

        Assert.Equal(SqlDbType.DateTime2, p.SqlDbType);
        Assert.Equal(scale, p.Scale);
    }

    [Theory]
    [InlineData("datetime", SqlDbType.DateTime)]
    [InlineData("smalldatetime", SqlDbType.SmallDateTime)]
    [InlineData("date", SqlDbType.Date)]
    public void SqlServer_OtherDateTypes(string dataType, SqlDbType expected)
    {
        Assert.Equal(expected, Sql(new DateTime(2024, 3, 5), dataType).SqlDbType);
    }

    [Fact]
    public void SqlServer_TimeAndDatetimeoffset_KeepScale()
    {
        var time = Sql(TimeSpan.FromHours(1), "time(4)");
        Assert.Equal(SqlDbType.Time, time.SqlDbType);
        Assert.Equal(4, time.Scale);

        var dto = Sql(new DateTimeOffset(2024, 3, 5, 1, 2, 3, TimeSpan.FromHours(2)), "datetimeoffset(2)");
        Assert.Equal(SqlDbType.DateTimeOffset, dto.SqlDbType);
        Assert.Equal(2, dto.Scale);
    }

    [Fact]
    public void SqlServer_NonDateValuesAndColumns_AreLeftToTheProvider()
    {
        var number = Sql(5, "int");
        Assert.Equal(SqlDbType.Int, number.SqlDbType); // inferred from the value, untouched

        var text = Sql("abc", "nvarchar(20)");
        Assert.Equal(SqlDbType.NVarChar, text.SqlDbType);

        // A string typed into a datetime2 column is converted to DateTime by the planner first; an unconverted value is not forced.
        var raw = Sql("2024-01-01", "datetime2(7)");
        Assert.Equal(SqlDbType.NVarChar, raw.SqlDbType);
    }

    [Fact]
    public void Postgres_Timestamptz_IsSentAsUtc()
    {
        var unspecified = Pg(new DateTime(2024, 3, 5, 14, 7, 9, DateTimeKind.Unspecified), "timestamp with time zone");
        Assert.Equal(NpgsqlDbType.TimestampTz, unspecified.NpgsqlDbType);
        Assert.Equal(DateTimeKind.Utc, ((DateTime)unspecified.Value!).Kind);
        Assert.Equal(new DateTime(2024, 3, 5, 14, 7, 9, DateTimeKind.Utc), unspecified.Value); // same wall clock, now UTC

        var local = Pg(new DateTime(2024, 3, 5, 14, 7, 9, DateTimeKind.Local), "timestamp(3) with time zone");
        Assert.Equal(NpgsqlDbType.TimestampTz, local.NpgsqlDbType);
        Assert.Equal(DateTimeKind.Utc, ((DateTime)local.Value!).Kind);
    }

    [Fact]
    public void Postgres_Timestamp_IsUnspecified_AndDateIsDate()
    {
        var ts = Pg(new DateTime(2024, 3, 5, 14, 7, 9, DateTimeKind.Utc), "timestamp without time zone");
        Assert.Equal(NpgsqlDbType.Timestamp, ts.NpgsqlDbType);
        Assert.Equal(DateTimeKind.Unspecified, ((DateTime)ts.Value!).Kind);

        Assert.Equal(NpgsqlDbType.Date, Pg(new DateTime(2024, 3, 5), "date").NpgsqlDbType);
    }

    [Fact]
    public void Sqlite_ParameterIsUnchanged()
    {
        var p = new SqliteParameter { ParameterName = "@p0", Value = new DateTime(2024, 3, 5) };
        ParameterTyping.Configure(p, Col("created", "DATETIME", typeof(DateTime)));

        Assert.Equal(new DateTime(2024, 3, 5), p.Value);
    }

    // ---- parsing of typed text ----

    /// <summary>The value a filter on <paramref name="column"/> binds for <paramref name="text"/>.</summary>
    private static object Converted(ColumnInfo column, string text)
    {
        var table = EditTestData.Table("public", "t", false, column);
        var q = new TableQuery(table, [new ColumnFilter(column.Name, "=" + text)], null, [], 0, 10);
        return TableQueryBuilder.Build(new PostgresDriver(), q).Params[0].Value!;
    }

    [Fact]
    public void ConvertValue_Timestamptz_ParsesAsUtc_AndHonoursAnExplicitOffset()
    {
        var column = Col("at", "timestamp with time zone", typeof(DateTime));

        var plain = (DateTime)Converted(column, "2024-03-05 14:07:09");
        Assert.Equal(DateTimeKind.Utc, plain.Kind);
        Assert.Equal(new DateTime(2024, 3, 5, 14, 7, 9, DateTimeKind.Utc), plain);

        var offset = (DateTime)Converted(column, "2024-03-05T14:07:09+02:00");
        Assert.Equal(new DateTime(2024, 3, 5, 12, 7, 9, DateTimeKind.Utc), offset);
    }

    [Fact]
    public void ConvertValue_PlainTimestampAndSqlServerDatetime_StayUnspecified()
    {
        Assert.Equal(DateTimeKind.Unspecified,
            ((DateTime)Converted(Col("a", "timestamp without time zone", typeof(DateTime)), "2024-03-05 14:07:09")).Kind);
        Assert.Equal(DateTimeKind.Unspecified,
            ((DateTime)Converted(Col("a", "datetime2(7)", typeof(DateTime)), "2024-03-05 14:07:09")).Kind);
    }

    // ---- the parameters actually created for a command ----

    [Fact]
    public async Task Apply_BindsKeyAndEditedDatesWithTheColumnType()
    {
        var table = EditTestData.Table("dbo", "events", false,
            new ColumnInfo("id", "int", typeof(int), false, true, 0),
            new ColumnInfo("at", "datetime2(3)", typeof(DateTime), true, false, 1));
        var driver = new SqlServerDriver();
        var plan = EditPlanner.Plan(driver, table, ["id"],
            [new UpdateRow(EditTestData.Row(("id", 1)), EditTestData.Row(("at", "2024-03-06 01:02:03.456")))]);
        var connection = new FakeDbConnection(affected: 1, () => new SqlParameter());

        await EditApplier.ApplyAsync(connection, plan, CancellationToken.None);

        var parameters = connection.Commands.Single().Parameters.Cast<SqlParameter>().ToList();
        var at = parameters.Single(p => p.Value is DateTime);
        Assert.Equal(SqlDbType.DateTime2, at.SqlDbType);
        Assert.Equal(3, at.Scale);
        Assert.Equal(new DateTime(2024, 3, 6, 1, 2, 3, 456), at.Value);
        Assert.Equal(SqlDbType.Int, parameters.Single(p => p.Value is int).SqlDbType);
    }

    [Fact]
    public async Task Apply_DatetimeKeyColumn_IsBoundWithItsScale()
    {
        var table = EditTestData.Table("dbo", "events", false,
            new ColumnInfo("at", "datetime2(7)", typeof(DateTime), false, false, 0),
            new ColumnInfo("note", "nvarchar(10)", typeof(string), true, false, 1));
        var driver = new SqlServerDriver();
        var key = new DateTime(2024, 3, 5, 14, 7, 9).AddTicks(1234567 % 10_000_000);
        var plan = EditPlanner.Plan(driver, table, ["at"],
            [new DeleteRow(EditTestData.Row(("at", key)))]);
        var connection = new FakeDbConnection(affected: 1, () => new SqlParameter());

        await EditApplier.ApplyAsync(connection, plan, CancellationToken.None);

        var p = (SqlParameter)connection.Commands.Single().Parameters[0];
        Assert.Equal(SqlDbType.DateTime2, p.SqlDbType);
        Assert.Equal(7, p.Scale);
        Assert.Equal(key.Ticks, ((DateTime)p.Value!).Ticks);
    }

    [Fact]
    public void Filter_OnDatetime2Column_CarriesTheColumnSoTheLoaderCanTypeIt()
    {
        var table = EditTestData.Table("dbo", "events", false,
            new ColumnInfo("at", "datetime2(3)", typeof(DateTime), true, false, 0));
        var q = new TableQuery(table, [new ColumnFilter("at", "=2024-03-05 14:07:09.123")], null, [], 0, 10);

        var spec = TableQueryBuilder.Build(new SqlServerDriver(), q);

        var p = Assert.Single(spec.Params);
        Assert.Equal("datetime2(3)", p.Column!.DataType);
    }
}
