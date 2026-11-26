# Keyboard shortcuts

Every shortcut of the app. The same list is in the app (`F1`, and Settings > Shortcuts, which has a filter box and
**Copy as markdown**).

| Shortcut | Where | Action |
| --- | --- | --- |
| `Ctrl+Alt+F` | anywhere | Search in scripts (open tabs and library) and in the data of a connection |
| `Ctrl+T` | anywhere | Quick open a table/view of the active connection |
| `Ctrl+Shift+P` | anywhere | Quick open a table/view across all connections (metadata cache) |
| `Ctrl+,` | anywhere | Open the Settings dialog (Tools > Settings...) |
| `Ctrl+Shift+T` | anywhere | Reopen the last closed tab (like a browser; press again to go further back; up to 50 are remembered; a table keeps its filters, a script its text) |
| `Ctrl+Shift+H` | anywhere | Query history: search, filter, re-open or re-run the statements you ran (View > Query history) |
| `Ctrl+L` | table tab | Fuzzy-search a column and scroll to it |
| `Ctrl+L` | script tab | Show the estimated execution plan of the selection / the statement under the cursor (nothing runs) |
| `Ctrl+M` | script tab | Include the actual execution plan in the next runs (switch) |
| `Mouse wheel` / `Ctrl+wheel` | execution plan | Zoom around the cursor |
| `Drag` / `Shift+wheel` | execution plan | Pan the plan / pan it sideways |
| `Ctrl++` / `Ctrl+-` / `Ctrl+0` | execution plan | Zoom in / zoom out / fit the whole plan |
| `Ctrl+N` | anywhere | New script tab on the active (or selected) connection |
| `Ctrl+O` | anywhere | Open a script file in a script tab |
| `Enter` / `F2` / `Delete` | navigator, a script or script folder | Open / rename / delete it (see [Script library](scripts.md#script-library)) |
| `Ctrl+Enter` | script tab | Run the statement under the cursor (ends at `;`, a `GO` line or a blank line) or the selection |
| `F5` | script tab | Run the whole script; in a table tab: reload the data |
| `Ctrl+Shift+F` | script tab | Format the selection, or the whole script when nothing is selected (see [SQL formatting](scripts.md#sql-formatting)) |
| `Ctrl+K, Ctrl+D` | script tab | Same as `Ctrl+Shift+F` |
| `Ctrl+K, Ctrl+C` / `Ctrl+K, Ctrl+U` | script tab | Comment / uncomment the selected lines (after `Ctrl+K`, a key that completes no chord is ignored) |
| `Ctrl+Shift+Enter` | script tab on a connection in a group | Run the statement under the cursor / the selection on **every database of the group** (read-only, see [Linked database groups](groups.md)); in a script tab that is not in a group the status bar only says so (no ordinary run) |
| `Ctrl+Shift+F5` | script tab on a connection in a group | Run the whole script on every database of the group (read-only) |
| `Ctrl+S` | script tab / table tab | Save the script (the first time: name and library folder) / review the SQL and apply staged edits |
| `Ctrl+W` | anywhere | Close the active tab |
| `Ctrl+Tab` | anywhere | Next tab |
| `Ctrl+Shift+Tab` | anywhere | Previous tab |
| `Ctrl+Alt+PageDown` | table / script tab on a connection in a group | Switch the tab to the next database of the group (wraps around) |
| `Ctrl+Alt+PageUp` | table / script tab on a connection in a group | Switch the tab to the previous database of the group (wraps around; with several groups, the group picked in the selector) |
| `Esc` | quick open overlay | Close the overlay |
| `Esc` | table tab | Discard staged edits: specified in the design, **not wired yet** (use the Revert button; in a cell editor Esc cancels that edit) |
| `Ctrl+0` | table grid, cell selected | Set the cell to NULL (staged) |
| `Ctrl+Break` | script tab | Cancel the running query, or the running group run (all databases) |
| `F2` | table grid | Edit the cell |
