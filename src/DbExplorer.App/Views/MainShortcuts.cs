using Avalonia.Input;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// The window-level keyboard shortcuts, as a pure key-to-command mapping so it can be tested without a window. Modifiers
/// are compared for equality, so Ctrl+Shift+T does not also fire Ctrl+T (and Ctrl+Alt+PageDown is not Ctrl+PageDown). Every
/// shortcut is a Ctrl chord or F5, so typing in a text box or the editor is never affected.
/// </summary>
public static class MainShortcuts
{
    /// <summary>
    /// Runs the command bound to the key and returns true (the key is then handled), or returns false when the key has no
    /// meaning here. F5 only belongs to the window in a table tab (refresh): in a script tab it is the editor's "run
    /// script" and must reach it. Ctrl+W, Ctrl+Tab and F5 do nothing while the quick-open overlay is showing.
    /// </summary>
    /// <param name="commitOpenEdit">Ends a cell edit in progress (so its text is staged, and prompted about, before the
    /// chord leaves the tab); false when the text was refused. Called only for the chords that leave the tab or reload it
    /// (Ctrl+W, Ctrl+Tab, F5); a refused commit swallows the chord and does nothing.</param>
    public static bool Handle(MainViewModel main, Key key, KeyModifiers modifiers, Func<bool>? commitOpenEdit = null)
    {
        var overlay = main.QuickOpen is not null;
        bool Ready() => commitOpenEdit?.Invoke() ?? true;
        switch (key, modifiers)
        {
            case (Key.B, KeyModifiers.Control):
                main.ToggleNavigatorCommand.Execute(null);
                return true;
            case (Key.F1, KeyModifiers.None):
                main.ShowShortcutsCommand.Execute(null);
                return true;
            case (Key.Escape, KeyModifiers.None) when main.IsShortcutsVisible:
                main.HideShortcutsCommand.Execute(null);
                return true;
            case (Key.T, KeyModifiers.Control):
                main.ShowTablePickerCommand.Execute(null);
                return true;
            case (Key.T, KeyModifiers.Control | KeyModifiers.Shift):
                main.ReopenClosedTabCommand.Execute(null);
                return true;
            case (Key.P, KeyModifiers.Control | KeyModifiers.Shift):
                main.ShowAllTablesPickerCommand.Execute(null);
                return true;
            case (Key.H, KeyModifiers.Control | KeyModifiers.Shift):
                main.OpenHistoryCommand.Execute(null);
                return true;
            case (Key.N, KeyModifiers.Control):
                main.NewScriptTabCommand.Execute(null);
                return true;
            case (Key.O, KeyModifiers.Control):
                main.OpenScriptFileCommand.Execute(null);
                return true;
            // In a script tab Ctrl+L is "estimated plan": it belongs to the editor, which sees the key first.
            case (Key.L, KeyModifiers.Control) when main.ActiveTab is not ScriptTabViewModel:
                main.JumpToColumnCommand.Execute(null);
                return true;
            case (Key.W, KeyModifiers.Control) when !overlay && main.ActiveTab is not null:
                if (Ready()) main.CloseActiveTabCommand.Execute(null);
                return true;
            case (Key.Tab, KeyModifiers.Control) when !overlay && main.Tabs.Count > 0:
                if (Ready()) main.NextTabCommand.Execute(null);
                return true;
            case (Key.Tab, KeyModifiers.Control | KeyModifiers.Shift) when !overlay && main.Tabs.Count > 0:
                if (Ready()) main.PreviousTabCommand.Execute(null);
                return true;
            // Next / previous database of the tab's group. Ctrl+Alt+PageDown/PageUp: PageDown is no character key (so AltGr
            // typing is unaffected), and the plain / Ctrl / Shift variants stay with the grid and the editor.
            case (Key.PageDown, KeyModifiers.Control | KeyModifiers.Alt) when !overlay && main.ActiveTab is not null:
                if (Ready()) main.NextGroupMemberCommand.Execute(null);
                return true;
            case (Key.PageUp, KeyModifiers.Control | KeyModifiers.Alt) when !overlay && main.ActiveTab is not null:
                if (Ready()) main.PreviousGroupMemberCommand.Execute(null);
                return true;
            case (Key.F5, KeyModifiers.None) when !overlay && main.ActiveTab is TableTabViewModel:
                if (Ready()) main.RefreshActiveTableCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }
}
