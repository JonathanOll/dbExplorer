# Known limitations

What dbExplorer does not do, and the edges of what it does. Linked groups and the database upgrade have their own limits in
[their pages](groups.md).

- Large object columns (text, binary) are fully materialised for each 200-row page.
- Linked groups: same-engine only, read-only fan-out, no automatic statement timeout, whole-script runs compare only the last
  row-returning statement (details in [Linked database groups](groups.md)).
- A lexical guard that does not know functions with side effects: the read-only guard (ordinary read-only connections, the raw
  WHERE box and group runs) looks at tokens, not at what a function does. It refuses a fixed deny-list, matched by exact name
  when followed by `(` (schema-qualified, double-quoted and comment-separated forms included): PostgreSQL server control
  (`pg_terminate_backend`, `pg_cancel_backend`, `pg_reload_conf`, `pg_switch_wal`, ...), statistics resets (`pg_stat_reset*`),
  session-level advisory locks (`pg_advisory_lock*`, `pg_try_advisory_lock*`), `dblink*`, server-side files (`lo_export`,
  `pg_file_*`), replication slot functions, and on SQL Server `OPENQUERY` / `OPENROWSET` / `OPENDATASOURCE` and `NEXT VALUE FOR`.
  Any other function with side effects (a user-defined function that writes, `nextval` / `setval`, ...) is not detected; on
  PostgreSQL and SQLite the database's own read-only context still refuses most of them, on SQL Server nothing but the
  rolled-back transaction does. Obfuscated forms (dynamic SQL built inside a function, aliases, a function reached through a
  view) are an accepted gap.
- No background schema refresh: the schema and the metadata cache behind `Ctrl+Shift+P` are reloaded when a connection is
  (re)opened or on the tree's Refresh action, never while a connection just stays open.
- SQL Server read-only mode is a statement guard plus a rolled-back transaction, not a database-enforced read-only session.
  PostgreSQL uses a `READ ONLY` transaction. SQLite read-only is enforced by the database: the file is opened read-only
  (SQLite is not pooled, so toggling the flag applies to the next connection opened, and a script tab reconnects at its
  next run) and scripts also set `PRAGMA query_only`.
- One connection per script tab: a transaction left open (`BEGIN TRAN` without `COMMIT`/`ROLLBACK`) keeps its locks until
  you finish it or close the tab, which rolls it back (an explicit rollback is sent on SQL Server and PostgreSQL). Table
  tabs, the tree and edits use other connections: they do not see an uncommitted change, and on SQL Server (default READ
  COMMITTED) a table tab reading rows that the open transaction has locked may wait until you `COMMIT` or `ROLLBACK`. There
  is no "transaction open" indicator (it cannot be detected reliably across the three engines). After a cancelled statement
  the next run waits for it to stop first.
- `Ctrl+Enter` ends a statement at `;`, a `GO` line or a blank line: a statement with a blank line inside it (a long
  `CREATE PROCEDURE`) is cut there; select it and press `Ctrl+Enter`, or use `F5`. One exception: an `UPDATE` / `DELETE`
  with no `WHERE` of its own followed, after a blank line, by a segment starting with `WHERE` runs as one statement.
- Toggling **Read-only** on a connection only takes effect on a script tab's connection at its next run, so a user
  transaction left open there keeps its locks until then (or until the tab closes).
- A result cut at the row limit cancels its command; on PostgreSQL that can abort an open transaction. The run then
  reports "connection was reset ... any open transaction was aborted and rolled back" at once (a probe runs at the end of a
  cancelled or truncated run) instead of a later `COMMIT` silently doing nothing.
- Date/time parameters are bound with the exact column type (SQL Server `datetime2(n)`, PostgreSQL `timestamptz` as UTC);
  this is covered by unit tests on the parameter objects only, not yet confirmed against real servers (see the [integration
  tests](development.md#integration-tests-against-real-databases)).
- The PostgreSQL and SQL Server code paths are only validated by unit tests with fakes unless the [integration tests](development.md#integration-tests-against-real-databases)
  are run against real servers.
- Authentication is user/password only (no Windows integrated or Azure AD), no SSH tunnels, no MySQL/Oracle, no DDL
  editing or export wizards.
- ER diagrams are read only (no schema editing). They show tables and declared foreign keys: no views, no virtual keys, no
  relation guessed from column names; a key to a table outside the shown schemas is not drawn. Diagram tabs and the places
  of dragged tables are not saved, and changing columns, schemas or depth lays the diagram out again. Lines are not routed
  around cards, so a long one can pass behind a table. A very large diagram is exported at a reduced resolution (at most
  48 megapixels, 16,000 pixels a side). PostgreSQL and SQL Server diagrams have not been tried against real servers.
- Views, procedures and functions: triggers are not listed. PostgreSQL aggregates and the functions installed by extensions
  are left out. A PostgreSQL view definition is rebuilt from the catalog (`pg_get_viewdef` plus a `CREATE` line): original
  comments and layout, view options and the indexes of a materialized view are not in it. A definition opened in a script tab
  is plain text: running it is subject to the usual statement splitting (a SQL Server body is cut at `;`, see above).
- Export: "All rows" of a script result runs the statement again, so the data can differ from what the grid shows, and
  temporary tables or an open transaction of the tab are not seen. A cell or column selection covers loaded rows only.
  Excel files were not checked in Excel itself (the tests read the package back). See [Copy and export](export.md).
- Quick open: columns are matched without typo tolerance. Across connections, procedures and functions are found for the
  connections opened since this feature exists (the cache is written after connecting).
