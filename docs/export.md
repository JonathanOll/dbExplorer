# Copy and export

Works the same in the result grids of a script and in the data grid of a table.

## Selecting

| What | How |
| --- | --- |
| Rows | click, `Ctrl+click`, `Shift+click` |
| A rectangle of cells | drag across cells, `Alt+click` the opposite corner, or `Shift+arrows` |
| Whole columns | `Ctrl+click` a column header, `Ctrl+Space` on a cell, or **Select column** in the cell menu |

A rectangle or column selection replaces the row selection, and `Esc` drops it. A column selection covers every loaded row.

In a table grid, `Delete` (and **Delete row**) marks every row the selection touches, and `Ctrl+0` sets the selected cells
to NULL. Both are only staged until you review and save.

## Copy

- **Copy** (`Ctrl+C`): one cell gives its value; a selection gives tab separated text, which a spreadsheet pastes as cells.
- **Copy with headers** (`Ctrl+Shift+C`): the same with the column names first.
- **Copy as**: CSV, TSV, JSON, Markdown table, SQL `INSERT`, SQL `UPDATE`, `IN (...)` list, column names.

SQL is written for the engine of the connection (identifiers quoted, literals escaped). For a script result the table name is
`table_name`: replace it. `UPDATE` needs a key, so it is offered on tables that have one (primary or virtual key); the key
columns are added even when they are not selected. The `IN` list takes one column (the clicked or current one when whole
rows are selected) and leaves NULL out, since `IN` never matches it.

## Advanced copy

`Ctrl+Alt+C`: column delimiter, row separator, quoting, column names, row numbers, text for NULL, date format and the
formula protection, with a preview. It takes the selection, or every loaded row when nothing is selected. The choices are
remembered.

## Export to a file

`Ctrl+E`, or **Export…** in the cell menu or in the result line. Formats: CSV, TSV, JSON, Excel (XLSX), SQL `INSERT`,
Markdown.

| Rows | What is written |
| --- | --- |
| Selection | the selected cells |
| Loaded rows | what the grid holds (a table page, or a result up to the row limit) |
| All rows | the query runs again and is streamed to the file, with no row limit |

**All rows** of a table uses the applied filters, `WHERE` and sort; pending changes are not included. For a script result it
is offered only when the statement passes the read-only guard (a statement that may write is never run again). It runs on a
new connection in a read-only context, so temporary tables, variables and uncommitted changes of the tab are not visible to
it. Group run results and the Statistics / Messages grids have no query to run again.

The file is written in the background with constant memory. **Cancel** stops it. Rows go to a temporary `.partial` file that
replaces the target only when complete: a cancelled or failed export leaves no partial file and keeps an existing file.

### Formats

- Text encoding: by default UTF-8, with a byte order mark for CSV and TSV (so a spreadsheet reads accents). UTF-8, UTF-8
  with BOM, UTF-16 and Windows-1252 can be chosen.
- Dates are ISO 8601 unless a date format is given (a .NET format such as `dd/MM/yyyy HH:mm`). SQLite returns dates as text,
  which is written unchanged.
- Binary values are written as `0x...` hexadecimal (a binary literal in SQL).
- XLSX: texts are never formulas. Numbers with more than 15 digits are written as text to keep every digit. A cell holds at
  most 32,767 characters (longer ones are cut, and the status bar says so). A new sheet starts after 1,048,576 rows.

## Spreadsheet formulas (CSV and TSV)

A spreadsheet runs a text that starts with `=`, `+`, `-` or `@` as a formula. Data coming from a database can use this to
leak information or to start a program when the file is opened. **Protect against spreadsheet formulas** puts an apostrophe
before such texts (and before column names). It is off by default because it changes the data. Turn it on for files that
will be opened in a spreadsheet and hold text you do not control. Numbers are not touched. XLSX does not need it.
