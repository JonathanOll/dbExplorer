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

    /// <summary>Esc: stops the run, but only while one is running (otherwise the editor keeps the key).</summary>
    CancelIfBusy,

    /// <summary>Ctrl+/: comments or uncomments the selected lines.</summary>
    ToggleComment,

    /// <summary>F6: moves the focus between the editor and the results.</summary>
    SwitchPane,

    /// <summary>Ctrl+Space: opens the completion list at the caret.</summary>
    Complete,
}

/// <summary>One line of the shortcuts flyout.</summary>
public sealed record ScriptShortcut(string Label, string Keys);

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
        (Key.Escape, KeyModifiers.None) => ScriptKeyAction.CancelIfBusy,
        (Key.OemQuestion or Key.Divide, KeyModifiers.Control) => ScriptKeyAction.ToggleComment,
        (Key.F6, KeyModifiers.None) => ScriptKeyAction.SwitchPane,
        (Key.Space, KeyModifiers.Control) => ScriptKeyAction.Complete,
        _ => ScriptKeyAction.None,
    };

    /// <summary>The chords shown by the shortcuts flyout of the toolbar.</summary>
    public static IReadOnlyList<ScriptShortcut> Shortcuts { get; } =
    [
        new("Run the selection or the statement at the caret", "Ctrl+Enter"),
        new("Run the whole script", "F5"),
        new("Run the statement on the group", "Ctrl+Shift+Enter"),
        new("Run the whole script on the group", "Ctrl+Shift+F5"),
        new("Stop the run", "Esc"),
        new("Save the script", "Ctrl+S"),
        new("Open a script file", "Ctrl+O"),
        new("Find in the script", "Ctrl+F"),
        new("Comment or uncomment the selected lines", "Ctrl+/"),
        new("Complete tables, columns and keywords (also while typing)", "Ctrl+Space"),
        new("Open result tab 1 to 9", "Ctrl+1 to 9"),
        new("Move between the editor and the results", "F6"),
        new("Next or previous database of the group", "Ctrl+Alt+PageDown / PageUp"),
    ];

    /// <summary>Ctrl+1 to Ctrl+9 select the 1st to 9th result tab: the 0-based index, or null for any other chord.</summary>
    public static int? ResultTabIndex(Key key, KeyModifiers modifiers)
    {
        if (modifiers != KeyModifiers.Control) return null;
        if (key is >= Key.D1 and <= Key.D9) return key - Key.D1;
        if (key is >= Key.NumPad1 and <= Key.NumPad9) return key - Key.NumPad1;
        return null;
    }
}
