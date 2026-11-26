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

    /// <summary>Ctrl+L: the estimated execution plan of the selection or the statement at the caret (nothing runs).</summary>
    EstimatedPlan,

    /// <summary>Ctrl+M: switches "Actual plan" on or off for the next runs.</summary>
    ToggleActualPlan,

    /// <summary>Ctrl+Shift+F: formats the selection, or the whole script when nothing is selected.</summary>
    FormatSql,

    /// <summary>Ctrl+K: the first key of a two-key chord (see <see cref="ScriptTabKeys.ResolveChord"/>).</summary>
    StartChord,

    /// <summary>Ctrl+K, Ctrl+C: comments the selected lines.</summary>
    CommentLines,

    /// <summary>Ctrl+K, Ctrl+U: uncomments the selected lines.</summary>
    UncommentLines,
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
        (Key.L, KeyModifiers.Control) => ScriptKeyAction.EstimatedPlan,
        (Key.M, KeyModifiers.Control) => ScriptKeyAction.ToggleActualPlan,
        (Key.F, KeyModifiers.Control | KeyModifiers.Shift) => ScriptKeyAction.FormatSql,
        (Key.K, KeyModifiers.Control) => ScriptKeyAction.StartChord,
        _ => ScriptKeyAction.None,
    };

    /// <summary>
    /// The second key of a Ctrl+K chord, with Ctrl still held or already released. None means the chord has no such key: the
    /// key is then dropped, never handed to the editor (whose own Ctrl+D deletes the line).
    /// </summary>
    public static ScriptKeyAction ResolveChord(Key key, KeyModifiers modifiers) =>
        modifiers is not (KeyModifiers.Control or KeyModifiers.None) ? ScriptKeyAction.None : key switch
        {
            Key.D => ScriptKeyAction.FormatSql,
            Key.C => ScriptKeyAction.CommentLines,
            Key.U => ScriptKeyAction.UncommentLines,
            _ => ScriptKeyAction.None,
        };

    /// <summary>A modifier key on its own: pressing it again between the two keys of a chord does not end the chord.</summary>
    public static bool IsModifier(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    /// <summary>The chords shown by the shortcuts flyout of the toolbar.</summary>
    public static IReadOnlyList<ScriptShortcut> Shortcuts { get; } =
    [
        new("Run the selection or the statement at the caret", "Ctrl+Enter"),
        new("Run the whole script", "F5"),
        new("Run the statement on the group", "Ctrl+Shift+Enter"),
        new("Run the whole script on the group", "Ctrl+Shift+F5"),
        new("Show the estimated execution plan (nothing runs)", "Ctrl+L"),
        new("Include the actual execution plan in the next runs", "Ctrl+M"),
        new("Stop the run", "Esc"),
        new("Save the script", "Ctrl+S"),
        new("Open a script file", "Ctrl+O"),
        new("Find in the script", "Ctrl+F"),
        new("Comment or uncomment the selected lines", "Ctrl+/"),
        new("Format the selection, or the whole script", "Ctrl+Shift+F"),
        new("Format the selection, or the whole script", "Ctrl+K, Ctrl+D"),
        new("Comment the selected lines", "Ctrl+K, Ctrl+C"),
        new("Uncomment the selected lines", "Ctrl+K, Ctrl+U"),
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
