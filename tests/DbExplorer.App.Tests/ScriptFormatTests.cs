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

/// <summary>Ctrl+Shift+F: the selection or the whole script is formatted, as one undoable edit, with the caret kept on its token.</summary>
public class ScriptFormatTests : IDisposable
{
    private readonly ScriptTabFixture _t = new();

    public void Dispose() => _t.Dispose();

    private async Task<ScriptTabViewModel> Tab(string text, SqlFormatOptions? options = null)
    {
        var session = await _t.Sessions.GetOrOpenAsync(_t.Config, CancellationToken.None);
        var hooks = options is null ? _t.Hooks : _t.Hooks with { FormatOptions = () => options };
        return _t.Track(new ScriptTabViewModel(_t.Config, session, hooks, 1) { PersistDelay = TimeSpan.FromMilliseconds(40), Text = text });
    }

    private async Task<(Window Window, TextEditor Editor, ScriptTabViewModel Tab)> Open(string text, SqlFormatOptions? options = null)
    {
        var tab = await Tab(text, options);
        var window = new Window { Content = new ContentControl { Content = tab }, Width = 1100, Height = 700 };
        window.Show();
        Pump();
        var view = window.GetVisualDescendants().OfType<ScriptTabView>().Single();
        return (window, view.FindControl<TextEditor>("Editor")!, tab);
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    // ------------------------------------------------------------------ view-model

    [Fact]
    public async Task NoSelection_FormatsTheWholeScript()
    {
        var tab = await Tab("select a,b from t where x=1;\nselect 2");
        tab.FormatSqlCommand.Execute(null);

        Assert.Equal("SELECT\n    a,\n    b\nFROM t\nWHERE x = 1;\n\nSELECT 2", tab.Text);
    }

    [Fact]
    public async Task Selection_FormatsOnlyTheSelection()
    {
        const string text = "select 1  from   a;\nselect b,c from   t;\nselect 3  from   z";
        var tab = await Tab(text);
        var start = text.IndexOf("select b", StringComparison.Ordinal);
        tab.Selection = (start, start + "select b,c from   t;".Length);

        tab.FormatSqlCommand.Execute(null);

        Assert.Equal("select 1  from   a;\nSELECT\n    b,\n    c\nFROM t;\nselect 3  from   z", tab.Text);
    }

    [Fact]
    public async Task Caret_StaysOnTheSameToken()
    {
        const string text = "select a,b from tbl where x=1";
        var tab = await Tab(text);
        tab.CaretOffset = text.IndexOf("tbl", StringComparison.Ordinal) + 1;   // between t and b

        tab.FormatSqlCommand.Execute(null);

        Assert.Equal("tbl".Substring(0, 1), tab.Text.Substring(tab.CaretOffset - 1, 1));
        Assert.Equal('b', tab.Text[tab.CaretOffset]);
    }

    [Fact]
    public async Task AlreadyFormatted_ChangesNothing_AndSaysSo()
    {
        const string text = "SELECT *\nFROM t";
        var tab = await Tab(text);
        tab.FormatSqlCommand.Execute(null);

        Assert.Equal(text, tab.Text);
        Assert.Null(tab.PrepareFormat());
        Assert.Contains("Already formatted", _t.Status.Message);
    }

    [Fact]
    public async Task TheOptionsOfTheConfig_AreUsed()
    {
        var tab = await Tab("SELECT a, b FROM t", new SqlFormatOptions { KeywordCase = KeywordCase.Lower, CommaPosition = CommaPosition.Leading, IndentSize = 2 });
        tab.FormatSqlCommand.Execute(null);

        Assert.Equal("select\n  a\n  , b\nfrom t", tab.Text);
    }

    [Fact]
    public async Task PrepareFormat_ReplacesOnlyTheChangedPart()
    {
        var tab = await Tab("SELECT *\nFROM t\nWHERE a=1");
        var edit = tab.PrepareFormat()!;

        Assert.Equal("SELECT *\nFROM t\nWHERE a=1".IndexOf("=1", StringComparison.Ordinal), edit.Start);
        Assert.Equal(1, edit.Length);
        Assert.Equal(" = ", edit.Text);
    }

    [Fact]
    public void Shortcut_IsCtrlShiftF_AndDoesNotClash()
    {
        Assert.Equal(ScriptKeyAction.FormatSql, ScriptTabKeys.Resolve(Key.F, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal(ScriptKeyAction.None, ScriptTabKeys.Resolve(Key.F, KeyModifiers.Control));
        Assert.Contains(ScriptTabKeys.Shortcuts, s => s.Keys == "Ctrl+Shift+F");
        Assert.Contains(ShortcutCatalog.Sections.SelectMany(g => g.Items), s => s.Keys == "Ctrl+Shift+F");
        Assert.Single(ShortcutCatalog.Sections.SelectMany(g => g.Items), s => s.Keys == "Ctrl+Shift+F");
    }

    // ------------------------------------------------------------------ editor

    [AvaloniaFact]
    public async Task CtrlShiftF_FormatsTheEditor_AndOneUndoRestoresIt()
    {
        const string original = "select a,b from t where x=1";
        var (window, editor, tab) = await Open(original);
        editor.TextArea.Focus();
        Pump();

        window.KeyPress(Key.F, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F, "F");
        Pump();

        Assert.Equal("SELECT\n    a,\n    b\nFROM t\nWHERE x = 1", editor.Text);
        Assert.Equal(editor.Text, tab.Text);

        editor.Undo();
        Pump();
        Assert.Equal(original, editor.Text);
        Assert.Equal(original, tab.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FormatInTheEditor_OnlyTouchesTheSelection_AndKeepsItSelected()
    {
        const string text = "select 1  from   a;\nselect b,c from   t;";
        var (window, editor, tab) = await Open(text);
        var start = text.IndexOf("select b", StringComparison.Ordinal);
        editor.Select(start, text.Length - start);
        Pump();

        tab.FormatSqlCommand.Execute(null);
        Pump();

        Assert.Equal("select 1  from   a;\nSELECT\n    b,\n    c\nFROM t;", editor.Text);
        Assert.Equal(start, editor.SelectionStart);
        Assert.Equal(editor.Text.Length - start, editor.SelectionLength);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FormatInTheEditor_KeepsTheCaretOnItsToken()
    {
        const string text = "select a,b from tbl where x=1";
        var (window, editor, tab) = await Open(text);
        editor.CaretOffset = text.IndexOf("where", StringComparison.Ordinal) + 2;   // wh|ere
        Pump();

        tab.FormatSqlCommand.Execute(null);
        Pump();

        Assert.Equal("wh", editor.Text.Substring(editor.CaretOffset - 2, 2), ignoreCase: true);
        Assert.Equal("ere", editor.Text.Substring(editor.CaretOffset, 3), ignoreCase: true);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheToolbarButton_AndTheMenuCommand_AreThere()
    {
        var (window, _, tab) = await Open("select 1 from t");
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Format SQL");
        Assert.NotNull(ToolTip.GetTip(button));

        Assert.True(button.Command!.CanExecute(null));
        button.Command.Execute(null);
        Pump();
        Assert.Equal("SELECT 1\nFROM t", tab.Text);
        window.Close();
    }
}
