# Database upgrade Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** From the gear menu, play the SQL scripts of one Company sprint (read from the company `dbupdate` folder) on the current database or on a whole group, after a preview with warnings (index creation, long-running statements) and an explicit confirmation.

**Architecture:** Pure Core pieces (folder scanner, T-SQL warning analyzer, plan builder, GO-batch splitter) feed a new, separate, write-capable `UpgradeRunner` that runs batches with plain `DbCommand.ExecuteNonQueryAsync`, 4 databases at a time. It sits beside `GroupRunner` and shares no code with it, so the read-only group run stays byte-for-byte what it is. Two view models (`OptionsViewModel`, `UpgradeViewModel`) take injected delegates, in the style of `UpdateViewModel`; one last wiring task adds the gear-menu entries.

**Tech Stack:** .NET 10, Avalonia 11 (compiled bindings, CommunityToolkit.Mvvm), Microsoft.Data.SqlClient / Microsoft.Data.Sqlite (tests use SQLite files, as `GroupRunnerTests` does), xUnit, Avalonia.Headless.

**Spec:** `docs/superpowers/specs/2026-11-24-db-upgrade-design.md` (approved). Read it with this plan.

## Global Constraints

- The existing group run stays read-only: **`src/DbExplorer.Core/Groups/GroupRunner.cs`, `src/DbExplorer.Core/Scripting/ReadOnlyGuard.cs` and `src/DbExplorer.Core/Scripting/ScriptRunner.cs` must not be changed by any commit of this feature** (stage files by name, never `git add -A`/`-u`). The upgrade never goes through them; a write must never become possible through the group run. Caution: at the time of writing another feature (execution statistics) has uncommitted edits in `GroupRunner.cs`, `ScriptRunner.cs`, `ScriptConnection.cs` and the script-tab view models in the same working tree; leave those edits alone (do not stage, revert or reformat them) and check with `git diff --cached --stat` that none of those paths is in your commit. If that feature has been committed by then, `git diff <commit-before-task> -- <those files>` must be empty for this feature's own commits.
- No per-database tracking, no "already played" memory, no version detection, no edit of the repository, no script marker convention, no transaction wrapper around scripts (scripts do DDL and `GO` batches and are replayable).
- Config key: `DbUpdatePath` in `config.json`, backward compatible, default `null` (feature off). Last used sprint number is not stored.
- Folder scan and options dialog happen **only on demand**, never at launch (no disk access added to the startup path; `App.axaml.cs` gains one cheap line).
- 4 databases at a time (the same number as "Run on group"). Command timeout 0 (none), cancellable.
- Target engine is SQL Server (T-SQL). The UI refuses other engines before anything is opened; the runner itself stays engine-agnostic so its tests can use SQLite files.
- Connections flagged read-only are refused (named in the message) **before any connection is opened**; the runner repeats the check, and the connection factory re-checks the live session config.
- Windows, Avalonia 11, **compiled bindings** (`x:DataType` on every view), code and comments in English, SQL Server driver in Core.
- Existing tests stay green. New logic is unit-tested (scanner on a temp tree, analyzer, runner against SQLite files, view-model state machines) plus headless window tests for the two dialogs.
- Follow existing patterns: new Core code in `src/DbExplorer.Core/Upgrade/`, tests in `tests/DbExplorer.Core.Tests/Upgrade/` and `tests/DbExplorer.App.Tests/Upgrade/`, commit messages `feat(upgrade): ...`, each commit message ends with the repository's Co-Authored-By trailer.
- Run commands: `dotnet test tests/DbExplorer.Core.Tests --filter "FullyQualifiedName~<Class>"`, `dotnet test tests/DbExplorer.App.Tests --filter "FullyQualifiedName~<Class>"`, full suite `dotnet test DbExplorer.sln`.

## Review Focus

Inputs and conditions the spec implies but the plan's main tests would not reach (each has a test in the task named in brackets):

1. **Sprint input that is not a sprint**: empty, `abc`, `-5`, `0`, ` 143 `, `99999999999`, an unknown number, a sprint whose folders hold no scripts. Expected: a clear message (with the nearest sprints for an unknown one), no exception, nothing contacted. [Task 6]
2. **Scripts silently skipped or mangled**: the real folder holds an extension-less file in a sprint folder (`v0690-sprint 126/fbo - 001 - Delete Obsole Sync Triggers`), 53 of 1243 scripts are Windows-1252 (not UTF-8) and 221 start with a BOM. Expected: ignored files are listed in the preview; text decodes correctly and never starts with U+FEFF. [Tasks 1, 5]
3. **`;` inside `IF ... BEGIN ... END`** (real shape: `ale - 002`, `DROP INDEX ... ;` inside a guard) must not cut a batch, and `GO n` (repeat count, absent today) must be refused instead of silently run once. [Task 3, Task 5]
4. **Long scripts**: no 30 s command timeout, and a result set returned by a script (`SELECT` mid-script) must never be truncated or cancel the batch. [Task 3]
5. **Read-only flipped or window closed mid-way**: the flag changed after the dialog opened; X or Cancel all while connecting or running; every connection released; a database that failed never stops the others. [Tasks 3, 6]

## Decisions the spec left to the builder (do not re-decide)

| Topic | Decision |
|---|---|
| Runner placement | New `UpgradeRunner` in Core, standalone. It copies the small slot / semaphore / connect-with-cancel pattern of `GroupRunner` (about 40 lines) rather than extracting it, to keep `GroupRunner.cs` untouched. It uses `DbConnection` + `ExecuteNonQueryAsync(ct)` + `CommandTimeout = 0`, not `ScriptRunner`/`ScriptConnection` (those set no timeout, so SqlClient's 30 s applies, and truncate big result sets by cancelling the command). |
| Batch split | New `ScriptSplitter.SplitBatches(string)`: SQL Server lexing, **GO lines only**, `;` kept inside the batch (`Split` also cuts at `;`, which breaks `IF/BEGIN/END`). `GO n` (n > 1) is refused: `ScriptSplitter.FindGoRepeat`. |
| Sprint folders | Scan top level of the root only: folders matching `^[vV]\d+` (so `_views`, `_functions`... are out). Sprint number = first `sprint\s*[- ]?\s*(\d+)` in the folder name (case-insensitive), else `null` (selectable by folder only). `v0710-sprint 128` and `v0710-sprint 129` are different sprints that share a version. No recursion; anything in a version folder that is not a top-level `*.sql` file (extension-less files, subfolders, `.txt`) goes to `DbUpdateFolder.Ignored` and is shown in the preview. |
| Order | Folders: version number ascending, then natural name. Scripts: natural order of the file name, full-name tiebreak (`fbo - 001 - A` before `fbo - 001 - B`). Natural = case-insensitive, runs of digits compared as numbers (so `adj - 003` < `adj - 037`, `adj - 2` < `adj - 10`), other characters ordinal. |
| Production | A script whose file name (without `.sql`) matches `-\s*Production\d+\s*$` (case-insensitive) is labelled `Production<n>` and unchecked by default. Matching is on the file name only (`mch - 003 - ...Production notes.sql` is not one). |
| Analyzer | Lexer-based (`SqlLexer`, SQL Server options: comments, strings, `[brackets]` ignored). Kinds `Index` and `LongRunning`; each warning = kind, short reason, 1-based line. Rules in Task 2. `ALTER INDEX ... REBUILD` is kind `Index` (it is an index operation). Single numeric threshold: a script of **100 000 characters or more** is `LongRunning` "Large script". Heuristic, never blocks. |
| Encoding | `Decode` (BOM, else strict UTF-8, else Windows-1252) moves to Core as public `SqlTextDecoder`; `ScriptFileEncoding.Decode` delegates to it. |
| Unreadable / `GO n` script | The plan item carries a `LoadError`; the preview shows it, the checkbox is disabled, and **Run stays disabled** while any such script exists (never run a partial sprint silently). |
| Targets | "This database" (the current connection) plus one entry per group of that connection with at least 2 members. Group target = typed confirmation of the sprint number (or of the folder code, e.g. `v0691`, for a by-folder run). |
| Flow | One modal, one view model: `Setup -> Preview -> Confirm -> Running -> Finished`. Confirmation is a state of the same window, not a new dialog. Closing the window while running is refused (Cancel all first). |
| Sessions | The view model starts one session resolve per target on the UI thread (through the tree, as `RunOnGroupAsync` does) before `Task.Run`; the runner's connection factory awaits it and opens a plain connection. |
| Path validity | The gear menu's "Upgrade database..." is enabled only after `DbUpdateCatalog.Validate` succeeded; validation runs when the flyout opens and when the options are saved, never at construction. |
| Options dialog | Browse is code-behind (`StorageProvider.OpenFolderPickerAsync`); validation runs on OK, after Browse and on focus loss (not per keystroke: the path may be a network share). |
| Dialog plumbing | New `IUpgradeDialogs` + `UpgradeDialogService` (precedent: `IEditDialogs`); `IDialogService` and `FakeDialogs` are not touched. |

## File Structure and parallelism

New Core files (`src/DbExplorer.Core/Upgrade/`): `NaturalComparer.cs`, `DbUpdateCatalog.cs` (T1); `ScriptAnalyzer.cs` (T2); `UpgradeRules.cs`, `UpgradeRunner.cs`, `UpgradeLog.cs` (T3); `SqlTextDecoder.cs`, `UpgradePlan.cs` (T5).
Modified Core: `Scripting/ScriptSplitter.cs` (T3 only), `Model/AppConfig.cs` + `Config/ConfigStore.cs` (T4 only).
New App files: `ViewModels/OptionsViewModel.cs`, `Views/OptionsDialog.axaml(.cs)` (T4); `ViewModels/UpgradeViewModel.cs`, `Views/UpgradeDialog.axaml(.cs)` (T6); `ViewModels/IUpgradeDialogs.cs`, `UpgradeDialogService.cs`, `ViewModels/MainViewModel.Upgrade.cs` (T7).
Modified App: `ViewModels/ConnectionTreeViewModel.cs` (T4), `ViewModels/ScriptFileEncoding.cs` (T5), `Views/MainWindow.axaml(.cs)`, `App.axaml.cs`, `tests/.../Updates/UpdateWindowTests.cs` (T7). `README.md` (T8, append-only).

| Wave | Tasks (independent inside a wave: disjoint files, no shared compile dependency) |
|---|---|
| 1 | **T1** scanner, **T2** analyzer, **T3** runner + splitter, **T4** config + options dialog, **T8** README (text only; proofread after T7) |
| 2 | **T5** plan builder (needs T1, T2) |
| 3 | **T6** upgrade view model + dialog (needs T3, T5) |
| 4 | **T7** wiring (needs T4, T6) |

---

### Task 1: Folder scanner and natural sort (Core)

**Files:**
- Create: `src/DbExplorer.Core/Upgrade/NaturalComparer.cs`, `src/DbExplorer.Core/Upgrade/DbUpdateCatalog.cs`
- Test: `tests/DbExplorer.Core.Tests/Upgrade/DbUpdateCatalogTests.cs`, `tests/DbExplorer.Core.Tests/Upgrade/TempDbUpdateTree.cs` (fixture helper)

**Interfaces:**
- Produces:
  - `public sealed class NaturalComparer : IComparer<string> { public static NaturalComparer Instance; public int Compare(string? a, string? b); }`
  - `public sealed record DbUpdateScript(string FileName, string FullPath, string FolderName, bool IsProduction, string? ProductionTag)`
  - `public sealed record DbUpdateFolder(string Name, string FullPath, string Code, int Version, int? Sprint, IReadOnlyList<DbUpdateScript> Scripts, IReadOnlyList<string> Ignored)` (`Code` = leading `v0860`)
  - `public sealed class DbUpdateCatalog { string Root; IReadOnlyList<DbUpdateFolder> Folders; IReadOnlyList<int> Sprints; IReadOnlyList<DbUpdateFolder> FoldersOfSprint(int sprint); IReadOnlyList<int> NearestSprints(int sprint, int perSide = 2); static string? Validate(string? path); static DbUpdateCatalog Scan(string path); }`
  - Test helper: `sealed class TempDbUpdateTree : IDisposable { string Root; void Add(string folder, string file, string text = "SELECT 1"); void AddDir(string name); static TempDbUpdateTree Standard(); }`. `Standard()` builds: `v0002-sprint 23`, `v0003-sprint-23-hotfix`, `v0013-sprint-ocr`, `v0122-Hotfix-Trads`, `V0121-change brandvariant`, `v0691-hotfix Deny var on payment accountant`, `v0710-sprint 128`, `v0710-sprint 129`, `v0720-sprint 129`, `v0860-sprint 143` (files `adj - 037 - x.sql`, `adj - 003 - Add covering index on Suppliers AccountId Deleted.sql`, `adj - 10 - x.sql`, `adj - 2 - x.sql`, `fbo - 001 - B.sql`, `fbo - 001 - A.sql`, `mch - 010 - Disable users - Production2.sql`, `mch - 003 - Production notes.sql`, extension-less `fbo - 001 - Delete Obsole Sync Triggers`, `notes.txt`), `v0861-sprint 143`, `v0862-sprint 143`, plus root-level `_views\IndexDetails.sql` and `cmi-01-service-broker-x.sql`.
- Consumes: nothing.

- [ ] **Step 1: Write the failing tests** (`DbUpdateCatalogTests`, assertions on `TempDbUpdateTree.Standard()`):
  - `Scan_ListsOnlyVersionFolders_OrderedByVersionThenName`: `Folders` names start `v0002-sprint 23`, `v0003-sprint-23-hotfix`, ..., contain neither `_views` nor the root `.sql`; both `v0710-*` folders present, `V0121-change brandvariant` sorts by version 121.
  - `Sprint_ParsedFromName`: `v0860-sprint 143` -> 143; `v0003-sprint-23-hotfix` -> 23; `v0009-sprint 28`-style space and dash separators; `v0013-sprint-ocr`, `v0122-Hotfix-Trads`, `V0121-change brandvariant`, `v0691-hotfix Deny var on payment accountant` -> `null`.
  - `FoldersOfSprint_143_IsThreeFoldersInOrder` (`v0860`, `v0861`, `v0862`); `FoldersOfSprint_129_IsV0710And0720_NotSprint128` (the shared version `v0710` does not leak); unknown sprint -> empty.
  - `Scripts_AreInNaturalOrder`: in `v0860-sprint 143`: `adj - 2`, `adj - 003`... asserted as the exact expected sequence (`adj - 2 - x`, `adj - 003 - Add covering...`, `adj - 10 - x`, `adj - 037 - x`, `fbo - 001 - A`, `fbo - 001 - B`, `mch - 003`, `mch - 010`).
  - `Production_FromFileNameSuffixOnly`: `... - Production2.sql` -> `IsProduction`, tag `Production2`; `mch - 003 - Production notes.sql` -> not.
  - `Ignored_ListsNonSqlEntriesAndSubfolders`: `v0860-sprint 143` ignores `fbo - 001 - Delete Obsole Sync Triggers` and `notes.txt`; a subfolder added with `AddDir` is ignored too and its scripts are not listed (no recursion).
  - `NearestSprints_143Absent` e.g. tree without 144/145: `NearestSprints(999)` = the two highest; `NearestSprints(140)` on sprints {128,129,143} -> `[129, 143]`; empty catalog -> empty.
  - `Validate`: null/blank -> message; missing folder -> message naming the path; a path that is a file -> message; folder with no `v\d+` subfolder -> message; trailing backslash and surrounding spaces accepted; `Standard()` root -> `null`. Unauthorized/IO exceptions are returned as a message, never thrown.
  - `NaturalComparerTests`: `adj - 003` < `adj - 037`; `adj - 2` < `adj - 10`; case-insensitive; `a1` vs `a01` deterministic (leading zeros tiebreak, never equal for different strings); null-safe.
- [ ] **Step 2: Run** `dotnet test tests/DbExplorer.Core.Tests --filter "FullyQualifiedName~DbUpdateCatalogTests|FullyQualifiedName~NaturalComparerTests"`. Expected: compile failure / FAIL.
- [ ] **Step 3: Implement** the types above. `Scan` lists directories matching `^[vV]\d+`, files `*.sql` top-level only (`Directory.EnumerateFiles(dir, "*.sql")` also matches `.sqlx`-like extensions on Windows: filter on `Path.GetExtension` equal `.sql`, case-insensitive). A non-numeric-overflow version (`int.TryParse` fails) is `int.MaxValue`. `Scan` does not read file contents.
- [ ] **Step 4: Run** the same command. Expected: PASS. Also `git diff --cached --stat` (before committing) lists none of the protected files.
- [ ] **Step 5: Commit** `git add src/DbExplorer.Core/Upgrade tests/DbExplorer.Core.Tests/Upgrade && git commit -m "feat(upgrade): dbupdate folder scanner with sprint lookup and natural order"`.

---

### Task 2: T-SQL warning analyzer (Core)

**Files:**
- Create: `src/DbExplorer.Core/Upgrade/ScriptAnalyzer.cs`
- Test: `tests/DbExplorer.Core.Tests/Upgrade/ScriptAnalyzerTests.cs`

**Interfaces:**
- Produces:
  - `public enum ScriptWarningKind { Index, LongRunning }`
  - `public sealed record ScriptWarning(ScriptWarningKind Kind, string Reason, int Line)`
  - `public static class ScriptAnalyzer { public const int LargeScriptChars = 100_000; public static IReadOnlyList<ScriptWarning> Analyze(string sql); }` ordered by line; never throws (empty, unterminated string or comment included).
- Consumes: internal `SqlLexer.Tokenize(sql, SqlLexOptions.For(DbKind.SqlServer))` (same assembly).

Rules (this is the whole contract; implement and test exactly this, warn only, never block):
- **Index**: words `CREATE` + up to 4 modifiers from {`UNIQUE`,`CLUSTERED`,`NONCLUSTERED`,`COLUMNSTORE`,`XML`,`SPATIAL`} + `INDEX` -> reason `Creates index <name>` (name = next token, brackets/quotes stripped; `Creates an index` if none). An `IF NOT EXISTS` guard around it does not suppress the warning. `ALTER INDEX ... REBUILD` (word `REBUILD` within 12 tokens, before a `;`) -> `Rebuilds index <name|ALL>`. `DROP INDEX`, `ALTER INDEX ... DISABLE/REORGANIZE`, `sys.indexes`, `CREATE TABLE IndexLog` -> nothing.
- **LongRunning**:
  - `UPDATE`/`DELETE` that starts a DML statement and has no `WHERE` at paren depth 0 and no `TOP`: `UPDATE without WHERE: touches every row` / `DELETE without WHERE: ...`. A word is a DML start unless the previous token is `ON`, `OF`, `FOR`, `AFTER`, `INSTEAD`, `GRANT`, `DENY`, `REVOKE`, `THEN`, `,` or the next token is `STATISTICS` or `(` (`IF UPDATE(col)`). Target starting with `#` or `@` (temp table / table variable) is ignored. The statement ends (T-SQL has no mandatory `;`) at paren depth 0 on a `;` or a statement word: `INSERT UPDATE DELETE MERGE SELECT CREATE ALTER DROP TRUNCATE EXEC EXECUTE DECLARE IF ELSE WHILE BEGIN PRINT RETURN WAITFOR USE RAISERROR THROW`, or an `END` that closes no `CASE ... END` opened inside the statement. `SET` is not a stop word (`UPDATE t SET`).
  - `WHILE` -> `WHILE loop`; `WAITFOR` -> `WAITFOR delays the script`; `ALTER TABLE <name> ALTER COLUMN` (`ALTER` `COLUMN` within 8 tokens of `ALTER TABLE`, before a `;`) -> `ALTER COLUMN can rewrite the whole table`.
  - Text of `LargeScriptChars` or more -> `Large script (<n> KB)`, line 1.
- `Line` = 1-based, counted on `\n` (CRLF safe), of the keyword that triggered it.

- [ ] **Step 1: Write the failing tests** (`ScriptAnalyzerTests`; `Analyze(...)`, assert kinds, reason text and lines):
  - Real shapes: `adj - 003`'s guarded `CREATE NONCLUSTERED INDEX IX_Invoices_PublicId ON [dbo].[Invoices] ...` inside `IF NOT EXISTS ... BEGIN ... END` -> one `Index` warning, reason `Creates index IX_Invoices_PublicId`, correct line with CRLF; `ale - 002` shape (`DROP INDEX IF EXISTS [IX_a] ON [dbo].[T];` then `CREATE NONCLUSTERED INDEX [IX_a]` twice) -> exactly 2 `Index` warnings, none for the DROP; `CREATE UNIQUE CLUSTERED INDEX`, `CREATE COLUMNSTORE INDEX`; a plain `CREATE VIEW ...`/`CREATE TABLE ...`/`ALTER TABLE t ADD c int` script -> **no warning**.
  - Ignored text: `-- CREATE INDEX x`, `/* ALTER INDEX a REBUILD */`, `PRINT 'CREATE INDEX x'`, `[CREATE INDEX]` -> none; unterminated string / comment -> no exception.
  - Index extras: `ALTER INDEX ALL ON t REBUILD` and `ALTER INDEX IX_a ON t REBUILD WITH (ONLINE = ON)` -> `Index`; `ALTER INDEX IX_a ON t DISABLE` -> none.
  - Unbounded DML flagged: `UPDATE t SET a = 1`, `DELETE FROM t`, `IF EXISTS (SELECT 1 FROM x) UPDATE t SET a = 1`, `UPDATE t SET a = (SELECT 1 FROM x WHERE x.id = 1)` (the inner WHERE does not count), `UPDATE a SET x = 1 UPDATE b SET y = 2 WHERE z = 1` -> only the first, `UPDATE t SET a = CASE WHEN b = 1 THEN 2 ELSE 3 END SELECT 1 WHERE 1 = 1`-style (CASE...END and the next statement's WHERE do not rescue it).
  - DML not flagged: with `WHERE`; `UPDATE TOP (1000) t SET ...`; `UPDATE #tmp SET ...`, `DELETE FROM @rows`; `CREATE TRIGGER ... AFTER INSERT, UPDATE, DELETE AS`, `FOR UPDATE`, `INSTEAD OF DELETE`, `ON DELETE CASCADE`, `ON UPDATE NO ACTION`, `GRANT DELETE ON t TO r`, `UPDATE STATISTICS t`, `IF UPDATE(col)`, `MERGE ... WHEN MATCHED THEN UPDATE SET ...`, `... THEN DELETE`, `OUTPUT deleted.*`.
  - Other long-running: `WHILE @i < 10`, `WAITFOR DELAY '00:00:05'`, `ALTER TABLE [dbo].[T] ALTER COLUMN c nvarchar(max)` flagged; `ALTER TABLE t ADD c int` not.
  - `Large script`: text of exactly `LargeScriptChars - 1` chars (of `-- x\n` padding) -> none; `LargeScriptChars` -> one `LongRunning` on line 1.
  - Several warnings come back ordered by line; empty text and whitespace -> empty list.
- [ ] **Step 2: Run** `dotnet test tests/DbExplorer.Core.Tests --filter "FullyQualifiedName~ScriptAnalyzerTests"`. Expected: FAIL (compile).
- [ ] **Step 3: Implement** `ScriptAnalyzer` over the token list (word text via `sql.AsSpan(t.Start, t.End - t.Start)`, ordinal-ignore-case compare); keep each rule a small private method; offsets to line via a precomputed newline index.
- [ ] **Step 4: Run** the same command. Expected: PASS; none of the protected files staged.
- [ ] **Step 5: Commit** `feat(upgrade): lexer-based warnings for index creation and long-running statements`.

---

### Task 3: Upgrade runner, rules, batch splitter, isolation tests (Core)

**Files:**
- Create: `src/DbExplorer.Core/Upgrade/UpgradeRules.cs`, `UpgradeRunner.cs`, `UpgradeLog.cs`
- Modify: `src/DbExplorer.Core/Scripting/ScriptSplitter.cs` (only this task touches it)
- Test: `tests/DbExplorer.Core.Tests/Upgrade/SplitBatchesTests.cs`, `UpgradeRulesTests.cs`, `UpgradeRunnerTests.cs`, `UpgradeIsolationTests.cs`

**Interfaces:**
- Produces (`DbExplorer.Core.Upgrade`, reusing `MemberState` from `DbExplorer.Core.Groups`):
  - `public sealed record UpgradeScript(string Name, string Text)`
  - `public sealed record UpgradeRunOptions(int MaxParallel = 4, int CommandTimeoutSeconds = 0)`
  - `public enum ScriptOutcomeState { Done, Failed, Cancelled, Skipped }`
  - `public sealed record ScriptOutcome(string Name, ScriptOutcomeState State, int BatchesRun, TimeSpan Duration, string? Error)`
  - `public sealed record UpgradeResult(ConnectionConfig Connection, MemberState State, IReadOnlyList<ScriptOutcome> Scripts, TimeSpan Duration, string? Error)`
  - `public sealed record UpgradeProgress(Guid ConnectionId, MemberState State, int ScriptIndex, int ScriptCount, string? ScriptName, string? Error)` (`ScriptIndex` is 1-based, 0 while pending)
  - `public sealed class UpgradeRefusedException(string message) : Exception(message)`
  - `public sealed record UpgradeTarget(string Label, IReadOnlyList<ConnectionConfig> Members, ConnectionGroup? Group)`
  - `public static class UpgradeRules { string? Refusal(IReadOnlyList<ConnectionConfig> targets, bool requireSqlServer); IReadOnlyList<UpgradeTarget> TargetsFor(AppConfig cfg, ConnectionConfig current); }`
  - `public sealed class UpgradeRunner(Func<ConnectionConfig, CancellationToken, Task<DbConnection>> openConnection) { void CancelMember(Guid connectionId); Task<IReadOnlyList<UpgradeResult>> RunAsync(IReadOnlyList<ConnectionConfig> targets, IReadOnlyList<UpgradeScript> scripts, UpgradeRunOptions options, IProgress<UpgradeProgress>? progress, CancellationToken ct); }` (the runner owns and disposes each opened connection; one run at a time per instance).
  - `public static class UpgradeLog { string Format(string label, IReadOnlyList<UpgradeResult> results); }`
  - `ScriptSplitter.SplitBatches(string script) : IReadOnlyList<ScriptStatement>` and `ScriptSplitter.FindGoRepeat(string script) : int?` (1-based line of the first `GO n` with n > 1).
- Consumes: `ScriptStatement`, `SqlLexer`, `GroupRules.GroupsOf/MembersOf`, `ConnectionConfig`.

Behavior contract:
- `Refusal` (null = fine), checked in this order: no targets -> `There is no database to upgrade.`; any `ReadOnly` -> `Read-only connections cannot be upgraded: A, B. Untick Read-only in the connection's menu first; nothing was run.`; mixed engines -> message; `requireSqlServer` and any non-SQL Server -> `Upgrade scripts are T-SQL: only SQL Server databases can be upgraded (not: X (Postgres)).`
- `TargetsFor`: first `new UpgradeTarget($"This database ({current.Name})", [current], null)`, then for each group of `current` (config order) with `MembersOf(...).Count >= 2`: `new UpgradeTarget($"Group {g.Name} ({n} databases)", members, g)`.
- `RunAsync`: throws `UpgradeRefusedException` before opening anything for: `Refusal(targets, requireSqlServer: false)`, no scripts, or any script with `FindGoRepeat != null` (names script and line). Otherwise per database (at most `MaxParallel` at once, results in target order): open connection (cancellable even if the delegate ignores the token; a late connection is disposed), then for each script in order report `Running(index, count, name)`, run each batch of `SplitBatches(text)` with `ExecuteNonQueryAsync(token)` and `CommandTimeout = CommandTimeoutSeconds`, **no transaction**. First failing batch: outcome `Failed` with `batch k of n: <message>`, remaining scripts `Skipped`, database `Failed` with `<script>: batch k of n: <message>`; other databases go on. Cancellation (all or one member, also while pending or connecting) -> that database `Cancelled`, current script `Cancelled`, rest `Skipped`; never throws. A script with no batch (empty, comment-only) is `Done` with 0 batches. An exception from the connection factory -> `Failed` with its message.
- `UpgradeLog.Format`: header line `label`, then per database `== <name>: <state> (<n> of <m> scripts, <s> s)`, then one line per script `  [OK|FAILED|CANCELLED|SKIPPED] <name>` plus ` : <error>` for failed.

- [ ] **Step 1: Write the failing tests.**
  - `SplitBatchesTests`: `Split_GoOnly_KeepsSemicolonsInsideTheBatch` with the real shape `IF NOT EXISTS (SELECT 1) BEGIN\n DROP INDEX IF EXISTS [IX_a] ON [dbo].[T];\n CREATE NONCLUSTERED INDEX [IX_a] ON [dbo].[T] ([c]);\nEND\nGO\nSELECT 3` -> exactly 2 batches, the first still containing both `;`; `go` lowercase and `GO -- comment` split; `GO` inside a string, a `/* */` comment or a `-- ` comment does not; no trailing newline after the last `GO`; empty and comment-only scripts -> 0 batches; CRLF; `[GO]` bracket identifier does not split. `FindGoRepeat("SELECT 1\nGO 5\n")` -> 2, `GO 1` and `GO` -> null.
  - `UpgradeRulesTests`: every `Refusal` branch (read-only message names both connections; Postgres refused only with `requireSqlServer: true`), `TargetsFor` (connection in no group -> 1 target; in a 1-member group -> still 1; in two groups -> 3, config order, `Group` set).
  - `UpgradeRunnerTests` (SQLite files like `GroupRunnerTests`: open with `new Session(cfg, new SqliteDriver(), [], null).OpenConnectionAsync(ct)`; each file has table `u(a INTEGER)`):
    - `Run_PlaysScriptsInOrder_AllBatches_OnEveryTarget`: 3 targets, script 1 `INSERT INTO u VALUES (1)\nGO\nINSERT INTO u VALUES (2)`, script 2 `INSERT INTO u VALUES (3)` -> every file holds `[1,2,3]`, all `Done`, outcomes `Done`/`BatchesRun` 2 and 1, progress per target goes Pending -> Running(1 of 2) -> Running(2 of 2) -> Done.
    - `Run_FirstError_StopsThatDatabaseOnly`: target 2 lacks table `marker`; script 2 is `INSERT INTO marker VALUES (1)`, script 3 any -> target 2 `Failed`, error contains the script name and `batch 1 of 1`, script 3 `Skipped`; the others `Done` and ran script 3.
    - `Run_NoTransactionWrapper`: batch 1 inserts a row, batch 2 fails -> the row from batch 1 is still in the file.
    - `Run_ScriptsWithSemicolonsInsideABlock_RunAsOneBatch` using SQLite `INSERT INTO u VALUES (1); INSERT INTO u VALUES (2);` as one batch -> both rows present.
    - `Run_LargeResultSet_IsNeitherTruncatedNorCancelled`: batch 1 `WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < 5000) SELECT i FROM n`, batch 2 `INSERT INTO u VALUES (9)` -> `Done`, row 9 exists.
    - `Run_RunsAtMostMaxParallelDatabases`: 6 targets, factory records concurrency, default options -> peak 4; `MaxParallel: 2` -> peak 2.
    - `Run_CommandTimeout_IsAppliedToEveryCommand`: factory returns the existing `FakeDbConnection` (tests/DbExplorer.Core.Tests/FakeDb.cs) with `CommandTimeoutSeconds: 7` -> every `Commands[i].CommandTimeout == 7`; default options -> 0.
    - `Run_CancelOneMember_WhileConnecting_OthersGo`: factory for target 2 awaits `Task.Delay(Timeout.Infinite, ct)`; `CancelMember` -> target 2 `Cancelled`, others `Done`, no exception. `Run_CancelAll_*`: all pending/connecting end `Cancelled`. `Run_CancelBetweenScripts`: a progress handler that cancels when it sees `Running` script 2 -> script 1 `Done`, script 2 `Cancelled`/`Skipped` per contract, database `Cancelled`, data of script 1 kept.
    - `Run_Refuses_ReadOnlyTarget_BeforeOpeningAnything` (factory call count 0, message names the connection); `Run_Refuses_NoScripts`, `Run_Refuses_GoRepeat` (names the script and line, 0 connects); `Run_FactoryThrows_MarksThatDatabaseFailed` (e.g. the "became read-only" exception the view model's factory throws).
    - `Run_DisposesEveryConnection` (a tracking `DbConnection` wrapper or `FakeDbConnection.State == Closed` after the run).
  - `UpgradeLogTests`: exact text of a small done/failed/skipped result set.
  - `UpgradeIsolationTests` (the read-only guarantee of the existing group run, in its own file):
    - `[Theory] GroupRun_StillRefusesEveryUpgradeStyleWrite_AndConnectsToNothing` with `IF NOT EXISTS (SELECT 1 FROM t) BEGIN CREATE NONCLUSTERED INDEX IX_t_name ON t(name) END`, `CREATE INDEX IX_t_name ON t(name)`, `ALTER TABLE t ADD c int`, `UPDATE t SET name = 'x'`: `new GroupRunner(factory).RunAsync(...)` throws `GroupRunRefusedException`, the factory is never called, the progress sink stays empty (copy the arrangement of `GroupRunnerTests.Run_RefusesAnyWriteStatement_BeforeRunningAnything`).
    - `GroupRunner_IsSealed_AndTheUpgradeRunnerIsNotOne`: `typeof(GroupRunner).IsSealed` and `!typeof(GroupRunner).IsAssignableFrom(typeof(UpgradeRunner))`.
    - `GroupRun_ThroughRunScriptOnGroupAsync_StillRefuses` lives in Task 6's App tests (see there).
- [ ] **Step 2: Run** `dotnet test tests/DbExplorer.Core.Tests --filter "FullyQualifiedName~SplitBatchesTests|FullyQualifiedName~UpgradeRulesTests|FullyQualifiedName~UpgradeRunnerTests|FullyQualifiedName~UpgradeLogTests|FullyQualifiedName~UpgradeIsolationTests"`. Expected: FAIL (compile).
- [ ] **Step 3: Implement.** `SplitBatches`: add a `semicolons` flag to the private `SplitCore` (default true, so `Split` and `SplitAtBlankLines` are unchanged: their existing tests are the regression net) and make a `;` token an ordinary token when false; lex with `SqlLexOptions.For(DbKind.SqlServer)`. `FindGoRepeat`: reuse the GO-line recognition (`IsGoSeparator`) and read the digits. `UpgradeRunner`: per-member `CancellationTokenSource` linked to the run token (copy the slot idea of `GroupRunner`, do not edit it); `SemaphoreSlim(MaxParallel)`; `connecting.WaitAsync(token)` with late-connection disposal. Batch execution: `await using var cmd = connection.CreateCommand(); cmd.CommandText = batch.Text; cmd.CommandTimeout = options.CommandTimeoutSeconds; await cmd.ExecuteNonQueryAsync(token)`; map `catch (Exception) when (token.IsCancellationRequested)` to Cancelled.
- [ ] **Step 4: Run** the Step 2 command, then the whole Core suite `dotnet test tests/DbExplorer.Core.Tests`. Expected: all PASS (existing `ScriptSplitter*` tests included). `git diff --cached --stat -- src/DbExplorer.Core/Groups src/DbExplorer.Core/Scripting/ReadOnlyGuard.cs src/DbExplorer.Core/Scripting/ScriptRunner.cs` (staged files only) prints nothing; this task stages `ScriptSplitter.cs` and nothing else under `Scripting/`.
- [ ] **Step 5: Commit** `feat(upgrade): separate write-capable runner, GO-only batch splitter, group-run isolation tests`.

---

### Task 4: `DbUpdatePath` setting and Options dialog (Core model + App)

**Files:**
- Modify: `src/DbExplorer.Core/Model/AppConfig.cs`, `src/DbExplorer.Core/Config/ConfigStore.cs` (the `normalized` copy in `Load`), `src/DbExplorer.App/ViewModels/ConnectionTreeViewModel.cs`
- Create: `src/DbExplorer.App/ViewModels/OptionsViewModel.cs`, `src/DbExplorer.App/Views/OptionsDialog.axaml`, `OptionsDialog.axaml.cs`
- Test: `tests/DbExplorer.Core.Tests/Upgrade/DbUpdatePathSettingTests.cs`, `tests/DbExplorer.App.Tests/Upgrade/OptionsViewModelTests.cs`, `tests/DbExplorer.App.Tests/Upgrade/OptionsDialogTests.cs`, `tests/DbExplorer.App.Tests/Upgrade/DbUpdatePathTreeTests.cs`

**Interfaces:**
- Produces:
  - `AppConfig.DbUpdatePath { get; set; } : string?` (default `null`; plain property like `AutoUpdateCheck`).
  - `ConnectionTreeViewModel.SetDbUpdatePath(string? path) : bool` (same contract as `SetAutoUpdateCheck`: applies in memory, saves unless persistence is off, status-bar error when not saved; extract the shared save block into a private helper used by both, `SetAutoUpdateCheck` behavior unchanged).
  - `public sealed record OptionsResult(string? DbUpdatePath)`
  - `public sealed partial class OptionsViewModel : ObservableObject { OptionsViewModel(string? currentDbUpdatePath, Func<string, string?> validate, Func<string, string?>? describe = null); string DbUpdatePath {get;set;}; string? PathError; string? PathInfo; bool HasPathError; void ValidatePath(); bool TryConfirm(); OptionsResult? Result; }` (`validate` returns an error message or null; `describe` returns the optional info line for a valid path, e.g. `147 version folders found`. Both are injected, so this task has no compile dependency on Task 1; Task 7 passes `DbUpdateCatalog.Validate` and a describe delegate that counts the version folders).
- Consumes: nothing from the other tasks.

- [ ] **Step 1: Write the failing tests.**
  - Core `DbUpdatePathSettingTests` (copy the three cases of `AutoUpdateCheckSettingTests` for `DbUpdatePath`): default `null` for `AppConfig.Empty()` and no file; an old file without the key loads `null`; set value survives `Save` -> `Load` (this fails until `ConfigStore.Load` copies it into `normalized`); a `Config with { Folders = ... }` copy keeps it.
  - `OptionsViewModelTests` (fake validator): blank path -> `TryConfirm()` true, `Result.DbUpdatePath == null`, no error (feature off); valid -> `Result` = trimmed path; validator message -> `PathError` set, `Result` null, `TryConfirm()` false; text edit clears `PathError`; whitespace-only counts as blank; `ValidatePath` does not call the validator for blank text; `PathInfo` comes from `describe` for a valid path and is null for an invalid one.
  - `DbUpdatePathTreeTests` (uses `TreeFixture`): `SetDbUpdatePath` writes `config.json` and returns true; with `persistenceEnabled: false` returns false, changes memory and reports "NOT saved" in `Status`; a failing save (config path is a directory) returns false and reports; `SetAutoUpdateCheck` tests of the existing suite still pass.
  - `OptionsDialogTests` (`[AvaloniaFact]`): window with the VM shows the path TextBox bound to `DbUpdatePath`, a "Browse..." button, OK/Cancel; with an invalid path the error banner is visible after `ValidatePath()` and OK does not close (`DialogResult`-style: use `Closed` flag).
- [ ] **Step 2: Run** `dotnet test tests/DbExplorer.Core.Tests --filter "FullyQualifiedName~DbUpdatePathSettingTests"` and `dotnet test tests/DbExplorer.App.Tests --filter "FullyQualifiedName~OptionsViewModelTests|FullyQualifiedName~DbUpdatePathTreeTests|FullyQualifiedName~OptionsDialogTests"`. Expected: FAIL (compile).
- [ ] **Step 3: Implement** the model/store/tree changes and the VM. **Layout of `OptionsDialog`** (same look as `GroupEditorDialog`: `DockPanel`, footer band `DbxWindowBg` with `OK` (accent, `IsDefault`) and `Cancel` (`IsCancel`), header band `DbxPanelHeaderBg` with `IconSettings` and title "Options"; size 640 x 300, `CanResize` false): body on `DbxContentBg`, a section label `DATABASE UPDATES` (class `section`), a caption "Folder with the dbupdate scripts of the repository (its dbupdate folder). Leave empty to switch the upgrade feature off.", then `Grid ColumnDefinitions="*,Auto"`: `TextBox` (`Watermark` `C:\repos\app\dbupdate`, `LostFocus` -> `ValidatePath`) and a `Browse...` button (code-behind: `StorageProvider.OpenFolderPickerAsync`, sets `DbUpdatePath`, calls `ValidatePath`); an error banner (`Classes="banner error"`, `IconError`) bound to `PathError`; an info line (`PathInfo`, e.g. `147 version folders found`). OK calls `TryConfirm()` and `Close(true)`; Cancel `Close(false)`.
- [ ] **Step 4: Run** the Step 2 commands plus `dotnet test tests/DbExplorer.Core.Tests` and `dotnet test tests/DbExplorer.App.Tests`. Expected: PASS.
- [ ] **Step 5: Commit** `feat(upgrade): DbUpdatePath setting and Options dialog`.

---

### Task 5: Plan builder and text decoding (Core, after T1 and T2)

**Files:**
- Create: `src/DbExplorer.Core/Upgrade/SqlTextDecoder.cs`, `src/DbExplorer.Core/Upgrade/UpgradePlan.cs`
- Modify: `src/DbExplorer.App/ViewModels/ScriptFileEncoding.cs` (`Decode` delegates to Core; `Encode` and `NewFile` stay)
- Test: `tests/DbExplorer.Core.Tests/Upgrade/SqlTextDecoderTests.cs`, `UpgradePlannerTests.cs`

**Interfaces:**
- Consumes (Tasks 1, 2, 3): `DbUpdateCatalog`, `DbUpdateFolder`, `DbUpdateScript`, `ScriptAnalyzer`, `ScriptWarning`, `ScriptSplitter.FindGoRepeat`.
- Produces:
  - `public static class SqlTextDecoder { public static (string Text, Encoding Encoding) Decode(byte[] bytes); }` (the body moved verbatim from `ScriptFileEncoding.Decode`: BOM UTF-8/16/32 stripped, else strict UTF-8, else Windows-1252; registers `CodePagesEncodingProvider`).
  - `public sealed record UpgradeScriptItem(DbUpdateScript Script, string? Text, string? LoadError, IReadOnlyList<ScriptWarning> Warnings) { string DisplayName => $"{Script.FolderName}/{Script.FileName}"; }`
  - `public sealed record UpgradePlan(string Label, string ConfirmToken, IReadOnlyList<UpgradeScriptItem> Items, IReadOnlyList<string> Ignored)`
  - `public static class UpgradePlanner { UpgradePlan Build(string label, string confirmToken, IReadOnlyList<DbUpdateFolder> folders); }` (reads each script with `File.ReadAllBytes`, a read failure or a `GO n` becomes `LoadError` on that item, never an exception; `Ignored` = `folder/name` of every `DbUpdateFolder.Ignored` entry).

- [ ] **Step 1: Write the failing tests.**
  - `SqlTextDecoderTests`: UTF-8 with BOM -> text has no `\uFEFF`; UTF-8 without BOM; UTF-16 LE/BE BOM; bytes of `"é"` in cp1252 (`0xE9`, invalid UTF-8) -> `"é"`; pure ASCII.
  - `UpgradePlannerTests` (temp tree, real files): items in catalog order (use `v0860-sprint 143` + `v0861-sprint 143`: v0860's natural order first, then v0861); a file written with a BOM and `\r\n` has `Text` without the BOM; a cp1252 accent survives; warnings come from the analyzer (an `adj - 003` style guarded `CREATE NONCLUSTERED INDEX` script -> one `Index` warning, a view script -> none); a script containing `GO 3` -> `LoadError` mentioning the line, `Text` still null; a locked file (open with `FileShare.None`) -> `LoadError`, other items fine; an empty folder -> empty `Items`; `Ignored` lists `v0860-sprint 143/fbo - 001 - Delete Obsole Sync Triggers`.
  - App regression: `dotnet test tests/DbExplorer.App.Tests` (open/save script file tests) stays green after the delegation.
- [ ] **Step 2: Run** `dotnet test tests/DbExplorer.Core.Tests --filter "FullyQualifiedName~SqlTextDecoderTests|FullyQualifiedName~UpgradePlannerTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.** Move `Decode` (and its private helpers `Windows1252`, `Strip`, the provider registration) to `SqlTextDecoder`; make `ScriptFileEncoding.Decode` a one-line call to it.
- [ ] **Step 4: Run** Core + App suites. Expected: PASS; none of the protected files staged.
- [ ] **Step 5: Commit** `feat(upgrade): plan builder with encoding-safe script loading`.

---

### Task 6: Upgrade view model and dialog (App, after T3 and T5)

**Files:**
- Create: `src/DbExplorer.App/ViewModels/UpgradeViewModel.cs` (also `UpgradeScriptRow`, `UpgradeWarningLine`, `UpgradeMemberViewModel`, `UpgradePhase`), `src/DbExplorer.App/Views/UpgradeDialog.axaml`, `UpgradeDialog.axaml.cs`
- Test: `tests/DbExplorer.App.Tests/Upgrade/UpgradeViewModelTests.cs`, `UpgradeDialogTests.cs`, `UpgradeGroupRunIsolationTests.cs`, `TempDbUpdateTree.cs` (App-side copy of the tiny fixture helper, 15 lines, building the same `Standard()` tree)

**Interfaces:**
- Consumes: `DbUpdateCatalog`, `DbUpdateFolder`, `UpgradePlanner`, `UpgradePlan`, `UpgradeScriptItem`, `UpgradeTarget`, `UpgradeRules`, `UpgradeRunner`, `UpgradeRunOptions`, `UpgradeResult`, `UpgradeProgress`, `UpgradeLog`, `ScriptWarning`, `MemberState`; existing `SafeProgress<T>` (internal, `GroupRunViewModel.cs`), `ErrorText.Describe`.
- Produces:
  - `public enum UpgradePhase { Setup, Preview, Confirm, Running, Finished }`
  - `public sealed partial class UpgradeScriptRow : ObservableObject { string Name; string Folder; bool IsProduction; string? ProductionTag; [ObservableProperty] bool IsSelected; IReadOnlyList<ScriptWarning> Warnings; string WarningText; string? LoadError; bool CanSelect; }`
  - `public sealed record UpgradeWarningLine(string ScriptName, ScriptWarning Warning) { string Text; }`
  - `public sealed partial class UpgradeMemberViewModel : ObservableObject { Guid Id; string Name; MemberState State; int ScriptIndex; int ScriptCount; string? ScriptName; string? Error; string StatusText; bool CanCancel; IRelayCommand CancelCommand; }` (`StatusText`: `waiting`, `running: script 3 of 12 - <name>`, `done - 12 scripts - 4.2 s`, `failed at <script>: <error>`, `cancelled`)
  - `public sealed partial class UpgradeViewModel : ObservableObject` with constructor `UpgradeViewModel(DbUpdateCatalog catalog, IReadOnlyList<UpgradeTarget> targets, Func<Guid, CancellationToken, Task<Session?>> resolveSession, Func<string, Task>? copyText = null, bool requireSqlServer = true)`; properties `Phase`, `SprintText`, `FolderChoices` (newest first, label `v0860-sprint 143 (27 scripts)`), `SelectedFolder`, `Targets`, `SelectedTarget`, `Message`, `Rows`, `IgnoredNote`, `WarningSummary`, `SelectedWarnings`, `ConfirmInput`, `ConfirmToken`, `NeedsTypedConfirmation`, `Members`, `Log`, `CanClose`; commands `LoadCommand` (async), `RequestRunCommand`, `BackCommand`, `ConfirmRunCommand` (async), `CancelAllCommand`, `CopyLogCommand`. `internal static TimeSpan CancelGrace { get; set; } = TimeSpan.FromSeconds(5)`.

Behavior contract:
- `SprintText` change clears `SelectedFolder`; choosing a folder clears `SprintText`. `Load`: by folder -> that one folder (`ConfirmToken` = `Code`, e.g. `v0691`); else parse `SprintText` (digits only, 1..int.MaxValue after trim; otherwise `Message` = `Enter a sprint number, for example 143.`): found -> all folders of the sprint (`ConfirmToken` = number as text); unknown -> `Sprint 999 was not found. Nearest sprints: 146, 147.` (or `No sprint folders found.`); found with 0 scripts -> `Sprint N has no scripts.`. Plan is built with `Task.Run`. Rows: every script, `IsSelected = !IsProduction && LoadError == null`; `Production<n>` label shown; rows with `LoadError` disabled. `IgnoredNote` = `Ignored (not a .sql file): a, b` when any. `WarningSummary` counts warnings of the **selected** rows (`3 warnings: 2 index, 1 long-running`, empty when none) and follows ticking; `SelectedWarnings` lists them all.
- `RequestRun` (only in Preview, at least one selected row, no row with `LoadError`; otherwise `Message` says why): `UpgradeRules.Refusal(SelectedTarget.Members, requireSqlServer)` non-null -> `Message` = that text, stay in Preview, **no session resolved, nobody contacted**; else `Phase = Confirm`. `NeedsTypedConfirmation` = `SelectedTarget.Group != null`; `ConfirmRunCommand` enabled when not needed or `ConfirmInput.Trim() == ConfirmToken`. `Back` returns Confirm -> Preview.
- `ConfirmRun`: `Phase = Running`; `Members` one per target (names made unique like `GroupRunViewModel.UniqueNames`); starts one `resolveSession` per member **on the calling (UI) thread**, then `Task.Run(runner.RunAsync(...))` with a `SafeProgress<UpgradeProgress>` created on the UI thread; the runner's factory awaits that member's resolve with `WaitAsync(token)`, throws `InvalidOperationException($"{name} no longer exists.")` for a null session and `InvalidOperationException($"{name} is read-only: not run.")` when `session.Config.ReadOnly`, else `await session.OpenConnectionAsync(token)`. On completion (authoritative): members take the result, `Log = UpgradeLog.Format(...)`, `Phase = Finished`, `Message` = `3 of 4 databases done, 1 failed.`. `UpgradeRefusedException` -> back to Preview with the message. Any other exception -> `Finished` with `Message`. `CancelAll` cancels the run token and waits `CancelGrace`; members that have not ended read `cancelled`, late progress is ignored. A member's `Cancel` calls `runner.CancelMember` and shows `cancelled` at once. `CanClose` is false while `Running`. `CopyLog` calls `copyText(Log)`.

**Layout of `UpgradeDialog`** (`Window`, 900 x 640, `MinWidth` 640, `MinHeight` 420, `CenterOwner`; same look as `SchemaDiffDialog`: header band `DbxPanelHeaderBg` with `IconDatabase` and "Upgrade database", footer band `DbxWindowBg`, content on `DbxContentBg`; no code-built dialog):
- Top block (visible in Setup/Preview): row 1 `Sprint` label, `TextBox` (width 90, `Enter` runs `LoadCommand`, watermark `143`), `Load` button, `or folder` label, `ComboBox` over `FolderChoices` (`MinWidth` 320); row 2 `Run on` label and `ComboBox` over `Targets` (shown when more than one target).
- `Message` as a banner (`Classes="banner warn"`, `IconWarning`); `WarningSummary` as a second warn banner (`IconWarning`); `IgnoredNote` as an info banner (`IconInfo`).
- Preview list (Preview): `ScrollViewer` + `ItemsControl` of rows, each `Grid ColumnDefinitions="Auto,*,Auto,Auto"`: `CheckBox` bound to `IsSelected` (`IsEnabled` = `CanSelect`), script name (mono font `DbxMonoFont`, folder in a `dim small` second line), a `Production<n>` badge (`Classes="badge warn"`), one `badge warn` per warning (kind as text, reason and line as tooltip), and the `LoadError` in red text.
- Confirm (Confirm phase, replaces the list): heading `Upgrade N script(s) on:`, the list of target databases, `Production` scripts count if any ticked, every line of `SelectedWarnings` (`script: Kind - reason (line n)`), and for a group a `TextBox` `Type <ConfirmToken> to confirm` bound to `ConfirmInput`.
- Running/Finished: one row per `Members` item (state icon like the group status panel: `IconSchedule`, `IconRefresh`, `IconCheck`, `IconError`, `IconStop`), name, `StatusText`, per-row Cancel button (`CanCancel`), `Cancel all` while running, `Copy log` after the run.
- Footer buttons by phase: Setup: `Close`; Preview: `Run...` (accent, `RequestRunCommand`) and `Close`; Confirm: `Run` (accent, `ConfirmRunCommand`) / `Back`; Running: `Cancel all`, Close disabled; Finished: `Copy log`, `Close` (accent). The X button during a run runs `CancelAll` and does not close (`Closing` handler sets `e.Cancel` while `!CanClose`).

- [ ] **Step 1: Write the failing tests.**
  - `UpgradeViewModelTests` (temp tree `Standard()`, SQLite databases, `resolveSession` returning `new Session(cfg, new SqliteDriver(), [], null)`, `requireSqlServer: false` except where stated; scripts in the tree write to table `u(a INTEGER)`):
    - Review Focus 1 `Load_InvalidSprintText_GivesAMessage_NoThrow_NothingResolved` as a `[Theory]` over `""`, `"abc"`, `"-5"`, `"0"`, `"99999999999"`, `"1.5"`; ` 143 ` loads sprint 143; unknown `999` -> message with nearest sprints; sprint whose folder is empty -> `has no scripts`; resolver call count 0 in all.
    - `Load_Sprint143_RowsInOrder_ProductionUnchecked_WarningsCounted` (index script -> summary `1 warning: 1 index`; unticking it clears the summary), `Load_ByFolder_UsesOnlyThatFolder`, `Load_FolderWithoutSprint_SelectableByFolderOnly` (`V0121-change brandvariant`, `ConfirmToken == "V0121"`), `Load_ShowsIgnoredFilesNote`, `Load_ScriptWithLoadError_DisablesRowAndBlocksRun`.
    - `RequestRun_ReadOnlyTarget_RefusedBeforeAnySessionIsResolved` (message names the connection, `Phase` stays Preview, resolver count 0); group with one read-only member -> whole run refused; `requireSqlServer: true` with a SQLite/Postgres target -> refused with the engine message; nothing selected -> message.
    - `Confirm_SingleDatabase_NoTyping`, `Confirm_Group_RequiresTypedSprint` (`ConfirmRunCommand.CanExecute` false until `ConfirmInput == "143"`, true for ` 143 `, false for `142`), `Back_ReturnsToPreview`.
    - `Run_Group_PlaysSelectedScriptsOnEveryDatabase`: 3-member group, scripts really change the SQLite files; `Members` end `Done`; `Phase == Finished`; `Log` contains each database and `[OK]`; unticked scripts did not run.
    - `Run_OneDatabaseFails_OthersFinish_SummaryNamesIt`; `Run_FactoryRecheck_SessionBecameReadOnly_FailsThatMemberOnly` (resolver returns a session whose config has `ReadOnly: true` while the snapshot said false; file unchanged); Review Focus 5 `CancelAll_WhileConnecting_EndsCancelled_AndReleasesEverything` (resolver that never completes: after `CancelAll`, within `CancelGrace` set to 100 ms all members read `cancelled`, `Phase` Finished, `CanClose` true) and `CancelMember_OnlyThatMember`; `CanClose_FalseWhileRunning`; `CopyLog_CallsTheDelegateWithTheLog`.
    - Resolves start on the calling thread (assert in the resolver delegate that the thread id equals the test's).
  - `UpgradeDialogTests` (`[AvaloniaFact]`, window shown with a VM over a temp tree): type `143` in the sprint box and `LoadCommand` -> rows with the expected script names are in the visual tree and the `Production2` badge text is present; an invalid sprint shows the message banner; `RequestRun` on a group target shows the typed-confirmation box and the Run button stays disabled until the token is typed; pressing the window close while `Running` does not close it.
  - `UpgradeGroupRunIsolationTests`: through the real script tab, `RunScriptOnGroupAsync` with the text of a guarded `CREATE NONCLUSTERED INDEX` script on a 2-member SQLite group is refused (`GroupRun.Notice` equals `GroupRunViewModel.ReadOnlyNotice`, no member ran, files unchanged). Build it from `ShellTestBase`/existing group tab tests (see `GroupRunViewModelTests`/`GroupSwitchTests` for the arrangement).
- [ ] **Step 2: Run** `dotnet test tests/DbExplorer.App.Tests --filter "FullyQualifiedName~Tests.Upgrade"`. Expected: FAIL (compile).
- [ ] **Step 3: Implement** the view model and the view (compiled bindings: `x:DataType="vm:UpgradeViewModel"`, nested `DataTemplate x:DataType`s). Reuse theme tokens only (`DbxWindowBg`, `DbxContentBg`, `DbxPanelHeaderBg`, `DbxBorder`, `DbxAccent`, `DbxTextDim`, `DbxMonoFont`) and icons already in `Styles/Icons.axaml`.
- [ ] **Step 4: Run** the Step 2 command, then `dotnet test tests/DbExplorer.App.Tests`. Expected: PASS; none of the protected files staged.
- [ ] **Step 5: Commit** `feat(upgrade): Upgrade dialog view model with preview, confirmation and per-database status`.

---

### Task 7: Gear menu, commands and wiring (App, after T4 and T6)

**Files:**
- Create: `src/DbExplorer.App/ViewModels/IUpgradeDialogs.cs`, `src/DbExplorer.App/UpgradeDialogService.cs`, `src/DbExplorer.App/ViewModels/MainViewModel.Upgrade.cs`
- Modify: `src/DbExplorer.App/Views/MainWindow.axaml` (gear flyout, lines 148-156), `MainWindow.axaml.cs` (none expected), `src/DbExplorer.App/App.axaml.cs`, `tests/DbExplorer.App.Tests/Updates/UpdateWindowTests.cs`
- Test: `tests/DbExplorer.App.Tests/Upgrade/MainUpgradeTests.cs`, `tests/DbExplorer.App.Tests/Upgrade/SettingsMenuUpgradeTests.cs`, `tests/DbExplorer.App.Tests/Upgrade/FakeUpgradeDialogs.cs`

**Interfaces:**
- Consumes: `OptionsViewModel`, `OptionsResult`, `ConnectionTreeViewModel.SetDbUpdatePath` (T4); `UpgradeViewModel`, `UpgradeRules.TargetsFor`, `DbUpdateCatalog.Scan/Validate` (T1, T3, T6); `MainViewModel.ScriptTarget()` and `ResolveScriptSessionAsync` (both private, same class: that is why the command lives in `MainViewModel`).
- Produces:
  - `public interface IUpgradeDialogs { Task<bool> ShowOptionsAsync(OptionsViewModel viewModel); Task ShowUpgradeAsync(UpgradeViewModel viewModel); }` and `public sealed class UpgradeDialogService(Window owner) : IUpgradeDialogs` (`new OptionsDialog { DataContext = vm }.ShowDialog<bool>(owner)`, `new UpgradeDialog { DataContext = vm }.ShowDialog(owner)`).
  - In `MainViewModel` (partial file): `void UseUpgrade(IUpgradeDialogs dialogs)`; `[ObservableProperty] bool UpgradeAvailable` (starts false; **no disk access at construction**); `void RefreshUpgradeAvailability()` (`DbUpdateCatalog.Validate(Tree.Config.DbUpdatePath) == null`, then `OpenUpgradeCommand.NotifyCanExecuteChanged()`); `[RelayCommand] Task OpenOptionsAsync()`; `[RelayCommand(CanExecute = nameof(UpgradeAvailable))] Task OpenUpgradeAsync()`; `Func<string, Task>? CopyToClipboard { get; set; }` (set by `App.axaml.cs` from the window's clipboard).

Behavior contract:
- `OpenOptionsAsync`: `new OptionsViewModel(Tree.Config.DbUpdatePath, DbUpdateCatalog.Validate, path => $"{DbUpdateCatalog.Scan(path).Folders.Count} version folders found")` (the describe call runs only for a valid path, on OK/Browse/focus loss); on OK `Tree.SetDbUpdatePath(vm.Result.DbUpdatePath)` then `RefreshUpgradeAvailability()`; cancel changes nothing. Never throws (status-bar error).
- `OpenUpgradeAsync`: no connection (`ScriptTarget()` null) -> `Status.Info("Select a connection in the tree or open a tab first, then choose Upgrade database.")`; `DbUpdateCatalog.Validate` error -> `Status.Error`, `UpgradeAvailable = false`; else `Scan` on the pool under `Status.Busy("Reading the dbupdate folder...")`, `targets = UpgradeRules.TargetsFor(Tree.Config, current)`, `new UpgradeViewModel(catalog, targets, ResolveScriptSessionAsync, CopyToClipboard)`, `await dialogs.ShowUpgradeAsync(vm)`. Never throws.
- Gear flyout items, in order: `Upgrade database...` (`Command="{Binding OpenUpgradeCommand}"`, `IsEnabled` follows `CanExecute`), `Options...` (`OpenOptionsCommand`), a `Separator`, then the existing `Check for updates automatically`. The `MenuFlyout` gets `Opening="OnSettingsFlyoutOpening"` whose handler calls `main.RefreshUpgradeAvailability()` (the only place besides Options-OK that touches the disk, on demand). `App.axaml.cs` gets one line: `main.UseUpgrade(new UpgradeDialogService(window));` plus the clipboard delegate; nothing else at startup.

- [ ] **Step 1: Write the failing tests.**
  - `MainUpgradeTests` (`ShellTestBase`/`TreeFixture`, `FakeUpgradeDialogs` recording the view models and returning scripted results): `UpgradeAvailable_IsFalseAtConstruction_EvenWithAValidPath` (Review: nothing validated at launch); `RefreshUpgradeAvailability` true for a valid temp tree, false for blank, missing folder, a file; `OpenOptions_Ok_SavesPathInConfigJson_AndEnablesUpgrade`; `OpenOptions_Cancel_ChangesNothing`; `OpenOptions_InvalidPath_NeverSaved` (the dialog fake calls `TryConfirm` with the real validation: a missing folder and a plain file are refused, a temp tree is accepted and its `PathInfo` counts the folders); `OpenUpgrade_NoConnection_SaysSo_AndOpensNoDialog`; `OpenUpgrade_InvalidPath_ReportsAndDisables`; `OpenUpgrade_BuildsTargets_ThisDatabaseThenItsGroups` (captured `UpgradeViewModel.Targets`); `OpenUpgrade_UsesTheActiveTabsConnection`.
  - `SettingsMenuUpgradeTests` (`[AvaloniaFact]`, pattern of `UpdateWindowTests`): the gear flyout contains `Upgrade database...`, `Options...` and `Check for updates automatically` in that order; `Upgrade database...` is disabled with no path, and enabled after the flyout opens with a valid path (`settings.Flyout.ShowAt(settings)`); clicking `Options...` calls `ShowOptionsAsync` once.
  - `UpdateWindowTests.SettingsMenu_TogglesTheAutomaticCheck`: change `Items.Single()` to select the item by `Header == "Check for updates automatically"` (the existing assertions stay).
- [ ] **Step 2: Run** `dotnet test tests/DbExplorer.App.Tests --filter "FullyQualifiedName~MainUpgradeTests|FullyQualifiedName~SettingsMenuUpgradeTests|FullyQualifiedName~UpdateWindowTests"`. Expected: FAIL (compile / `Single()` after the menu change).
- [ ] **Step 3: Implement** interface, service, partial view model, menu XAML, `App.axaml.cs` line, `UpdateWindowTests` fix.
- [ ] **Step 4: Run** the whole solution `dotnet test DbExplorer.sln`. Expected: all PASS. Then `dotnet build DbExplorer.sln -c Release` has no warnings from the new files. `git diff --cached --stat -- src/DbExplorer.Core/Groups src/DbExplorer.Core/Scripting/ReadOnlyGuard.cs src/DbExplorer.Core/Scripting/ScriptRunner.cs` (staged files only) prints nothing. Optional startup check (the spec forbids slowing startup): `pwsh scripts/measure-startup.ps1` (needs a publish; compare with the numbers in README "Startup performance", expected within noise of ~300 ms).
- [ ] **Step 5: Commit** `feat(upgrade): gear menu entries, Options and Upgrade commands`.

---

### Task 8: README section (independent; append-only)

**Files:**
- Modify: `README.md` (**append-only, targeted edits**: another agent is editing the installer text of the same file at the same time)

- [ ] **Step 1:** Re-read `README.md` immediately before editing. Never rewrite the file: use `Edit` calls anchored on the exact heading line `## Configuration and data` and insert the new section **above** it; if the anchor changed, re-read and re-anchor.
- [ ] **Step 2: Insert section `## Database upgrade`** covering, in the README's existing voice: what it is (play the dbupdate scripts of one sprint on the current database or a group); the setting (gear menu **Options...**, `DbUpdatePath` in `config.json`, empty = off, validated when the menu opens and when saved, nothing is read at launch); folder layout understood (version folders `vNNNN-...`, sprint number from the name, several folders per sprint, hotfix folders without a sprint selectable by folder, `Production<n>` scripts unchecked and labelled, only top-level `*.sql` read, other files listed as ignored); natural order; **Upgrade database...** dialog flow (Preview with checkboxes and warnings, confirmation, typed sprint number for a group, 4 databases at a time, per-database status, Cancel per database and Cancel all, Copy log); the warnings and their heuristics (index creation or rebuild, `UPDATE`/`DELETE` without `WHERE`, `WHILE`, `WAITFOR`, `ALTER COLUMN`, scripts of 100 000 characters or more; warning only, never a block; not detected: `ALTER TABLE ... ADD PRIMARY KEY/UNIQUE`, dynamic SQL, row counts); safety (read-only connections refused and named before anything runs, SQL Server only, separate write path: **"Run on group" stays read-only**); behavior (scripts split on `GO` lines only, `GO n` refused, no transaction wrapper, no command timeout, first error stops that database and marks the rest skipped, other databases continue, scripts are expected to be replayable and nothing is remembered, refresh the schema in the navigator afterwards to see new tables); limitations (encoding detection, cancelling a DDL statement may leave a database partly upgraded, results of script `SELECT`s are discarded, SQL Server only).
- [ ] **Step 3: One-line cross-reference.** In the `Linked database groups` "Limitations" list, the bullet `The fan-out is read-only: no write, DDL or migration on several databases at once, ...` becomes misleading: append ` (the separate [Database upgrade](#database-upgrade) is the only write path, and it is not part of Run on group)` with one targeted `Edit` on that exact bullet text; leave every other line alone. Also add `DbUpdatePath` to the `config.json` row of the "Configuration and data" table the same way (targeted edit of that cell only).
- [ ] **Step 4:** `git diff README.md` shows only added lines for the new section plus the two small appends; no line of the installer section touched.
- [ ] **Step 5: Commit** `docs(upgrade): README section for the Database upgrade`.

---

## Self-review

**Spec coverage.** Option + persistence + backward compat + default off: T4. Scanner (folder rules, sprint regex, folders without sprint by folder, order, Production labelling): T1. Preview, unknown-sprint message with nearest sprints, warnings column and summary: T6 (data from T1, T2, T5). Analyzer kinds/reasons/lines and thresholds: T2. Targets (current / group of the current connection), 4 at a time, confirmation with typed sprint for groups: T3, T6. Safety (read-only refused before opening, runner repeats, factory re-checks, existing group run never write-capable, isolation tests): T3, T6, Global Constraints. Execution (GO split, no transaction, unbounded timeout, first error stops a database, per-database panel, Cancel per database and all, copyable log): T3, T6. No persistence of the sprint: nothing stored. Startup constraint: T7 (nothing at construction). Tests and README: every task, T8. Optional row-count hints (approach 3): deliberately not planned (spec: only if cheap; it is not).

**Types.** `UpgradeScript`, `UpgradeRunOptions`, `UpgradeProgress`, `UpgradeResult`, `ScriptOutcome`, `UpgradeTarget`, `UpgradeRules`, `UpgradeRunner`, `UpgradeLog` (T3) are the names T6 uses; `UpgradePlan`, `UpgradeScriptItem`, `UpgradePlanner` (T5), `DbUpdateCatalog`/`DbUpdateFolder`/`DbUpdateScript` (T1), `ScriptWarning`/`ScriptWarningKind` (T2) as used in T5 and T6; `OptionsViewModel`/`OptionsResult`/`SetDbUpdatePath` (T4) as used in T7. `UpgradeViewModel.Targets` and `Members` are the properties T7 and its tests read.
