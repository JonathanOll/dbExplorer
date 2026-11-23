using Avalonia.Input;

namespace DbExplorer.App.Views;

/// <summary>What a key chord does in the script editor.</summary>
public enum ScriptKeyAction
{
    None,
    RunStatement,
    RunScript,
    RunStatementOnGroup,
    RunScriptOnGroup,
    Save,
    Cancel,
}

/// <summary>
/// The key chords of the script tab as a pure mapping, so they can be tested without a window. Modifiers are compared for
/// equality: Ctrl+Shift+Enter (run on group) is not Ctrl+Enter (run here), Ctrl+Shift+F5 is not F5. The window-level shortcuts
/// (<see cref="MainShortcuts"/>) never use Enter or F5 with Shift, so these chords reach the editor.
/// </summary>
public static class ScriptTabKeys
{
    public static ScriptKeyAction Resolve(Key key, KeyModifiers modifiers) => (key, modifiers) switch
    {
        (Key.Enter, KeyModifiers.Control) => ScriptKeyAction.RunStatement,
        (Key.Enter, KeyModifiers.Control | KeyModifiers.Shift) => ScriptKeyAction.RunStatementOnGroup,
        (Key.F5, KeyModifiers.None) => ScriptKeyAction.RunScript,
        (Key.F5, KeyModifiers.Control | KeyModifiers.Shift) => ScriptKeyAction.RunScriptOnGroup,
        (Key.S, KeyModifiers.Control) => ScriptKeyAction.Save,
        (Key.Cancel or Key.Pause, KeyModifiers.Control) => ScriptKeyAction.Cancel,
        _ => ScriptKeyAction.None,
    };
}
