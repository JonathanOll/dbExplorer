using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.Tests;

/// <summary>The autocomplete of the script editor: when the list opens, how it closes, what Enter / Tab insert.</summary>
public class SqlCompletionViewTests : IDisposable
{
    private readonly ScriptTabFixture _t = new();

    public void Dispose() => _t.Dispose();

    private async Task<(Window Window, ScriptTabView View, TextEditor Editor, ScriptTabViewModel Tab)> Open(string text, bool withSession = true)
    {
        var tab = await _t.NewTab(text, withSession);
        var window = new Window { Content = new ContentControl { Content = tab }, Width = 1100, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var view = window.GetVisualDescendants().OfType<ScriptTabView>().Single();
        var editor = view.FindControl<TextEditor>("Editor")!;
        editor.TextArea.Focus();
        Pump();
        editor.CaretOffset = editor.Text.Length;
        return (window, view, editor, tab);
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    // One text input per character, as a keyboard does.
    private static void Type(Window window, string text)
    {
        foreach (var c in text)
        {
            window.KeyTextInput(c.ToString());
            Pump();
        }
    }

    private static void Press(Window window, Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, physical, null);
        Pump();
    }

    private static void CtrlSpace(Window window) => Press(window, Key.Space, PhysicalKey.Space, RawInputModifiers.Control);

    private static string[] Offered(ScriptTabView view) =>
        Shown(view).Select(d => ((SqlCompletionData)d).Item.Label).ToArray();

    // CurrentList is only set once the list has been filtered by typing.
    private static IList<AvaloniaEdit.CodeCompletion.ICompletionData> Shown(ScriptTabView view) =>
        view.OpenCompletion!.CompletionList.CurrentList ?? view.OpenCompletion.CompletionList.CompletionData;

    [AvaloniaFact]
    public async Task CtrlSpace_OpensTheListAtTheCaret_WithoutTakingTheFocus()
    {
        var (window, view, editor, _) = await Open("SELECT * FROM ");

        CtrlSpace(window);

        Assert.NotNull(view.OpenCompletion);
        Assert.Contains("big", Offered(view));
        Assert.Contains("t", Offered(view));
        Assert.True(editor.TextArea.IsFocused);
        Assert.Equal("SELECT * FROM ", editor.Text);   // the chord types nothing
        window.Close();
    }

    [AvaloniaFact]
    public async Task CtrlSpace_WithoutALoadedSchema_OffersKeywordsAndFunctionsOnly()
    {
        var (window, view, _, _) = await Open("SELECT ", withSession: false);

        CtrlSpace(window);

        Assert.NotNull(view.OpenCompletion);
        Assert.All(Shown(view), d =>
            Assert.True(((SqlCompletionData)d).Item.Kind is CompletionKind.Keyword or CompletionKind.Function));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Dot_AfterAName_OpensTheColumnsOfTheAlias_AndEnterInsertsOne()
    {
        var (window, view, editor, tab) = await Open("SELECT x FROM t x");
        editor.CaretOffset = "SELECT x".Length;

        Type(window, ".");

        Assert.NotNull(view.OpenCompletion);
        Assert.Equal(["id", "name"], Offered(view));
        Press(window, Key.Enter, PhysicalKey.Enter);

        Assert.Null(view.OpenCompletion);
        Assert.Equal("SELECT x.id FROM t x", editor.Text);
        Assert.Equal("SELECT x.id".Length, editor.CaretOffset);
        Assert.Equal(editor.Text, tab.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Dot_NotAfterAName_OpensNothing()
    {
        var (window, view, _, _) = await Open("SELECT 1");

        Type(window, ".");
        Type(window, "5");

        Assert.Null(view.OpenCompletion);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TwoLetters_OpenTheList_OneLetterDoesNot()
    {
        var (window, view, _, _) = await Open("SELECT * FROM ");

        Type(window, "b");
        Assert.Null(view.OpenCompletion);

        Type(window, "i");
        Assert.NotNull(view.OpenCompletion);
        Assert.Equal("big", Offered(view)[0]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Enter_ReplacesExactlyTheTypedPrefix()
    {
        var (window, view, editor, _) = await Open("SELECT * FROM ");
        Type(window, "bi");

        Press(window, Key.Enter, PhysicalKey.Enter);

        Assert.Equal("SELECT * FROM big", editor.Text);   // no line break, nothing left of "bi"
        Assert.Null(view.OpenCompletion);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Tab_Accepts_TheSelectedItem()
    {
        var (window, view, editor, _) = await Open("SELECT * FROM ");
        CtrlSpace(window);
        Type(window, "t");
        Assert.NotNull(view.OpenCompletion);

        Press(window, Key.Tab, PhysicalKey.Tab);

        Assert.Equal("SELECT * FROM t", editor.Text);
        Assert.Null(view.OpenCompletion);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(" ")]
    [InlineData(",")]
    [InlineData("(")]
    [InlineData(";")]
    public async Task ANonIdentifierCharacter_ClosesTheListWithoutAccepting(string typed)
    {
        var (window, view, editor, _) = await Open("SELECT * FROM ");
        Type(window, "bi");
        Assert.NotNull(view.OpenCompletion);

        Type(window, typed);

        Assert.Equal("SELECT * FROM bi" + typed, editor.Text);   // "bi" was not completed to "big"
        Assert.Null(view.OpenCompletion);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Escape_ClosesTheListAndKeepsTheText()
    {
        var (window, view, editor, _) = await Open("SELECT * FROM ");
        Type(window, "bi");
        Assert.NotNull(view.OpenCompletion);

        Press(window, Key.Escape, PhysicalKey.Escape);

        Assert.Null(view.OpenCompletion);
        Assert.Equal("SELECT * FROM bi", editor.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task UpAndDown_MoveTheSelection_TypingNarrowsTheList()
    {
        var (window, view, _, _) = await Open("SELECT * FROM ");
        CtrlSpace(window);
        var list = view.OpenCompletion!.CompletionList;
        var first = list.SelectedItem;

        Press(window, Key.Down, PhysicalKey.ArrowDown);
        Assert.NotSame(first, list.SelectedItem);
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Assert.Same(first, list.SelectedItem);

        var all = Shown(view).Count;
        Type(window, "b");
        Assert.NotNull(view.OpenCompletion);
        Assert.True(Shown(view).Count < all);
        Assert.StartsWith("b", Offered(view)[0], StringComparison.OrdinalIgnoreCase);   // the best matches lead

        Type(window, "zz");   // nothing matches: the list closes
        Assert.Null(view.OpenCompletion);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("SELECT 'abc")]
    [InlineData("SELECT * FROM t -- abc")]
    [InlineData("SELECT * FROM t /* abc")]
    public async Task NeverOpensInsideAStringOrAComment(string text)
    {
        var (window, view, _, _) = await Open(text);

        Type(window, "de");
        Assert.Null(view.OpenCompletion);
        CtrlSpace(window);
        Assert.Null(view.OpenCompletion);
        Type(window, ".");
        Assert.Null(view.OpenCompletion);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AsYouType_DoesNotOpenWhileTheScriptRuns()
    {
        var (window, view, _, tab) = await Open(ScriptTabFixture.Endless);
        var run = tab.RunStatementAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!tab.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(tab.IsRunning);

        Type(window, " FROM bi");
        Assert.Null(view.OpenCompletion);

        tab.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        window.Close();
    }

    [Fact]
    public void CtrlSpace_IsMappedAndListedAmongTheShortcuts()
    {
        Assert.Equal(ScriptKeyAction.Complete, ScriptTabKeys.Resolve(Key.Space, KeyModifiers.Control));
        Assert.Equal(ScriptKeyAction.None, ScriptTabKeys.Resolve(Key.Space, KeyModifiers.None));
        Assert.Contains(ScriptTabKeys.Shortcuts, s => s.Keys == "Ctrl+Space");
        Assert.Contains(ShortcutCatalog.Sections.SelectMany(x => x.Items), i => i.Keys == "Ctrl+Space");
    }
}
