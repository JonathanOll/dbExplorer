using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using DbExplorer.App.Settings;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App.Tests;

/// <summary>
/// Typing in the script editor, sent as real input events: the arrows on a selection, surrounding a selection, closing
/// brackets and quotes. Characters arrive as text input (any keyboard layout), never as key codes.
/// </summary>
public class ScriptEditorTypingTests : IDisposable
{
    private readonly ScriptTabFixture _t = new();

    public void Dispose()
    {
        SetAutoClose(true);
        _t.Dispose();
    }

    private static void SetAutoClose(bool on) =>
        SettingsService.Instance.Update(s => s with { Editor = s.Editor with { AutoCloseBrackets = on } });

    private async Task<(Window Window, ScriptTabView View, TextEditor Editor, ScriptTabViewModel Tab)> Open(string text, int caret = -1)
    {
        var tab = await _t.NewTab(text);
        var window = new Window { Content = new ContentControl { Content = tab }, Width = 1100, Height = 400 };
        window.Show();
        Pump();
        var view = window.GetVisualDescendants().OfType<ScriptTabView>().Single();
        var editor = view.FindControl<TextEditor>("Editor")!;
        editor.TextArea.Focus();
        editor.CaretOffset = caret < 0 ? editor.Text.Length : caret;
        Pump();
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

    private static void Type(Window window, string text)
    {
        foreach (var c in text)
        {
            window.KeyTextInput(c.ToString());
            Pump();
        }
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        Pump();
    }

    private static string Lines() => string.Join('\n', Enumerable.Range(1, 200).Select(i => $"select {i};"));

    [AvaloniaFact]
    public async Task CtrlA_Left_PutsTheCaretAtTheStart_AndScrollsThere()
    {
        var (window, _, editor, tab) = await Open(Lines());

        Press(window, PhysicalKey.A, RawInputModifiers.Control);
        editor.TextArea.Caret.BringCaretToView();
        Pump();
        Assert.True(editor.VerticalOffset > 0);
        Press(window, PhysicalKey.ArrowLeft);

        Assert.Equal(0, editor.CaretOffset);
        Assert.Equal(0, editor.SelectionLength);
        Assert.Equal(0, editor.VerticalOffset);
        Assert.Equal(0, tab.CaretOffset);
        Assert.Null(tab.Selection);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CtrlA_Right_PutsTheCaretAtTheEnd()
    {
        var (window, _, editor, _) = await Open(Lines(), caret: 0);

        Press(window, PhysicalKey.A, RawInputModifiers.Control);
        Press(window, PhysicalKey.ArrowRight);

        Assert.Equal(editor.Text.Length, editor.CaretOffset);
        Assert.Equal(0, editor.SelectionLength);
        Assert.True(editor.VerticalOffset > 0);
        window.Close();
    }

    // Three lines of 7 characters: offsets 0-7, 8-15, 16-23.
    [AvaloniaTheory]
    [InlineData(PhysicalKey.ArrowLeft, 10, 10)]
    [InlineData(PhysicalKey.ArrowRight, 10, 13)]
    [InlineData(PhysicalKey.ArrowUp, 10, 2)]      // from the start of the selection, one line up
    [InlineData(PhysicalKey.ArrowDown, 10, 21)]   // from the end of the selection, one line down
    public async Task AnArrow_OnASelection_StartsFromItsEdge(PhysicalKey key, int start, int expected)
    {
        var (window, _, editor, _) = await Open("abcdefg\nabcdefg\nabcdefg");
        editor.Select(start, 3);
        Pump();

        Press(window, key);

        Assert.Equal(expected, editor.CaretOffset);
        Assert.Equal(0, editor.SelectionLength);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ShiftArrow_StillExtendsTheSelection()
    {
        var (window, _, editor, _) = await Open("abcdef", caret: 0);

        Press(window, PhysicalKey.ArrowRight, RawInputModifiers.Shift);
        Press(window, PhysicalKey.ArrowRight, RawInputModifiers.Shift);

        Assert.Equal(2, editor.SelectionLength);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("(", ")")]
    [InlineData("[", "]")]
    [InlineData("{", "}")]
    [InlineData("'", "'")]
    [InlineData("\"", "\"")]
    public async Task AnOpener_SurroundsTheSelection_WhichStaysSelected_InOneUndoStep(string open, string close)
    {
        var (window, _, editor, tab) = await Open("select abc from t");
        editor.Select(7, 3);
        Pump();

        Type(window, open);

        Assert.Equal($"select {open}abc{close} from t", editor.Text);
        Assert.Equal("abc", editor.SelectedText);
        Assert.Equal(editor.Text, tab.Text);
        Assert.Equal((8, 11), tab.Selection);
        editor.Undo();
        Assert.Equal("select abc from t", editor.Text);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("(", "()")]
    [InlineData("[", "[]")]
    [InlineData("{", "{}")]
    [InlineData("'", "''")]
    [InlineData("\"", "\"\"")]
    public async Task AnOpener_IsClosed_TypingTheCloserSkipsIt_AndOneUndoRemovesThePair(string open, string pair)
    {
        var (window, _, editor, _) = await Open("select ");

        Type(window, open);
        Assert.Equal("select " + pair, editor.Text);
        Assert.Equal(8, editor.CaretOffset);
        editor.Undo();
        Assert.Equal("select ", editor.Text);

        Type(window, open);
        Type(window, pair[1..]);
        Assert.Equal("select " + pair, editor.Text);
        Assert.Equal(9, editor.CaretOffset);
        window.Close();
    }

    [AvaloniaFact]
    public async Task NestedPairs_AreSkippedAfterTypingInside()
    {
        var (window, _, editor, _) = await Open("select ");

        Type(window, "f('a')");

        Assert.Equal("select f('a')", editor.Text);
        Assert.Equal(editor.Text.Length, editor.CaretOffset);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ACloserTheUserTyped_IsNotSkipped()
    {
        var (window, _, editor, _) = await Open("()", caret: 1);

        Type(window, ")");

        Assert.Equal("())", editor.Text);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("select abc", 7, "(", "select (abc")]          // before a word
    [InlineData("select abc", 10, "'", "select abc'")]         // after a word
    [InlineData("select 'ab", 10, "\"", "select 'ab\"")]       // inside a string
    [InlineData("-- it ", 6, "'", "-- it '")]                  // inside a comment
    [InlineData("/* x ", 5, "\"", "/* x \"")]
    public async Task NoCloser_WhereItWouldBeInTheWay(string text, int caret, string typed, string expected)
    {
        var (window, _, editor, _) = await Open(text, caret);

        Type(window, typed);

        Assert.Equal(expected, editor.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Backspace_BetweenAnEmptyPair_RemovesBoth_InOneUndoStep()
    {
        var (window, _, editor, _) = await Open("select ");
        Type(window, "(");

        Press(window, PhysicalKey.Backspace);

        Assert.Equal("select ", editor.Text);
        editor.Undo();
        Assert.Equal("select ()", editor.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SettingOff_NoAutoClose_ButTheSelectionIsStillSurrounded()
    {
        SetAutoClose(false);
        var (window, _, editor, _) = await Open("select ");

        Type(window, "(");
        Assert.Equal("select (", editor.Text);

        editor.Select(0, 6);
        Pump();
        Type(window, "[");
        Assert.Equal("[select] (", editor.Text);
        window.Close();
    }

    // AZERTY: [ and { are AltGr (Ctrl+Alt) + a digit key; the character arrives as text input.
    [AvaloniaTheory]
    [InlineData(PhysicalKey.Digit5, "[", "[]")]
    [InlineData(PhysicalKey.Digit4, "{", "{}")]
    public async Task AltGrCharacters_ArePaired(PhysicalKey key, string typed, string expected)
    {
        var (window, _, editor, _) = await Open("");

        window.KeyPressQwerty(key, RawInputModifiers.Control | RawInputModifiers.Alt);
        Type(window, typed);
        window.KeyReleaseQwerty(key, RawInputModifiers.Control | RawInputModifiers.Alt);

        Assert.Equal(expected, editor.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AcceptingACompletion_InsideAnAutoClosedQuote_LeavesOneClosingQuote()
    {
        var (window, view, editor, _) = await Open("SELECT * FROM ");

        Type(window, "\"bi");
        Assert.NotNull(view.OpenCompletion);
        Press(window, PhysicalKey.Enter);

        Assert.Equal("SELECT * FROM \"big\"", editor.Text);
        Assert.Equal(editor.Text.Length, editor.CaretOffset);
        window.Close();
    }
}
