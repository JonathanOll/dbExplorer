using DbExplorer.Core.Metadata;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Tests;

public class MetadataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-meta-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static TableInfo Table(string? schema, string name, bool isView, params string[] columns) =>
        new(new TableRef(schema, name),
            isView,
            columns.Select((c, i) => new ColumnInfo(c, "text", typeof(string), true, i == 0, i)).ToList(),
            [],
            []);

    // ---- MetadataCache ----

    [Fact]
    public void Cache_SaveLoad_RoundTrips()
    {
        var cache = new MetadataCache(Path.Combine(_dir, "nested", "cache"));
        var id = Guid.NewGuid();

        cache.Save(id, [
            Table("dbo", "Users", false, "Id", "Email"),
            Table(null, "ActiveUsers", true, "Id")
        ]);

        var loaded = cache.Load(id);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(new TableRef("dbo", "Users"), loaded[0].Ref);
        Assert.False(loaded[0].IsView);
        Assert.Equal(["Id", "Email"], loaded[0].Columns);
        Assert.Equal(new TableRef(null, "ActiveUsers"), loaded[1].Ref);
        Assert.True(loaded[1].IsView);
        Assert.Equal(["Id"], loaded[1].Columns);
    }

    [Fact]
    public void Cache_Load_ReturnsEmptyWhenNoFile()
    {
        var cache = new MetadataCache(_dir);

        Assert.Empty(cache.Load(Guid.NewGuid()));
    }

    [Fact]
    public void Cache_CorruptFile_ReturnsEmpty()
    {
        var cache = new MetadataCache(_dir);
        var id = Guid.NewGuid();
        cache.Save(id, [Table(null, "T", false, "A")]);
        var file = Directory.GetFiles(_dir, "*.json").Single();
        File.WriteAllText(file, "{ not valid json ]");

        Assert.Empty(cache.Load(id));
    }

    [Fact]
    public void Cache_Save_OverwritesPreviousSchema()
    {
        var cache = new MetadataCache(_dir);
        var id = Guid.NewGuid();
        cache.Save(id, [Table(null, "Old", false, "A")]);

        cache.Save(id, [Table(null, "New", false, "B")]);

        Assert.Equal("New", Assert.Single(cache.Load(id)).Ref.Name);
    }

    [Fact]
    public void Cache_Delete_RemovesOnlyThatConnection()
    {
        var cache = new MetadataCache(_dir);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        cache.Save(a, [Table(null, "A", false, "X")]);
        cache.Save(b, [Table(null, "B", false, "X")]);

        cache.Delete(a);
        cache.Delete(Guid.NewGuid()); // unknown id is a no-op

        Assert.Empty(cache.Load(a));
        Assert.Single(cache.Load(b));
    }

    // ---- FuzzyMatcher ----

    [Fact]
    public void Score_NoMatch_ReturnsNull()
    {
        Assert.Null(FuzzyMatcher.Score("xyz", "users"));
        Assert.Null(FuzzyMatcher.Score("sru", "users")); // order matters
        Assert.Null(FuzzyMatcher.Score("userss", "users")); // query longer than candidate
    }

    [Fact]
    public void Score_PrefixBeatsScattered()
    {
        var prefix = FuzzyMatcher.Score("usr", "users");
        var scattered = FuzzyMatcher.Score("usr", "unusual_records");

        Assert.NotNull(prefix);
        Assert.NotNull(scattered);
        Assert.True(prefix > scattered);
    }

    [Fact]
    public void Score_CamelCaseInitials()
    {
        var camel = FuzzyMatcher.Score("pd", "ProductDetail");
        var inner = FuzzyMatcher.Score("pd", "Upload");

        Assert.NotNull(camel);
        Assert.NotNull(inner);
        Assert.True(camel > inner);
    }

    [Fact]
    public void Score_WordBoundaryBeatsMidWord()
    {
        var boundary = FuzzyMatcher.Score("od", "order_detail");
        var mid = FuzzyMatcher.Score("od", "product");

        Assert.True(boundary > mid);
    }

    [Fact]
    public void Score_ShorterCandidateWins()
    {
        Assert.True(FuzzyMatcher.Score("user", "users") > FuzzyMatcher.Score("user", "users_archive_2020"));
    }

    [Fact]
    public void Score_IsCaseInsensitive()
    {
        Assert.NotNull(FuzzyMatcher.Score("USERS", "users"));
        Assert.Equal(FuzzyMatcher.Score("users", "Users"), FuzzyMatcher.Score("USERS", "users"));
    }

    [Fact]
    public void Score_EmptyQuery_MatchesEverything()
    {
        Assert.NotNull(FuzzyMatcher.Score("", "anything"));
    }

    [Fact]
    public void Rank_OrdersByScoreThenName_AndTakes()
    {
        string[] names = ["users_b", "users_a", "unusual_records", "orders", "users"];

        var ranked = FuzzyMatcher.Rank("usr", names, n => n, take: 3);

        // "users" is shortest prefix match; ties between users_a / users_b broken by name.
        Assert.Equal(["users", "users_a", "users_b"], ranked);
    }

    [Fact]
    public void Rank_ExcludesNonMatches()
    {
        var ranked = FuzzyMatcher.Rank("zzz", new[] { "users", "orders" }, n => n, take: 10);

        Assert.Empty(ranked);
    }

    [Fact]
    public void Rank_EmptyQuery_ReturnsFirstNAlphabetical()
    {
        string[] names = ["beta", "Alpha", "delta", "charlie"];

        var ranked = FuzzyMatcher.Rank("", names, n => n, take: 3);

        Assert.Equal(["Alpha", "beta", "charlie"], ranked);
    }

    [Fact]
    public void Rank_NonPositiveTake_ReturnsEmpty()
    {
        Assert.Empty(FuzzyMatcher.Rank("u", new[] { "users" }, n => n, take: 0));
    }

    [Theory]
    [InlineData("usr", "users")]
    [InlineData("ord", "public.order_items")]
    [InlineData("pd", "ProductDetail")]
    [InlineData("sch2", "schema2.table")]
    [InlineData("xyz", "users")]
    [InlineData("sru", "users")]
    [InlineData("userss", "users")]
    public void ScoreFast_MatchesWhatScoreMatches_NeverAboveIt(string query, string candidate)
    {
        var best = FuzzyMatcher.Score(query, candidate);
        var fast = FuzzyMatcher.ScoreFast(query, candidate);

        Assert.Equal(best is null, fast is null);
        if (best is not null) Assert.True(fast <= best);
    }

    [Fact]
    public void ScoreFast_WordAsWritten_ScoresLikeScore()
    {
        Assert.Equal(FuzzyMatcher.Score("users", "users"), FuzzyMatcher.ScoreFast("USERS", "users"));
        Assert.Equal(FuzzyMatcher.Score("ord", "orders"), FuzzyMatcher.ScoreFast("ord", "orders"));
    }
}
