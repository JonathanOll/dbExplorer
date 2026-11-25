namespace DbExplorer.App.Views;

public sealed record ShortcutItem(string Keys, string Text);

public sealed record ShortcutSection(string Title, IReadOnlyList<ShortcutItem> Items);

/// <summary>
/// The keyboard shortcuts as shown to the user: the welcome page lists <see cref="Highlights"/>, the F1 card lists
/// <see cref="Sections"/>. One list, so the two never disagree (the key handling itself is in MainShortcuts and ScriptTabKeys).
/// </summary>
public static class ShortcutCatalog
{
    public static readonly IReadOnlyList<ShortcutItem> Highlights =
    [
        new("Ctrl+T", "Go to a table of the selected connection"),
        new("Ctrl+Shift+P", "Go to a table in any connection"),
        new("Ctrl+N", "New SQL script"),
        new("Ctrl+O", "Open a script file"),
        new("Ctrl+Shift+T", "Reopen the last closed tab"),
    ];

    public static readonly IReadOnlyList<ShortcutSection> Sections =
    [
        new("Navigate", [
            new("Ctrl+T", "Go to a table of the selected connection"),
            new("Ctrl+Shift+P", "Go to a table in any connection"),
            new("Ctrl+Alt+F", "Search in scripts and in the data of a database"),
            new("Ctrl+F", "Filter the navigator (while it has the focus)"),
            new("Ctrl+B", "Hide or show the navigator"),
            new("Enter", "Connect, or open the selected table"),
            new("F2", "Edit or rename the selected node"),
            new("Delete", "Delete the selected node"),
            new("F5", "Refresh the schema (navigator focused)"),
        ]),
        new("Tabs", [
            new("Ctrl+N", "New SQL script"),
            new("Ctrl+O", "Open a script file"),
            new("Ctrl+W", "Close the tab"),
            new("Ctrl+Tab", "Next tab"),
            new("Ctrl+Shift+Tab", "Previous tab"),
            new("Ctrl+Shift+T", "Reopen the last closed tab"),
            new("Ctrl+Shift+H", "Query history: search, re-open or re-run past statements"),
        ]),
        new("Table", [
            new("F5", "Reload"),
            new("Ctrl+L", "Jump to a column"),
            new("F2", "Edit the cell"),
            new("Ctrl+0", "Set the cell to NULL"),
            new("Ctrl+S", "Save, or review the changes"),
        ]),
        new("Script", [
            new("Ctrl+Enter", "Run the statement"),
            new("F5", "Run the script"),
            new("Ctrl+Shift+Enter", "Run the statement on the whole group"),
            new("Ctrl+Shift+F5", "Run the script on the group"),
            new("Ctrl+Space", "Complete tables, columns and keywords"),
            new("Ctrl+L", "Show the estimated execution plan (nothing runs)"),
            new("Ctrl+M", "Include the actual execution plan in the runs"),
            new("Ctrl+Shift+F", "Format the selection, or the whole script"),
        ]),
        new("Linked databases", [
            new("Ctrl+Alt+PageDown", "Next database of the group"),
            new("Ctrl+Alt+PageUp", "Previous database of the group"),
        ]),
        new("Search box", [
            new("Tab", "Switch between this connection and all connections"),
        ]),
        new("Help", [
            new("Ctrl+,", "Open the settings"),
            new("F1", "This list"),
            new("Esc", "Close this list or the search"),
        ]),
    ];
}
