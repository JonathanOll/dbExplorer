using DbExplorer.Core.Model;
using DbExplorer.Core.Plans;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Tests;

public class QueryHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-history-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "history.jsonl");
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static HistoryEntry Entry(string sql, Guid? conn = null, string name = "Shop", string? error = null, int minutesAgo = 0, PlanMode plan = PlanMode.None) =>
        new(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-minutesAgo), conn ?? A, name, DbKind.Sqlite, "shop.db", sql, 12, 3, error, plan);

    [Fact]
    public void Append_ThenQuery_ReturnsMostRecentFirst_AndSurvivesAReload()
    {
        var h = new QueryHistory(File_);
        h.Append(Entry("SELECT 1", minutesAgo: 2));
        h.Append(Entry("SELECT 2", minutesAgo: 1));

        Assert.Equal(["SELECT 2", "SELECT 1"], h.Query().Select(e => e.Sql).ToArray());
        // a new instance reads the file: nothing is kept in memory only
        var again = new QueryHistory(File_).Query();
        Assert.Equal(2, again.Count);
        Assert.Equal(PlanMode.None, again[0].Plan);
        Assert.Equal(DbKind.Sqlite, again[0].Kind);
        Assert.Equal(3, again[0].Rows);
    }

    [Fact]
    public void Constructing_ReadsNothing_AndMissingFileIsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "garbage\n");
        var h = new QueryHistory(File_);   // would throw or change the file if it read it now
        Assert.Equal("garbage\n", File.ReadAllText(File_));
        Assert.Empty(h.Query());
        Assert.Empty(new QueryHistory(Path.Combine(_dir, "none.jsonl")).Query());
    }

    [Fact]
    public void Query_FiltersByTextConnectionErrorsAndDate_IgnoringCase()
    {
        var h = new QueryHistory(File_);
        h.Append(Entry("SELECT * FROM orders", A, "Shop", minutesAgo: 120));
        h.Append(Entry("DELETE FROM orders", A, "Shop", error: "Refused: read-only", minutesAgo: 30));
        h.Append(Entry("select count(*) from Customers", B, "Billing", minutesAgo: 1));

        Assert.Equal(2, h.Query("ORDERS").Count);
        Assert.Single(h.Query("billing"));                       // the connection name matches too
        Assert.Single(h.Query(connectionId: B));
        Assert.Equal("DELETE FROM orders", Assert.Single(h.Query(onlyErrors: true)).Sql);
        Assert.Equal(2, h.Query(since: DateTime.UtcNow.AddHours(-1)).Count);
        Assert.Single(h.Query("orders", A, true));
        Assert.Equal(2, h.Query(limit: 2).Count);
        Assert.Empty(h.Query("nothing like this"));
    }

    [Fact]
    public void Cap_KeepsTheNewest_AndCompactsTheFile()
    {
        var h = new QueryHistory(File_, maxEntries: 20);
        for (var i = 0; i < 50; i++) h.Append(Entry($"SELECT {i}"));

        var kept = h.Query(limit: 100);
        Assert.Equal(20, kept.Count);
        Assert.Equal("SELECT 49", kept[0].Sql);
        Assert.Equal("SELECT 30", kept[^1].Sql);

        var fresh = new QueryHistory(File_, maxEntries: 20);   // the file itself was compacted too (never grows without bound)
        Assert.Equal(20, fresh.Query(limit: 100).Count);
        Assert.True(File.ReadAllLines(File_).Length < 30);
    }

    [Fact]
    public void LoadingAnOverlongFile_TrimsToTheCap()
    {
        var big = new QueryHistory(File_, maxEntries: 100);
        for (var i = 0; i < 30; i++) big.Append(Entry($"SELECT {i}"));

        var small = new QueryHistory(File_, maxEntries: 10);
        Assert.Equal(10, small.Query(limit: 100).Count);
        Assert.Equal(10, File.ReadAllLines(File_).Length);
    }

    [Fact]
    public void LongSql_IsTruncatedWithAMarker()
    {
        var h = new QueryHistory(File_, maxSqlLength: 100);
        h.Append(Entry(new string('x', 250)));

        var sql = h.Query().Single().Sql;
        Assert.StartsWith(new string('x', 100), sql);
        Assert.Contains("truncated (150 more characters)", sql);
        Assert.True(sql.Length < 200);
    }

    [Fact]
    public void CorruptLines_AreSkipped_AndTheFileIsNeverDeleted()
    {
        var h = new QueryHistory(File_);
        h.Append(Entry("SELECT 1"));
        File.AppendAllText(File_, "{not json\n\n{\"Id\":\"x\"}\n");
        h.Append(Entry("SELECT 2"));

        var fresh = new QueryHistory(File_);
        Assert.Equal(["SELECT 2", "SELECT 1"], fresh.Query().Select(e => e.Sql).ToArray());
        Assert.True(File.Exists(File_));
    }

    [Fact]
    public void Delete_RemovesOnlyTheGivenEntries_AndPersists()
    {
        var h = new QueryHistory(File_);
        var one = Entry("SELECT 1");
        h.Append(one);
        h.Append(Entry("SELECT 2"));

        h.Delete([one.Id, Guid.NewGuid()]);

        Assert.Equal("SELECT 2", Assert.Single(new QueryHistory(File_).Query()).Sql);
    }

    [Fact]
    public void Clear_EmptiesTheHistory_AndLaterAppendsWork()
    {
        var h = new QueryHistory(File_);
        h.Append(Entry("SELECT 1"));

        h.Clear();
        Assert.Empty(h.Query());
        Assert.Empty(new QueryHistory(File_).Query());

        h.Append(Entry("SELECT 2"));
        Assert.Single(new QueryHistory(File_).Query());
    }

    [Fact]
    public void IoErrors_NeverThrow()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(File_);   // the "file" is a folder: every read and write fails
        var h = new QueryHistory(File_);

        h.Append(Entry("SELECT 1"));
        Assert.Empty(h.Query());
        h.Delete([Guid.NewGuid()]);
        h.Clear();
    }

    [Fact]
    public async Task ConcurrentAppends_AreAllKept()
    {
        var h = new QueryHistory(File_);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++) h.Append(Entry($"SELECT {t}-{i}"));
        })));

        Assert.Equal(200, new QueryHistory(File_).Query(limit: 1000).Count);
    }
}
