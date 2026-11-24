# N2F database upgrade (play a sprint's dbupdate scripts) - design

Date: 2026-11-24. Status: approved by the user in conversation ("go"). Size: project.

## Intent and why

The user upgrades N2F databases by playing the SQL scripts of a sprint from the n2f repo folder
`Web/Common/n2jWeb.Data/dbupdate/` (on their PC `C:\repos\n2f\Web\Common\n2jWeb.Data\dbupdate\`). Today this is manual.
They want dbExplorer to: take that folder path in its options, analyse its structure, play the scripts of one sprint
given its number, warn about scripts that create an index or are long, and run it easily on a whole group of databases.

The scripts are replayable by convention (guarded, e.g. `IF NOT EXISTS`), confirmed by the user. So **no per-database
tracking of what was already played**.

## Goals

- Option "dbupdate folder" persisted in `config.json` (default empty = feature off).
- Scanner that understands the folder layout (below) and lists the scripts of sprint N.
- Preview of the scripts with checkboxes and a warnings column before anything runs.
- Run on the current connection or on a group (4 databases at a time, like "Run on group"), per-database status
  panel, Cancel per database and Cancel all.
- Warnings for scripts that create/rebuild an index, or look long.

## Non-goals / anti-goals

- No tracking table, no "already played" memory, no version-detection of a database.
- No change to the read-only guarantee of "Run on group" (the existing group run stays read and compare only).
  A write must never become possible through the existing group run.
- No edit of the n2f repo, no marker convention in scripts.
- No wrapping of scripts in a transaction (scripts do DDL, `GO` batches, replayable).
- Not a Liquibase replacement (n2f's `databases/main/changelog` is out of scope).
- Row-count based "long" detection is optional, later (see Open for the builder).

## Facts about the folder (observed 2026-11-24)

- `dbupdate/` has 169 entries and 1434 `.sql` files.
- Sprint folders: `vNNNN-sprint X` (also `v0010-hotfix-permission-group`, `v0691-hotfix <text>`, `V0121-change brandvariant`).
  `NNNN` is a version counter: `0770 + (sprint - 134) * 10` in the recent range; hotfix/extra folders use the
  in-between numbers (`v0781-sprint 135`).
- One sprint can have several folders: sprint 143 = `v0860`, `v0861`, `v0862`; sprint 129 = `v0710` and `v0720`;
  note `v0710-sprint 128` and `v0710-sprint 129` share a number. Separator after `sprint` is `-` or a space.
- Script file names: `<trigram> - <NNN> - <description>.sql` (spaces and dashes vary; legacy files like
  `cmi-01-...`, `fle - v0190 - 001 - ...`). Some end with `- Production1` / `- Production2`: they target specific servers.
- Root also holds legacy standalone scripts and `_functions`, `_stored_procedures`, `_triggers`, `_views`,
  `_seed account 1` (re-runnable object definitions, not part of a sprint). Out of scope for the sprint run.
- Scripts may contain `GO` batch separators and T-SQL; the target engine is SQL Server.

## Approaches considered

1. **Text analysis of the script for warnings (chosen).** Lexer-based detection of `CREATE [UNIQUE] [CLUSTERED|NONCLUSTERED] INDEX`,
   `ALTER INDEX ... REBUILD`, `ALTER TABLE ... ALTER COLUMN`, `UPDATE`/`DELETE` without `WHERE`, `WHILE` loops, `WAITFOR`.
   Cheap, no change to n2f. Heuristic, so warning only, never a block.
2. Marker comment in scripts (`-- @long`). Reliable but needs the whole n2f repo to adopt it. Rejected.
3. Measure real table row counts on the target. Better signal, extra queries and engine-specific code. Optional follow-up on top of 1.

Execution: a **separate Upgrade runner** reusing connections and the parallel per-database machinery of the group
runner. Relaxing the read-only guard of the group run was rejected (it would weaken an existing guarantee).

## Design (decisions by the assistant are marked "call")

1. **Option**: a new "Options..." dialog (reached from the existing gear menu of the navigator toolbar) with the
   dbupdate folder path (browse + validate: exists, contains `v*` folders). Stored in `config.json` (`DbUpdatePath`,
   backward compatible, default null). Menu item "Upgrade database..." only enabled when the path is valid. (call)
2. **Scanner** (Core, pure, no UI): `DbUpdateCatalog.Scan(path)` -> sprints with their folders and scripts.
   Sprint number from `sprint\s*[- ]?\s*(\d+)` in the folder name; folders without it (hotfix, V0121) are listed
   but selectable only by folder. (call) Order: folder version number ascending, then folder name, then script file name
   (natural sort, so `adj - 003` before `adj - 037`). (call) Scripts whose name ends with `Production<n>` are
   unchecked by default and labelled. (call)
3. **Preview**: dialog "Upgrade database" - input sprint number, list of scripts (checkbox, name, folder, warnings),
   warnings summary at the top. Unknown sprint -> clear message listing the nearest sprints.
4. **Warnings analyzer** (Core, uses `SqlLexer` so comments and strings are ignored): kinds `Index`, `LongRunning`
   (unbounded UPDATE/DELETE, loop, ALTER COLUMN, REBUILD, WAITFOR); each warning has script, kind, short reason, line.
   Warning only, shown in the preview and repeated in the confirmation.
5. **Target**: the current connection, or a group (the user's existing groups; the dialog offers the group of the
   current connection). 4 databases at a time (same as group run).
6. **Confirmation**: explicit dialog listing target databases, number of scripts and all warnings; for a group it asks
   to confirm with the sprint number typed. (call)
7. **Safety**: connections flagged read-only are refused (named in the message) before anything runs; the check runs
   before any connection is opened, and the runner repeats it. The Upgrade runner is the only write path of the feature
   and is never reachable from the read-only group run.
8. **Execution**: per database, scripts one after another in the scanned order; each script is split into batches on
   `GO` (reuse `ScriptSplitter`), no transaction wrapper, command timeout generous/unbounded for index builds (call: 0 = none,
   cancellable). First error in a database stops that database (remaining scripts marked skipped); other databases continue.
   Status panel per database: waiting / running (script i of n, name) / done / failed (script + error) / cancelled,
   Cancel per database and Cancel all. A log of what ran can be copied.
9. **Persistence**: last used sprint number is not stored; no per-database state.

## Constraints

- Windows, Avalonia 11, compiled bindings, SQL Server driver in Core; code and comments in English.
- Must not slow startup (the options dialog and catalog scan happen only on demand, never at launch).
- Existing tests must stay green; new logic unit-tested (scanner on a temp tree, analyzer, runner against fakes,
  view-model state machines), headless window test for the dialog.
- Follow the project's existing patterns (see README "Run on group", `GroupRunner`, `ScriptRunner`, `ScriptSplitter`,
  `UpdateViewModel` for a recent view-model with injected abstractions).

## Left to the builder

- Exact UI layout and wording, following the look of existing dialogs.
- Exact heuristics thresholds for "long" (document them), natural sort details, how legacy names are parsed.
- Whether the Upgrade runner subclasses/reuses `GroupRunner` pieces or sits beside it (must not weaken its read-only guard).
- Optional: row-count hints for index warnings (approach 3), only if cheap.
- README section and tests.
