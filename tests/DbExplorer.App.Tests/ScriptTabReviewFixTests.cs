using System.Text;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.Tests;

/// <summary>Review round 1: cancel while connecting, stale selection, file encodings.</summary>
public class ScriptTabReviewFixTests : IDisposable
{
    private readonly ScriptTabFixture _t = new();

    public ScriptTabReviewFixTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public void Dispose() => _t.Dispose();

    // ---- 1. Cancel while connecting ----

    [Fact]
    public async Task Cancel_WhileConnecting_ReleasesTheTabAtOnce_WithoutException()
    {
        var never = new TaskCompletionSource<Session?>();   // an unreachable host: the connect never answers
        var hooks = _t.Hooks with { ResolveSession = _ => never.Task };
        var tab = _t.Track(new ScriptTabViewModel(_t.Config, null, hooks, 1) { Text = "SELECT 1" });

        var run = tab.RunScriptAsync();
        Assert.True(tab.IsRunning);
        await Task.Delay(50);
        Assert.False(run.IsCompleted);

        tab.Cancel();

        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))));
        await run;   // must not throw
        Assert.False(tab.IsRunning);
        Assert.Null(tab.ErrorMessage);
        Assert.Empty(tab.Results);
        Assert.Contains("cancel", _t.Status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancel_WhileConnecting_PassesTheCancellationToTheConnect()
    {
        CancellationToken seen = default;
        var started = new TaskCompletionSource();
        var hooks = _t.Hooks with
        {
            ResolveSession = async ct =>
            {
                seen = ct;
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            },
        };
        var tab = _t.Track(new ScriptTabViewModel(_t.Config, null, hooks, 1) { Text = "SELECT 1" });

        var run = tab.RunScriptAsync();
        await started.Task;
        tab.Cancel();
        await run;

        Assert.True(seen.IsCancellationRequested);
        Assert.False(tab.IsRunning);
    }

    [Fact]
    public async Task RunAgain_AfterACancelledConnect_Works()
    {
        var calls = 0;
        var hooks = _t.Hooks with
        {
            ResolveSession = ct => ++calls == 1
                ? new TaskCompletionSource<Session?>().Task
                : _t.Sessions.GetOrOpenAsync(_t.Config, ct).ContinueWith(t => (Session?)t.Result, ct),
        };
        var tab = _t.Track(new ScriptTabViewModel(_t.Config, null, hooks, 1) { Text = "SELECT 1" });
        var first = tab.RunScriptAsync();
        tab.Cancel();
        await first;

        await tab.RunScriptAsync();

        Assert.Equal(1L, Assert.Single(tab.Results).Sets[0].Rows[0][0]);
    }

    // ---- 2. Selection does not survive a fresh editor ----

    [Fact]
    public async Task EditorAttached_ClearsTheSelection_SoTheStatementAtTheCaretRuns()
    {
        var tab = await _t.NewTab("SELECT 1 AS a;\nSELECT 2 AS b;");
        tab.CaretOffset = 3;
        tab.Selection = (16, 30);   // selected in the previous editor, which a tab switch has thrown away

        tab.EditorAttached();   // what the view does when it attaches a new editor
        await tab.RunStatementAsync();

        Assert.Null(tab.Selection);
        Assert.Equal("a", Assert.Single(tab.Results).Sets[0].Columns[0].Name);
    }

    // ---- 4. Encodings ----

    private string Write(string name, byte[] bytes)
    {
        var path = _t.TempFile(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private async Task AssertRoundTrip(byte[] original, string expectedText)
    {
        var path = Write("rt.sql", original);
        var tab = await _t.NewTab();

        await tab.OpenAsync(path);
        Assert.Equal(expectedText, tab.Text);
        await tab.SaveAsync(null);   // no edit

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public Task Windows1252File_RoundTripsByteForByte()
    {
        const string text = "SELECT 'é à ç ü' AS café -- œuvre €";
        var bytes = Encoding.GetEncoding(1252).GetBytes(text);
        Assert.DoesNotContain((byte)0xEF, bytes);
        return AssertRoundTrip(bytes, text);
    }

    [Fact]
    public Task Utf8WithBom_KeepsItsBom()
    {
        const string text = "SELECT 'é' -- 日本";
        return AssertRoundTrip([.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes(text)], text);
    }

    [Fact]
    public Task Utf8WithoutBom_StaysWithoutBom()
    {
        const string text = "SELECT 'é' -- 日本";
        return AssertRoundTrip(Encoding.UTF8.GetBytes(text), text);
    }

    [Fact]
    public Task Utf16WithBom_KeepsItsEncoding()
    {
        const string text = "SELECT 'é'";
        return AssertRoundTrip([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)], text);
    }

    [Fact]
    public async Task EditingAWindows1252File_KeepsItWindows1252()
    {
        var path = Write("ansi.sql", Encoding.GetEncoding(1252).GetBytes("SELECT 'é'"));
        var tab = await _t.NewTab();
        await tab.OpenAsync(path);

        tab.Text += " -- à";
        await tab.SaveAsync(null);

        Assert.Equal(Encoding.GetEncoding(1252).GetBytes("SELECT 'é' -- à"), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task NewFile_IsUtf8WithoutBom()
    {
        var tab = await _t.NewTab("SELECT 'é'");
        var path = _t.TempFile("new.sql");

        await tab.SaveAsync(path);

        Assert.Equal(Encoding.UTF8.GetBytes("SELECT 'é'"), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Windows1252File_GivenCharactersItCannotHold_IsSavedAsUtf8_NeverAsQuestionMarks()
    {
        var path = Write("ansi2.sql", Encoding.GetEncoding(1252).GetBytes("SELECT 'é'"));
        var tab = await _t.NewTab();
        await tab.OpenAsync(path);

        tab.Text += " -- 日本";
        await tab.SaveAsync(null);

        Assert.Equal("SELECT 'é' -- 日本", File.ReadAllText(path, new UTF8Encoding(false, true)));
        Assert.Contains("UTF-8", _t.Status.Message);   // the conversion is announced, and not overwritten by a plain "Saved"
        Assert.Contains(path, _t.Status.Message);
    }

    [Fact]
    public async Task RestoredFileBackedTab_KeepsTheEncodingOfItsFile()
    {
        var path = Write("restored.sql", Encoding.GetEncoding(1252).GetBytes("SELECT 'é'"));
        var saved = new DbExplorer.Core.Scripting.SavedScript(Guid.NewGuid(), _t.Config.Id, "SELECT 'é' -- edited", path);
        var tab = _t.Track(new ScriptTabViewModel(_t.Config, null, _t.Hooks, 1, saved));

        await tab.SaveAsync(null);

        Assert.Equal(Encoding.GetEncoding(1252).GetBytes("SELECT 'é' -- edited"), File.ReadAllBytes(path));
    }
}
