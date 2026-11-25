using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Tests;

public sealed class ScriptSearchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-search-" + Guid.NewGuid().ToString("N"));

    public ScriptSearchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static List<ScriptMatch> InText(string text, ScriptSearchOptions o) =>
        new ScriptSearch(o).SearchText(text, "src", "name", null, false).ToList();

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Plain_IsCaseInsensitiveByDefault_AndReportsLineAndColumn()
    {
        var hits = InText("select 1;\nSELECT * FROM Orders;\r\nselect 2", new("select"));

        Assert.Equal([1, 2, 3], hits.Select(h => h.Line));
        Assert.Equal([0, 0, 0], hits.Select(h => h.Column));
        Assert.Equal(10, hits[1].Offset);
        Assert.Equal(33, hits[2].Offset);   // "\r\n" counts as one line break
    }

    [Fact]
    public void CaseSensitive_SkipsOtherCasing()
    {
        var hits = InText("select\nSELECT", new("SELECT", CaseSensitive: true));

        Assert.Single(hits);
        Assert.Equal(2, hits[0].Line);
    }

    [Fact]
    public void WholeWord_DoesNotMatchInsideAnIdentifier()
    {
        var hits = InText("orders\norders_old\nmy_orders\norders", new("orders", WholeWord: true));

        Assert.Equal([1, 4], hits.Select(h => h.Line));
    }

    [Fact]
    public void WholeWord_WorksWhenTheTextStartsWithASymbol()
    {
        var hits = InText("a = @id\nb = x@id", new("@id", WholeWord: true));

        Assert.Equal([1], hits.Select(h => h.Line));   // \b would miss line 1; "x@id" is glued to a word, so not whole
    }

    [Fact]
    public void Plain_RegexCharactersAreLiteral()
    {
        var hits = InText("a.b\naxb\nSELECT (1)", new("a.b"));

        Assert.Single(hits);
        Assert.Single(InText("SELECT (1)", new("(1)")));
    }

    [Fact]
    public void Regex_Matches_AndSeveralHitsOnOneLine()
    {
        var hits = InText("id 12, id 345\nnone", new(@"id \d+", UseRegex: true));

        Assert.Equal(2, hits.Count);
        Assert.Equal([0, 7], hits.Select(h => h.Column));
        Assert.Equal([5, 6], hits.Select(h => h.Length));
    }

    [Fact]
    public void Regex_EmptyMatchesAreNotHits()
    {
        Assert.Empty(InText("bbb", new("a*", UseRegex: true)));
    }

    [Fact]
    public void Regex_Invalid_IsReportedWithAMessage()
    {
        var o = new ScriptSearchOptions("(unclosed", UseRegex: true);

        Assert.StartsWith("Invalid regular expression", o.Validate());
        Assert.Throws<ArgumentException>(() => new ScriptSearch(o));
        Assert.NotNull(new ScriptSearchOptions("").Validate());
        Assert.Null(new ScriptSearchOptions("(unclosed").Validate());   // plain text: fine
    }

    [Fact]
    public void Regex_Catastrophic_TimesOutPerLine_AndTheSearchGoesOn()
    {
        var bad = new string('a', 60) + "!";
        var search = new ScriptSearch(new(@"^(a+)+$", UseRegex: true));

        var hits = search.SearchText(bad + "\naaa", "s", "n", null, false).ToList();

        Assert.Single(hits);
        Assert.Equal(2, hits[0].Line);
        Assert.Equal(1, search.Stats.LinesTimedOut);
    }

    [Fact]
    public void LongLine_IsCroppedAroundTheHit()
    {
        var line = new string('x', 500) + " needle " + new string('y', 500);

        var hit = InText(line, new("needle")).Single();

        Assert.True(hit.LineText.Length < 400);
        Assert.Equal("needle", hit.LineText.Substring(hit.Start, hit.Length));
        Assert.Equal(501, hit.Column);
    }

    [Fact]
    public void LeadingIndentation_IsTrimmedFromTheShownLine()
    {
        var hit = InText("      SELECT 1", new("SELECT")).Single();

        Assert.Equal("SELECT 1", hit.LineText);
        Assert.Equal(0, hit.Start);
        Assert.Equal(6, hit.Column);
    }

    [Fact]
    public void Folder_SearchesSubFoldersAndOnlySqlFiles_InPathOrder()
    {
        Write("b.sql", "SELECT customers");
        Write("a/inner.sql", "select customers");
        Write("a/notes.txt", "customers");
        Write(".hidden/x.sql", "customers");
        Write("c.SQL", "customers");

        var hits = new ScriptSearch(new("customers")).SearchFolder(_dir).ToList();

        Assert.Equal(["b.sql", "c.SQL", Path.Combine("a", "inner.sql")], hits.Select(h => h.DisplayName));
        Assert.All(hits, h => Assert.False(h.Unsaved));
    }

    [Fact]
    public void Folder_SkippedPaths_AreNotRead()
    {
        var open = Write("open.sql", "needle");
        Write("closed.sql", "needle");

        var hits = new ScriptSearch(new("needle")).SearchFolder(_dir, new HashSet<string> { open }).ToList();

        Assert.Equal(["closed.sql"], hits.Select(h => h.DisplayName));
    }

    [Fact]
    public void Folder_FileOverFiveMegabytes_IsSkippedAndCounted()
    {
        var big = Path.Combine(_dir, "big.sql");
        using (var f = File.Create(big)) f.SetLength(ScriptSearch.MaxFileBytes + 1);
        Write("small.sql", "needle");
        var search = new ScriptSearch(new("needle"));

        var hits = search.SearchFolder(_dir).ToList();

        Assert.Single(hits);
        Assert.Equal(1, search.Stats.FilesSkippedTooLarge);
        Assert.Equal(1, search.Stats.FilesSearched);
    }

    [Fact]
    public void Folder_BinaryAndLockedFiles_AreToleratedAndCounted()
    {
        File.WriteAllBytes(Path.Combine(_dir, "bin.sql"), [0x00, 0x01, (byte)'n', (byte)'e', (byte)'e', (byte)'d', (byte)'l', (byte)'e', 0x00]);
        var locked = Write("locked.sql", "needle");
        Write("ok.sql", "needle");
        var search = new ScriptSearch(new("needle"));

        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var hits = search.SearchFolder(_dir).ToList();

        Assert.Equal(["ok.sql"], hits.Select(h => h.DisplayName));
        Assert.Equal(1, search.Stats.FilesBinary);
        Assert.Equal(1, search.Stats.FilesUnreadable);
    }

    [Fact]
    public void Folder_DecodesLikeTheScriptTabs_Utf8BomUtf16AndWindows1252()
    {
        File.WriteAllBytes(Path.Combine(_dir, "a.sql"), [0xEF, 0xBB, 0xBF, .. "café needle"u8.ToArray()]);
        File.WriteAllBytes(Path.Combine(_dir, "b.sql"), [.. new byte[] { 0xFF, 0xFE }, .. System.Text.Encoding.Unicode.GetBytes("needle é")]);
        File.WriteAllBytes(Path.Combine(_dir, "c.sql"), [.. "caf"u8.ToArray(), 0xE9, .. " needle"u8.ToArray()]);   // Windows-1252

        var hits = new ScriptSearch(new("needle")).SearchFolder(_dir).ToList();
        Assert.Equal(3, hits.Count);

        var accent = new ScriptSearch(new("café")).SearchFolder(_dir).Select(h => h.DisplayName).ToList();
        Assert.Equal(["a.sql", "c.sql"], accent);
    }

    [Fact]
    public void Folder_Cancellation_StopsTheEnumeration()
    {
        for (var i = 0; i < 20; i++) Write($"s{i:00}.sql", "needle");
        using var cts = new CancellationTokenSource();
        var seen = 0;

        Assert.Throws<OperationCanceledException>(() =>
        {
            foreach (var _ in new ScriptSearch(new("needle")).SearchFolder(_dir, null, cts.Token))
            {
                if (++seen == 3) cts.Cancel();
            }
        });
        Assert.Equal(3, seen);
    }

    [Fact]
    public void Matches_AreLazy_NothingIsReadBeyondWhatIsConsumed()
    {
        for (var i = 0; i < 5; i++) Write($"s{i}.sql", "needle");
        var search = new ScriptSearch(new("needle"));

        var first = search.SearchFolder(_dir).Take(2).ToList();

        Assert.Equal(2, first.Count);
        Assert.True(search.Stats.FilesSearched <= 2);
    }

    [Fact]
    public void SearchText_MarksUnsavedAndTheSource()
    {
        var hit = new ScriptSearch(new("x")).SearchText("x", "tab-1", "Script 1", null, true).Single();

        Assert.True(hit.Unsaved);
        Assert.Equal("tab-1", hit.Source);
        Assert.Null(hit.Path);
    }
}
