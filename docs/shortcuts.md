# Keyboard shortcuts

Every shortcut of the app. The same list is in the app (`F1`, and Settings > Shortcuts, which has a filter box and
**Copy as markdown**).

| Shortcut | Where | Action |
| --- | --- | --- |
| `Ctrl+Shift+K` | anywhere | Command palette: every command of the app, searchable (View > Command palette...; press again to close) |
| `Up` / `Down`, `PageUp` / `PageDown`, `Home` / `End` | command palette | Move the selection |
| `Enter` / `Tab` | command palette | Run the command / open the choices of a command that asks for one |
| `Backspace` (empty search box) / `Shift+Tab` | command palette, second step | Back to the previous step |
| `Esc` | command palette | Close it |
| `Ctrl+Alt+F` | anywhere | Search in scripts (open tabs and library) and in the data of a connection |
| `Ctrl+T` | anywhere | Quick open a table, view, procedure, function or column of the active connection; words in any order, and naming another database searches the others too ([Quick open](features.md#quick-open)) |
| `Ctrl+Shift+P` | anywhere | Quick open the same across all connections (metadata cache) |
| `Alt+1` ... `Alt+5` | quick open overlay | Switch tables / views / procedures / functions / columns on or off (remembered) |
| `Shift+Tab` | quick open overlay | Move to the type chips (`Tab` / arrows between them, `Space` or `Enter` toggles) |
| `Ctrl+,` | anywhere | Open the Settings dialog (Tools > Settings...) |
| `Ctrl+Shift+T` | anywhere | Reopen the last closed tab (like a browser; press again to go further back; up to 50 are remembered; a table keeps its filters, a script its text) |
| `Ctrl+Shift+H` | anywhere | Query history: search, filter, re-open or re-run the statements you ran (View > Query history) |
| `Ctrl+L` | table tab | Fuzzy-search a column and scroll to it |
| `Ctrl+L` | script tab | Show the estimated execution plan of the selection / the statement under the cursor (nothing runs) |
| `Ctrl+M` | script tab | Include the actual execution plan in the next runs (switch) |
| `Mouse wheel` / `Ctrl+wheel` | execution plan | Zoom around the cursor |
| `Drag` / `Shift+wheel` | execution plan | Pan the plan / pan it sideways |
| `Ctrl++` / `Ctrl+-` / `Ctrl+0` | execution plan | Zoom in / zoom out / fit the whole plan |
| `Mouse wheel` / `Ctrl+wheel` | ER diagram | Zoom around the cursor |
| `Drag` / `Shift+wheel` | ER diagram | Pan the canvas (or move a table) / pan sideways; arrows pan too |
| `Ctrl++` / `Ctrl+-` / `Ctrl+0` | ER diagram | Zoom in / zoom out / fit the whole diagram |
| `Ctrl+F` | ER diagram | Find a table (`Enter` next match, `Shift+Enter` previous, `Esc` clears) |
| `Enter` / double-click | ER diagram | Open the data of the selected table |
| `F8` / `Shift+F8` | result comparison | Next / previous difference (see [Compare results](compare.md)) |
| `Ctrl+N` | anywhere | New script tab on the active (or selected) connection |
| `Ctrl+O` | anywhere | Open a script file in a script tab |
| `Enter` | navigator, a table or view / a procedure or function | Open its data / its definition |
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
| `Ctrl+0` | table grid, cell selected | Set the cell to NULL (staged); with a cell or column selection, every selected cell |
| `Ctrl+Break` | script tab | Cancel the running query, or the running group run (all databases) |
| `F2` | table grid | Edit the cell |
| `Ctrl+C` / `Ctrl+Shift+C` | result and table grids | Copy the cell or the selection / with the column names (tab separated) |
| `Ctrl+Alt+C` | result and table grids | [Advanced copy](export.md#advanced-copy) |
| `Ctrl+E` | result and table grids | [Export to a file](export.md#export-to-a-file) |
| `Shift+arrows` / `Alt+click` / drag | result and table grids | Select a rectangle of cells |
| `Ctrl+Space` / `Ctrl+click` on a header | result and table grids | Select (or unselect) whole columns |
| `Esc` | result and table grids | Drop the cell or column selection |
