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
        new("Ctrl+Shift+K", "Command palette: run any command"),
        new("Ctrl+T", "Go to a table or object of the selected connection"),
        new("Ctrl+Shift+P", "Go to a table or object in any connection"),
        new("Ctrl+N", "New SQL script"),
        new("Ctrl+O", "Open a script file"),
        new("Ctrl+Shift+T", "Reopen the last closed tab"),
    ];

    public static readonly IReadOnlyList<ShortcutSection> Sections =
    [
        new("Navigate", [
            new("Ctrl+Shift+K", "Command palette: run any command"),
            new("Ctrl+T", "Go to a table or object of the selected connection"),
            new("Ctrl+Shift+P", "Go to a table or object in any connection"),
            new("Alt+1 … Alt+5", "In the quick open: search tables, views, procedures, functions, columns (on / off)"),
            new("Ctrl+Alt+F", "Search in scripts and in the data of a database"),
            new("Ctrl+F", "Filter the navigator (while it has the focus)"),
            new("Ctrl+B", "Hide or show the navigator"),
            new("Enter", "Connect, open the selected table, or the definition of a routine"),
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
            new("Ctrl+K, Ctrl+D", "Format the selection, or the whole script"),
            new("Ctrl+K, Ctrl+C", "Comment the selected lines"),
            new("Ctrl+K, Ctrl+U", "Uncomment the selected lines"),
        ]),
        new("Execution plan", [
            new("Mouse wheel", "Zoom around the cursor (with or without Ctrl)"),
            new("Drag", "Pan the plan (Shift+wheel pans sideways)"),
            new("Ctrl++ / Ctrl+-", "Zoom in or out"),
            new("Ctrl+0", "Fit the whole plan in the window"),
        ]),
        new("ER diagram", [
            new("Mouse wheel", "Zoom around the cursor (with or without Ctrl)"),
            new("Drag", "Pan the canvas, or move a table (Shift+wheel pans sideways)"),
            new("Ctrl++ / Ctrl+-", "Zoom in or out"),
            new("Ctrl+0", "Fit the whole diagram in the window"),
            new("Ctrl+F", "Find a table (Enter: next match)"),
            new("Enter / double-click", "Open the data of the selected table"),
        ]),
        new("Result and data grids", [
            new("Drag / Alt+click / Shift+arrows", "Select a rectangle of cells"),
            new("Ctrl+click a header / Ctrl+Space", "Select whole columns"),
            new("Ctrl+C", "Copy the cell or the selection"),
            new("Ctrl+Shift+C", "Copy with the column names"),
            new("Ctrl+Alt+C", "Advanced copy: delimiter, quotes, NULL, dates"),
            new("Ctrl+E", "Export to a file"),
        ]),
        new("Linked databases", [
            new("Ctrl+Alt+PageDown", "Next database of the group"),
            new("Ctrl+Alt+PageUp", "Previous database of the group"),
        ]),
        new("Search box", [
            new("Tab", "Switch between this connection and all connections"),
        ]),
        new("Command palette", [
            new("Enter", "Run the command, or open its choices"),
            new("Backspace", "Back from the choices (empty search box)"),
            new("Home / End", "First or last command"),
        ]),
        new("Help", [
            new("Ctrl+,", "Open the settings"),
            new("F1", "This list"),
            new("Esc", "Close this list or the search"),
        ]),
    ];
}
