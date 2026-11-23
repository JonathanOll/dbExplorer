# dbExplorer — Linked database groups — Design

Date: 2026-11-23. Status: proposed, awaiting user approval. Extends `2026-11-20-dbexplorer-design.md`.

## Intent and why

The user works with several databases that share the same schema (e.g. `acmeResto` / `acmeResto2`, "Translate dev" / "Translate local", dev / preprod / customer copies). They want to **link** such databases into a group, **switch** between members instantly without losing what they are looking at, and **run the same query on all members in parallel** to read and compare results.

User decision (2026-11-23): **read and compare only**. No write fan-out, ever, in this feature.

## Goals

- **Group**: a named, ordered list of connections, stored in `config.json` (`AppConfig.Groups`). A connection can belong to several groups. All members of a group must have the same engine (`DbKind`), so one SQL text runs on every member; enforced when editing the group.
- **Tree**: groups appear as their own section in the connections tree; a group node expands to its members (shortcuts to the real connections). Context menu: New group…, Edit group…, Rename, Delete group, Add to group…, Remove from group, Check schema differences. Group editor dialog: name + checklist of connections of one engine.
- **Switch**: table tabs and script tabs whose connection belongs to a group show a database selector (members of that group; if the connection is in several groups, a group picker first). Switching keeps the tab: a table tab reloads the same table (same filters, raw WHERE, sort, page 0); a script tab keeps its text and results and uses the new connection from the next run. Next/previous member shortcut (the builder picks chords that do not collide with existing shortcuts nor break typing). Pending edits go through the existing Save/Discard/Cancel guard; a table missing in the target database shows an error strip, not a crash; a script tab's open transaction on the old connection is rolled back when it is released (existing behaviour, say so in the tooltip).
- **Run on group** (script tab): button + shortcut run the statement under the caret / the selection / the whole script (same selection rules as today) on **all members in parallel** (concurrency cap, default 4, configurable), each member on its own dedicated connection owned by that tab. A status panel lists every member: pending / running / done (rows, ms) / error / cancelled; Cancel all and cancel one.
- **Read-only by construction**: a group run ALWAYS applies the kind-aware `ReadOnlyGuard` and the read-only execution context to every member, whatever the member's own `ReadOnly` flag says; a write statement is refused for all members with a clear message ("Group runs are read-only; run writes on one database at a time").
- **Results and comparison**: one result tab per member plus an **All** tab that merges the rows with a leading `Database` column (columns must match; if member column sets differ, merge on the common columns and show a warning naming the differences). A **Differences** toggle on the All tab shows only the rows that are not identical across all members, with a `Present in` column listing the databases that have the row (rows are compared on all their values). Row limit per member as today (default 1000, truncation notice per member).
- **Check schema differences** (group menu): connects read-only to all members, loads their schemas and shows a report: tables/views missing or extra per member, columns missing/extra/type-different. Informational, never blocking.
- A failing or unreachable member never blocks the others; timeouts per member; errors are shown on that member only.

## Non-goals

- Any write on several databases at once (UPDATE/DDL/migrations), data sync, full table data diff, schema migration, cross-engine groups, opening one table in all members at once, automatic discovery of same-schema databases, per-group credentials.

## Anti-goals

- A fan-out that can modify data even by accident (a missed guard path).
- Slower launch: groups are a tiny part of the config; nothing eager is added at startup.
- Hiding failures: every member's outcome is visible.

## Constraints

- Reuses: `ScriptConnection` (dedicated connection per tab; one per member for fan-out), `ScriptRunner`, `ReadOnlyGuard` (kind-aware), the read-only execution context, `SessionManager`/`Session`, `ResultGrid`, existing tab/shortcut/exit-guard hooks, `ConfigStore` (missing `Groups` property must load as empty: older config files keep working).
- Connection management stays per connection (credentials, ReadOnly flag, DPAPI) — a group only references connection ids; deleting a connection removes it from its groups; a group with fewer than 2 members is allowed but flagged.
- The visual redesign (in progress) provides the design tokens/icons; new views follow them (group icon, member selector, run-on-group button, status panel).
- Launch speed is the user's top priority.

## Approaches considered

1. **First-class groups in config + fan-out engine in Core (chosen).** Groups are persisted entities; switching and fan-out are built on the existing per-tab dedicated connections. Trade-off: new config schema, new tree section, new UI in script/table tabs.
2. **Reuse folders as groups.** Smallest change, but folders are one-level organisation only, a connection sits in one folder, folders mix engines: rejected (a connection often belongs to several groups, and a folder-as-group would change what folders mean for the user's existing organisation).
3. **Ad-hoc multi-selection in the tree ("Run on selected")**, no persisted groups. Flexible but no persistent switching and no named sets: possible later add-on on top of (1), not instead of it.

## Decisions made by the user

- Purpose: read and compare only.
- Wants: link databases of the same schema, switch easily, run the same query on them in parallel.

## My calls (to check)

- Same-engine requirement per group; concurrency cap 4; All tab with `Database` column + Differences toggle; schema-difference report; fan-out read-only enforced independent of the member flag; shortcuts chosen by the builder.

## Left to the builder

- Config schema details for `Groups` (ids, ordering), tree node types, selector control, fan-out coordinator class design (Core), result merging data structure, how the Differences comparison hashes rows (value equality incl. NULLs, binary, culture-invariant), concurrency cap setting UI, test strategy (fake drivers + SQLite files as members), README/shortcut table updates.
