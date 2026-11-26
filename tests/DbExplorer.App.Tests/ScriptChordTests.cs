using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App.Tests;

/// <summary>
/// The two-key chords of the script editor (Ctrl+K, then Ctrl+D / Ctrl+C / Ctrl+U), sent as real key events. The second key
/// must never reach AvaloniaEdit, whose own Ctrl+D deletes the line.
/// </summary>
public class ScriptChordTests : IDisposable
{
    private const string Script = "select a,b from t where x=1;\nselect 2;\nselect 3";
    private const string Formatted = "SELECT\n    a,\n    b\nFROM t\nWHERE x = 1;\n\nSELECT 2;\nSELECT 3";

    private readonly ScriptTabFixture _t = new();

    public void Dispose() => _t.Dispose();

    private async Task<(Window Window, TextEditor Editor, ScriptTabViewModel Tab)> Open(string text)
    {
        var session = await _t.Sessions.GetOrOpenAsync(_t.Config, CancellationToken.None);
        var tab = _t.Track(new ScriptTabViewModel(_t.Config, session, _t.Hooks, 1) { PersistDelay = TimeSpan.FromMilliseconds(40), Text = text });
        var window = new Window { Content = new ContentControl { Content = tab }, Width = 1100, Height = 700 };
        window.Show();
        Pump();
        var editor = window.GetVisualDescendants().OfType<ScriptTabView>().Single().FindControl<TextEditor>("Editor")!;
        editor.TextArea.Focus();
        editor.CaretOffset = 0;
        Pump();
        return (window, editor, tab);
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.Control)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        Pump();
    }

    [AvaloniaFact]
    public async Task CtrlK_CtrlD_FormatsTheDocument_AndOneUndoRestoresIt()
    {
        var (window, editor, tab) = await Open(Script);

        Press(window, PhysicalKey.K);
        Press(window, PhysicalKey.D);

        Assert.Equal(Formatted, editor.Text);
        Assert.Equal(editor.Text, tab.Text);
        editor.Undo();
        Assert.Equal(Script, editor.Text);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(PhysicalKey.ControlLeft)]
    [InlineData(PhysicalKey.ControlRight)]
    public async Task CtrlReleasedAndPressedBetweenTheKeys_KeepsTheChord(PhysicalKey control)
    {
        var (window, editor, _) = await Open(Script);

        window.KeyPressQwerty(control, RawInputModifiers.Control);
        Press(window, PhysicalKey.K);
        window.KeyReleaseQwerty(control, RawInputModifiers.None);
        window.KeyPressQwerty(control, RawInputModifiers.Control);
        Press(window, PhysicalKey.D);

        Assert.Equal(Formatted, editor.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SecondKeyWithoutCtrl_CompletesTheChord()
    {
        var (window, editor, _) = await Open(Script);

        Press(window, PhysicalKey.K);
        Press(window, PhysicalKey.D, RawInputModifiers.None);

        Assert.Equal(Formatted, editor.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CtrlK_CtrlC_Comments_AndCtrlK_CtrlU_Uncomments()
    {
        const string text = "select 1\n-- note\nselect 2";
        var (window, editor, tab) = await Open(text);
        editor.SelectAll();

        Press(window, PhysicalKey.K);
        Press(window, PhysicalKey.C);
        Assert.Equal("-- select 1\n-- -- note\n-- select 2", editor.Text);   // comments even a commented line: never a toggle
        Assert.Equal(editor.Text, tab.Text);

        editor.SelectAll();
        Press(window, PhysicalKey.K);
        Press(window, PhysicalKey.U);
        Assert.Equal(text, editor.Text);

        Press(window, PhysicalKey.K);
        Press(window, PhysicalKey.U);
        Assert.Equal("select 1\nnote\nselect 2", editor.Text);

        editor.SelectAll();
        Press(window, PhysicalKey.K);
        Press(window, PhysicalKey.U);   // nothing commented: never a toggle
        Assert.Equal("select 1\nnote\nselect 2", editor.Text);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(PhysicalKey.L)]
    [InlineData(PhysicalKey.X)]
    [InlineData(PhysicalKey.Delete)]
    [InlineData(PhysicalKey.Backspace)]
    [InlineData(PhysicalKey.Enter)]
    public async Task AnUnknownSecondKey_IsSwallowed_AndEndsTheChord(PhysicalKey second)
    {
        var (window, editor, tab) = await Open(Script);
        editor.CaretOffset = 3;

        Press(window, PhysicalKey.K);
        Press(window, second);

        Assert.Equal(Script, editor.Text);
        Assert.False(tab.IsBusy);

        Press(window, PhysicalKey.Slash);   // the next chord is an ordinary one again
        Assert.StartsWith("-- select a", editor.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Escape_CancelsTheChord()
    {
        var (window, editor, _) = await Open(Script);

        Press(window, PhysicalKey.K);
        Press(window, PhysicalKey.Escape, RawInputModifiers.None);
        Press(window, PhysicalKey.Slash);

        Assert.StartsWith("-- select a", editor.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task LeavingTheEditor_CancelsTheChord()
    {
        var (window, editor, _) = await Open(Script);

        Press(window, PhysicalKey.K);
        Press(window, PhysicalKey.F6, RawInputModifiers.None);   // swallowed: ends the chord
        Press(window, PhysicalKey.K);
        window.GetVisualDescendants().OfType<Button>().First(b => b.Focusable && b.IsEffectivelyEnabled && b.IsEffectivelyVisible).Focus();
        Pump();
        editor.TextArea.Focus();
        Pump();
        Press(window, PhysicalKey.Slash);

        Assert.StartsWith("-- select a", editor.Text);
        window.Close();
    }

    [Theory]
    [InlineData("Ctrl+K, Ctrl+D")]
    [InlineData("Ctrl+K, Ctrl+C")]
    [InlineData("Ctrl+K, Ctrl+U")]
    public void TheChords_AreInTheHelp(string keys)
    {
        Assert.Contains(ScriptTabKeys.Shortcuts, s => s.Keys == keys);
        Assert.Single(ShortcutCatalog.Sections.SelectMany(g => g.Items), s => s.Keys == keys);
    }
}
