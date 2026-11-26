# Database upgrade

Plays the SQL scripts of one sprint of the `dbupdate` folder of your database repository on the current database
or on every database of one of its groups, after a preview with warnings and an explicit confirmation. It is a **separate,
explicit write path**: [Run on group](groups.md) is unchanged and stays read-only. SQL Server only.

**Setting.** Tools > **Settings...** (`Ctrl+,`), page **Database upgrade** (also in the gear menu): the path of the dbupdate folder (Browse or type it; a valid
folder shows how many version folders it holds). Stored in `config.json` as `DbUpdatePath`; empty (the default, and what an
older file means) switches the feature off. The folder is validated after Browse, on Enter, when the box loses focus and on Close (it is kept at once when valid; Esc leaves without keeping a half-typed one);
the gear menu's **Upgrade database...** is only enabled when the saved path still validates (checked each time the
menu opens). The path must exist and contain at least one `v...` folder. Nothing is read at launch and nothing is scanned
until you open **Upgrade database...** (that scan is a directory listing; scripts are read when you press Load).

**Folder layout understood.** Only the top-level `v<NNNN>...` folders are looked at (`v0860-sprint 143`, `V0121-change x`,
`v0010-hotfix-...`); the other folders and files at the root (`_functions`, `_views`, legacy scripts...) are out of scope.
The sprint number is read from `sprint 143` / `sprint-143` in the folder name, and one sprint can span several folders
(`v0860`, `v0861`, `v0862`): they are played together. A folder without a sprint number (a hotfix) is not reachable by
sprint number; pick it by folder in the dialog. In a version folder only the top-level `*.sql` files are read; anything else
(sub-folders, `.txt`, `.sql.bak`) is not played and is listed in the preview as ignored. Order: folder version number
ascending, then folder name, then file name in natural order (`adj - 003` before `adj - 037`). A script whose name ends with
`- Production1`, `- Production2`... targets specific servers: it carries that label and starts **unticked**.

**Upgrade database... dialog** (needs a connection selected in the tree or an open tab: that is the current database).

1. **Sprint**: type the number and press Load (Enter works), or pick a version folder in the **or folder** list (newest
   first); the two exclude each other. An unknown sprint says so and names the nearest ones. **Run on** lists the current
   database and, when it belongs to groups of two or more databases, each of those groups.
2. **Preview**: one line per script (folder, file name, Production label, one badge per warning with its reason and line in
   the tooltip), a check box on each (a script that cannot be played cannot be ticked), a summary of the warnings of the
   ticked scripts (`3 warnings: 2 index, 1 long-running`) and the list of ignored files. **Search by file name** (any part,
   case ignored; `Esc` clears) narrows the list shown only: ticks and what runs do not change. **Untick long (n)** /
   **Tick long (n)** unticks every script with a long-running badge when one of them is ticked, ticks them all otherwise
   (Production ones and those hidden by the search included; index-only warnings do not count). Press **Run...**.
3. **Confirmation**: the number of scripts, the databases, every warning of the ticked scripts, and a note when Production
   scripts are ticked. For a group you must type the sprint number (the folder code, such as `v0860`, for a run by folder)
   before **Run** is enabled; **Back** returns to the preview with the ticks kept.
4. **Run**: the databases run 4 at a time (not configurable). Each has a line: waiting, `running: script 3 of 12 - name`,
   `done - 12 scripts - 4.2 s`, `failed at <script>: batch k of n: <error>` or cancelled, with a Cancel button on each line
   and **Cancel all**. The window cannot be closed while a run is in progress. When it ends, **Copy log** puts a text log
   (every database, one `OK` / `FAILED` / `CANCELLED` / `SKIPPED` line per script, the errors) on the clipboard. The sprint is not
   remembered.

**What blocks a run.** The check happens at Run..., again before any connection is opened, and the runner repeats it, so a
refusal contacts no database and nothing is run:

- A **read-only connection** among the targets: it is named in the message (untick Read-only in its menu first). The flag is
  read again from the live configuration when each database is opened.
- A target that is not **SQL Server** (the scripts are T-SQL), or databases of mixed engines.
- A script that cannot be played as written, shown on its line in the preview: it cannot be read; it has a **`GO n`**
  repeat count (anything but `GO` or `GO 1`) or another line that starts with `GO` and is not a separator (`GO;`,
  `GO SELECT 1`); or it contains a **`USE <db>`** statement, which would move the rest of the script to a database nobody
  confirmed (also checked inside `EXEC('...')` and `sp_executesql`). While one such script is listed, nothing can be run, even
  if you untick it. Fix the script, press Load again.
- No script ticked.

A database that is in the target list twice is upgraded once, and the log says so.

**Warnings.** Each script is scanned for statements that are likely to be slow or to take locks. A warning is information
only and never blocks. It is a heuristic over the SQL tokens (comments, strings and `[bracketed]` names are ignored; the
string literals of `EXEC('...')` and `sp_executesql N'...'` are scanned with the same rules; SQL assembled from variables
is not seen):

| Badge | Raised by |
| --- | --- |
| Index | `CREATE [UNIQUE] [CLUSTERED \| NONCLUSTERED \| COLUMNSTORE ...] INDEX`; `ALTER INDEX ... REBUILD`; `ALTER TABLE ... ADD PRIMARY KEY` or `UNIQUE` (the constraint builds an index); `CREATE`, `ALTER` or `DROP FULLTEXT INDEX` |
| Long-running | `UPDATE` or `DELETE` with no `WHERE` and no `TOP` (not when it reads the trigger tables `inserted` / `deleted`, has a `TOP` in a derived table of its `FROM`, joins a `#temp` table or `@table` variable, or sits in a `CREATE TRIGGER` body, which runs later and not during the upgrade); `WHILE`; `WAITFOR`; `ALTER TABLE ... ALTER COLUMN`; `INSERT ... SELECT` and `SELECT ... INTO` whose `SELECT` has no `WHERE` and no `TOP` (not into a `#temp` table or an `@table` variable); a script of **100 000 characters or more** (reported as a large script, with its size, on line 1) |

Row counts are not looked at: a `CREATE INDEX` on a small table warns like one on a huge table. A script without any warning
can still be slow.

**What a run does.** For each database, the ticked scripts one after another in the order above, each split into batches on
`GO` lines only (a `;` never splits a batch, so `IF ... BEGIN ... CREATE INDEX ...; END` stays whole); the script text is sent
as it is. A fresh connection is used for every database and closed at the end; the script tab connections are not touched.

- **No transaction wrapper.** Whatever a script commits stays committed, even if a later script fails.
- **No command timeout**: an index build runs until it ends or you cancel.
- **The first error stops that database**: the failing script is reported (batch k of n, the server message), the remaining
  scripts of that database are marked skipped, and the other databases carry on. An unreachable database is a failure of
  that database only.
- **Cancel** (one database or all) interrupts the running statement. Cancelling in the middle of a DDL statement can leave
  that database **partly upgraded**: the server rolls back the statement, not the scripts before it. If a provider does not
  stop at once, the window gives up after a few seconds and says that some databases may be partly upgraded.
- **Nothing is remembered about what was played.** The scripts are replayable by convention (guarded with `IF NOT EXISTS`
  and the like), so playing a sprint again on a database is how you finish an interrupted run. There is no tracking table
  and no version detection; the dialog never asks which version a database is at.
- The rows a script's `SELECT` returns are discarded. Files are decoded from their byte-order mark, else as UTF-8, else as
  Windows-1252.
- The schema shown in the navigator is not refreshed by the run: use the tree's Refresh to see new tables.

**Limits.** SQL Server only; the heuristics above are the only help for spotting a long script; the folder is read as it is on
disk when you press Load (scripts changed afterwards are not re-read); the app cannot tell which scripts a database already
has, only that they are meant to be replayable.
