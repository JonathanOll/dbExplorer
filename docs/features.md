# Features

What dbExplorer does day to day: browsing, filtering and editing data, jumping around, running SQL, and coming back
where you left off. The big topics have their own page: [linked database groups](groups.md), [scripts](scripts.md),
[execution plans and statistics](plans.md), [database upgrade](database-upgrade.md), [DBeaver import](dbeaver-import.md).

![Table tab, light](ui/after/main-table-light.png)

## Connection tree

Folders (one level) > connection > schema > table/view (SQLite has no schema level). Several
connections stay connected at once, one session each. Drag a connection into a folder; add, rename, edit, delete.
A **Groups** section at the top lists the linked database groups (see [Linked database groups](groups.md)).

Import from DBeaver: folders, connections, credentials and virtual keys, see [DBeaver import](dbeaver-import.md).

## Table tabs

Table tabs are labelled `connection · table` with a colour chip per connection, each with two sub-tabs:

- **Data**: pages of 200 rows (OFFSET paging), total row count only on demand (Count button), column sort,
  per-column filter row (`contains` by default, `=`, `!=`, `<`, `>`, `null`, `!null`, `in (a,b)`, combined with AND),
  "filter by this value" from the cell context menu, and a raw `WHERE` box. Filters run server-side with parameters.
- **Structure**: columns, types, nullability, primary key, foreign keys, indexes.

**Follow foreign keys**: `Ctrl+click` (or double-click) an FK value to open the referenced row in a new tab.

## Quick open

![Quick open, dark](ui/after/quick-open-dark.png)

Fuzzy-search tables/views of the active connection (`Ctrl+T`) or of all connections (`Ctrl+Shift+P`,
served from an on-disk metadata cache, never connects by itself); `Ctrl+L` jumps to a column in the current table.
The search forgives typos (a wrong, extra or missing letter, two letters swapped; from 4 letters on), exact matches
rank first, and `Ctrl+T` reopens with the text you typed last time, selected (type to replace it, Enter to reuse it).

## Row editing

Edits are staged in the grid (highlighted), `Ctrl+S` shows the generated SQL, then applies it in one
transaction. Insert and delete rows. Tables without a primary key are read-only until you define "virtual key"
columns for them (persisted, like DBeaver's virtual PK). Edit is disabled on connections flagged read-only.

## Script runner

A SQL editor tab per connection (AvaloniaEdit, syntax highlighting). `F5` runs the whole script,
split on `;` (respecting quotes, comments and `$$` bodies) and on `GO` lines for SQL Server. `Ctrl+Enter` with nothing
selected runs the statement under the cursor, which also ends at a **blank line** (outside strings, comments and `$$`
bodies; T-SQL needs no `;`), so a SELECT followed by a blank line and an UPDATE never run together; a cursor on a blank
line runs the statement above it. A selection runs as selected (split by `;`/`GO` only). One result grid per result
set, rows affected and duration, cancel button, and a "Rows" limit box (default 1000, up to 100 000): a result cut at
the limit says so, raise the limit and run again. Honours the connection read-only flag. Scripts are plain files;
unsaved editor contents are restored at the next start: from the saved tab list, plus any autosave copy written after it
(e.g. after a crash); older copies the list does not mention stay in `scripts\` and the status bar says how many.

More on scripts (library, history, formatting, search): [Scripts](scripts.md). Plans and statistics: [Execution plans and statistics](plans.md).

## One connection per script tab

The tab opens its own connection on its first run and keeps it, so `BEGIN TRAN`,
temp tables (`#t`), variables and `SET` options survive from one run to the next, and `ROLLBACK`/`COMMIT` in a later run
works. It is released when the tab closes (a transaction still open is rolled back), on Disconnect, when the connection
is edited, or when it is found broken; the next run then reconnects and the status bar says the session state was reset.

## Tabs survive closing the app

The open tabs come back at the next start, in the same order and with the same tab
active: tables (with the connection they are on now, so a tab switched to another group member returns on that member,
plus column filters, WHERE and sort) and scripts (connection, file, and any unsaved text from the autosave). Only the
active table tab loads at start (it connects and reads its first page in the background, after the window is shown);
every other restored table tab loads, connecting if needed, the first time you select it, and scripts connect only when
they run. Nothing connects before the first paint. A tab whose connection was deleted is skipped; one that cannot
connect shows the usual error with Reload. Tabs you closed yourself are not restored (and neither is a script you chose
Discard for when closing the window); the Ctrl+Shift+T history of closed tabs is not kept across runs. The list is saved
about a second after every change and when the window closes, in `session.json`. To start with no tabs, delete that file:
the app then restores only the unsaved scripts, as before. A script whose autosave copy or file cannot be read is not
opened as an empty dirty tab: its copy stays on disk, or it opens empty and unlinked from the file, with a message.

## Groups, upgrade, safety and theme

- **Linked database groups**: link databases that share one schema (dev, preprod, customer copies) into a named group, switch a tab between them with one key press, run the same read-only query on all of them at once and see where the results differ, or compare their schemas. Details, shortcuts and limits: [Linked database groups](groups.md).
- **Database upgrade**: play the dbupdate scripts of one sprint of the repository on the current database or on a whole group, with a preview, warnings (index creation, long-running statements) and a confirmation. Gear menu: **Settings...** and **Upgrade database...**. Details: [Database upgrade](database-upgrade.md).
- **Safety**: nothing is written without an explicit save; passwords are stored encrypted with Windows DPAPI (current user); SQL Server connections expose `encrypt` and `trustServerCertificate`.
- Light/dark theme follows Windows (or the choice made in [Settings](settings.md)).
