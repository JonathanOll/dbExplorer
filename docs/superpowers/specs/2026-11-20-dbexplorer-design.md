# dbExplorer — Design

Date: 2026-11-20

## Intent and why

A lightweight Windows desktop app to explore and edit databases (PostgreSQL, SQL Server, SQLite, others later) faster than DBeaver. The user is a full-stack developer at Company who today uses DBeaver with 8 connections (5 mssql, 2 postgres, 1 sqlite) organised in folders (Admin, Company, Keeple, Translate). Pain: DBeaver is heavy and navigation is slow. Goal: switch between databases instantly, jump to any table or column by typing its name, follow foreign keys, filter and edit rows.

## Goals

- Fast start, low RAM. Single native app, no Java.
- Connections organised in user-defined folders (tree, nestable not required in v1: one level like DBeaver).
- Quickly switch between several open connections (tabs per connection or per table).
- `Ctrl+T`: fuzzy-search tables/views of the active connection and open one.
- `Ctrl+Shift+T`: same, across all configured connections (connects lazily).
- `Ctrl+L`: on a table view, fuzzy-search a column and scroll/focus to it.
- Table view with two sub-tabs: **Data** and **Structure** (columns, types, nullability, PK, FK, indexes).
- Follow foreign keys: click a FK value to open the referenced row in a new tab.
- Filters: per-column filter row under the headers (operators: contains by default, `=`, `!=`, `<`, `>`, `null`, `!null`, `in (a,b)`), combined with AND; "filter by this value" from a cell context menu; plus a raw `WHERE` text box and column sort. Filters are executed server-side with parameters (never client-side on loaded rows only), with paging.
- Row editing: edits staged in the grid (highlighted), `Ctrl+S` shows a SQL preview then applies in a transaction; `Esc`/Revert discards. Insert and delete row supported.
- Tables without a primary key are read-only unless the user defines "virtual key" columns for that table (persisted), mirroring DBeaver's virtual PK.
- Import the user's DBeaver config (`%APPDATA%\DBeaverData\workspace6\General\.dbeaver\data-sources.json` + `credentials-config.json`): folders, connections, credentials.

## Script runner (added by user after review — approved)

- A SQL editor tab per connection (`Ctrl+N` opens one on the active connection), AvaloniaEdit with syntax highlighting.
- `Ctrl+Enter` runs the statement under the cursor (or the selection); `F5` runs the whole script. Statements split on `;` (Postgres/SQLite, respecting quotes/comments/`$$` bodies) and on `GO` lines (SQL Server).
- Results in grids below the editor, one per result set (multiple result sets / statements), with rows affected and duration; cancel button for running queries; result row limit (e.g. 1000, configurable) with "load more".
- Honours the connection `read-only` flag: write statements are refused with a clear message (builder's call on detection: first-keyword check is acceptable, DB-level read-only transaction where available is better).
- Scripts are plain files the user can save/open (`Ctrl+S` in editor saves to file); unsaved editor contents are restored on restart. Query history is out of scope for v1.
- Default auto-commit (matches DBeaver "dev" connection type); no manual transaction UI in v1.
- Table-view edits still go through the staged-edit + preview flow, not the script runner.

## Non-goals (v1)

- (Script runner moved to goals after user review: see "Script runner" below.)
- No schema/DDL editing, no ER diagrams, no data export/import wizards.
  - Update 2026-11-26: read-only ER diagrams are now in. Layout and model live in `DbExplorer.Core/Diagrams` (own layered
    layout, no graph library); metadata is the schema each `IDbDriver` loads in bulk; the canvas is the one of the plan view
    (`FreeCanvas`), painting only the visible tables. Schema editing is still out.
- No cross-platform support (Windows only, though Avalonia keeps the door open).
- No SSH tunnels, no other database engines (MySQL, Oracle) in v1; the driver layer must make adding them cheap.

## Anti-goals

- Becoming a heavy IDE. If startup is slow, it failed. (RAM is not a constraint: user said even 500 MB is fine, 2026-11-20.)
- Silent data changes: nothing is written without an explicit save.
- Storing passwords in clear text.

## Constraints

- Windows 11. Dev machine has .NET 10 SDK and Node; no Rust/Go.
- Connection configs live in a local user file (JSON) under `%APPDATA%\dbExplorer`. Passwords encrypted with Windows DPAPI (current user).
- Existing SQL Server hosts use `sslTrustServerCertificate` per connection; must be configurable.
- Code identifiers and comments in English; UI language: English (user is French; i18n not needed in v1 — builder's call to keep strings centralised).
- Not a git repo yet; initialise one in the project (builder's call).

## Approaches considered

1. **.NET 10 + Avalonia (chosen, my call — user delegated tech choice).** One native app, native ADO.NET drivers (Npgsql, Microsoft.Data.SqlClient, Microsoft.Data.Sqlite), good grid and theming, dark/light. Cost: ~100 MB RAM, bigger than WPF.
2. **.NET 10 + WPF.** Lightest on Windows, but dated look and more work for modern UI; Windows only.
3. **Tauri (Rust + web UI).** ~10 MB binary, but Rust not installed, weaker SQL Server driver, hand-built virtualised grid. Riskier for little gain.

Electron rejected: too heavy.

## UI sketch

```
┌ Connections ─────┬ [Admin SQLite ▾] [Company local ▾]  ← tabs
│ ▾ Admin          │ users ┃ Data ┃ Structure
│   Admin Postgre  │ WHERE: ____________   ⟳
│ ▾ Company            │ id │ name │ company_id→ │ …
│   ▸ Company local    │ filter row: [  ] [ Bo ] [   ]
│     ▸ dbo        │ 1  │ Bob  │ 42 ↗        │
└──────────────────┴────────────────────────
```

## Decisions made by the user

- Databases: pgsql, mssql, sqlite, "..." (extensible).
- Folders to group databases; easy switching between them.
- `Ctrl+T` table jump by typing name; jump to a column inside a table view.
- Use: look at data and structure, edit rows, follow FKs, filters.
- Lightweight; technology free.

## My calls (to be checked by the user)

- Avalonia/.NET 10; DPAPI-encrypted passwords; DBeaver import.
- Edit model: staged edits + SQL preview + explicit save; PK required or virtual key.
- `Ctrl+Shift+T` global search and `Ctrl+L` column jump shortcut choice.
- No SQL editor in v1.

## Additional calls (from builder check; all mine, user can override)

- **Connection = one database** (as in the DBeaver config: each connection names one `database`, "show all databases" is off). Tree: folder > connection > schema > table/view. SQLite has no schema level.
- **Tabs**: left tree is a navigator. Top-level tabs are open tables, labelled `connection · table`, with a colour chip per connection. `Ctrl+T` searches the connection of the active tab (or the one selected in the tree). FK click, `Ctrl+T` and `Ctrl+Shift+T` all open a new table tab. Several connections stay connected at once (one session per connection). The sketch's connection tabs are replaced by this.
- **`Ctrl+Shift+T`** searches an on-disk metadata cache (schema/table/column names) per connection, filled when a connection is first opened and refreshed in the background on each later connect. It never connects to a database by itself; choosing a result connects. Connections never opened show no results.
- **Auth**: v1 supports user/password only (SQL Server SQL auth, Postgres native, SQLite file). All 8 existing connections use this. Windows integrated/Azure AD are out of v1. SQL Server also exposes `encrypt` and `trustServerCertificate`. The DBeaver import is a one-time action, re-runnable: connections already present (same name) are skipped.
- **Safety**: each connection has a `read-only` flag (default off; imported connections are all DBeaver "dev" type so default off). When on, editing is disabled.
- **Scale**: page size 200 rows with OFFSET paging; total row count only on demand (button), never automatic. Views are read-only. Exotic types (uniqueidentifier, binary, json) shown as text; binary shown as hex preview. Target: cold start to a usable window under ~1 s (the pass/fail bar). RAM bar dropped by the user (2026-11-20): even 500 MB is fine, so GPU rendering may be chosen if it helps speed.
- Folders are one level only in v1 (like DBeaver).

## Left to the builder

- Solution layout, MVVM library, grid control choice (Avalonia DataGrid vs TreeDataGrid vs custom), paging size, fuzzy-match algorithm, metadata caching strategy, test strategy, theming details, installer/publish mode (framework-dependent vs self-contained).

