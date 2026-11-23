# Linked database groups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user link databases that share a schema into named groups, switch a tab between members instantly, and run one read-only query on all members in parallel with merged/diff result views.

**Architecture:** Groups are config entities (`AppConfig.Groups`); the fan-out engine, result merging and schema comparison live in `DbExplorer.Core` (fully unit-tested with SQLite files as members and fake drivers); the App adds a groups section in the tree, a member selector in table/script tabs, and a "Run on group" panel in the script tab. Fan-out reuses per-tab dedicated `ScriptConnection`s (one per member) and ALWAYS applies the kind-aware `ReadOnlyGuard` + read-only context.

**Tech Stack:** .NET 10, Avalonia 11, CommunityToolkit.Mvvm, xUnit (existing solution `DbExplorer.sln`: `src/DbExplorer.Core`, `src/DbExplorer.App`, `tests/DbExplorer.Core.Tests`, `tests/DbExplorer.App.Tests`).

**Spec:** `docs/superpowers/specs/2026-11-23-linked-groups-design.md` (read it; also `docs/superpowers/specs/2026-11-20-dbexplorer-design.md`).

## Global Constraints

- Read and compare ONLY: no write fan-out, ever. A group run refuses any write statement for ALL members, whatever each member's own `ReadOnly` flag says.
- All members of a group share one `DbKind` (enforced when editing a group).
- A connection may belong to several groups; deleting a connection removes it from its groups.
- Concurrency cap for a group run: default 4 (`GroupRunOptions.MaxParallel`); per-member row limit default 1000 (existing `ScriptOptions.MaxRows`, max 100000).
- An old `config.json` without `Groups` must load unchanged (groups empty).
- LAUNCH SPEED is the user's top priority: nothing eager added at startup (groups are part of the tiny config; no group work before first paint). RAM is not a constraint.
- UI follows the visual redesign's design tokens/icons (`docs/ui/README.md`, once merged); English UI text; vector icons only.
- Tools: PowerShell for `dotnet`/`git` (the Bash tool cannot write files in this directory); build/test with `dotnet test -p:UseSharedCompilation=false`; if a test project fails to build with "access denied" on obj, delete that project's stale `obj` and retry. No coordinate-based clicks / global keystrokes during any UI verification; headless tests only; never touch the user's real `%APPDATA%\dbExplorer` config or DBeaver files (temp `--data-dir`).
- Git commits end with: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Review Focus

1. A write statement reaching ANY member (multiple statements in one text, comments hiding a verb, a member whose `ReadOnly` is false) — Task 2 pins it.
2. One unreachable/slow member must not block the others; Cancel all / cancel one work; closing the tab leaks no connection — Task 2, Task 7.
3. Merging results with different column sets or runtime types (int vs long, NULL, binary, duplicate rows) and the Differences semantics — Task 3.
4. Switching a tab's database with pending edits, an open transaction, or a table missing in the target — Task 6.
5. Config compatibility (no `Groups`, group referencing a deleted connection, mixed engines) — Task 1, Task 5.

---

## File Structure

```
src/DbExplorer.Core/Model/ConnectionGroup.cs         record + AppConfig.Groups (edit AppConfig.cs)
src/DbExplorer.Core/Groups/GroupRules.cs              validation / lookup / cleanup helpers
src/DbExplorer.Core/Groups/GroupRunner.cs             parallel read-only fan-out
src/DbExplorer.Core/Groups/GroupResults.cs            Merge + Differences + canonical values
src/DbExplorer.Core/Groups/SchemaComparer.cs          schema diff report
src/DbExplorer.App/ViewModels/GroupTreeNodes.cs       tree nodes for groups (or extend TreeNodes.cs)
src/DbExplorer.App/ViewModels/GroupEditorViewModel.cs + Views/GroupEditorDialog.axaml(.cs)
src/DbExplorer.App/ViewModels/GroupRunViewModel.cs    status panel + merged/diff view state
src/DbExplorer.App/Views/ (script tab + table tab edits, SchemaDiffDialog)
tests/DbExplorer.Core.Tests/Group*Tests.cs, tests/DbExplorer.App.Tests/Group*Tests.cs
```

---

### Task 1: Group model, rules and config compatibility

**Files:**
- Create: `src/DbExplorer.Core/Model/ConnectionGroup.cs`, `src/DbExplorer.Core/Groups/GroupRules.cs`
- Modify: `src/DbExplorer.Core/Model/AppConfig.cs` (add `public List<ConnectionGroup> Groups { get; init; } = new();` as an init property, NOT a new positional parameter, so existing `new AppConfig(...)` calls and tests keep compiling)
- Test: `tests/DbExplorer.Core.Tests/GroupRulesTests.cs`, `tests/DbExplorer.Core.Tests/ConfigStoreTests.cs` (extend)

**Interfaces:**
- Produces:
  - `public sealed record ConnectionGroup(Guid Id, string Name, List<Guid> ConnectionIds);`
  - `public static class GroupRules`:
    - `string? Validate(AppConfig cfg, ConnectionGroup g)` → `null` when valid, else a user-facing message: empty/whitespace name; duplicate name (case-insensitive, other group); duplicate member id; unknown connection id; members of different `DbKind` (message names the kinds).
    - `IReadOnlyList<ConnectionGroup> GroupsOf(AppConfig cfg, Guid connectionId)`
    - `IReadOnlyList<ConnectionConfig> MembersOf(AppConfig cfg, ConnectionGroup g)` (group order; skips unknown ids)
    - `void RemoveConnection(AppConfig cfg, Guid connectionId)` (removes the id from every group; groups are kept even when left with 0/1 member)

- [ ] **Step 1: Failing tests** in `GroupRulesTests`: `Validate_AcceptsSameKindMembers`; `Validate_RejectsMixedKinds` (message contains both kinds); `Validate_RejectsDuplicateNameCaseInsensitive`; `Validate_RejectsUnknownAndDuplicateMembers`; `Validate_RejectsEmptyName`; `GroupsOf_ReturnsEveryGroupContainingTheConnection`; `MembersOf_KeepsOrder_AndSkipsUnknown`; `RemoveConnection_RemovesFromAllGroups_KeepsGroups`. In `ConfigStoreTests`: `Load_OldConfigWithoutGroups_HasEmptyGroups` (write a JSON without the property, assert `Groups` empty and not null) and `SaveThenLoad_RoundTripsGroups`.
- [ ] **Step 2:** Run `dotnet test tests/DbExplorer.Core.Tests -p:UseSharedCompilation=false --filter "GroupRulesTests|ConfigStoreTests"` → FAIL (types missing).
- [ ] **Step 3:** Implement the record, the `Groups` init property and `GroupRules`. System.Text.Json must tolerate the missing property (the init default applies) and a `null` value (treat as corrupt like existing null-list handling: throw `JsonException` so the recovery path runs).
- [ ] **Step 4:** Run the same filter, then the whole Core suite → PASS. Commit `feat(groups): group model, rules and config compatibility`.

---

### Task 2: Parallel read-only group runner

**Files:**
- Create: `src/DbExplorer.Core/Groups/GroupRunner.cs`
- Test: `tests/DbExplorer.Core.Tests/GroupRunnerTests.cs`

**Interfaces:**
- Consumes: `ScriptConnection` (`RunAsync(IReadOnlyList<ScriptStatement>, ScriptOptions, CancellationToken)`, owned/disposed by the caller), `ScriptStatement`, `ScriptOptions(int MaxRows, bool ReadOnly, DbKind? Kind)`, `StatementResult`, `ReadOnlyGuard.IsWrite(string sql, DbKind kind)`, `ConnectionConfig` (`Kind`, `Id`, `Name`).
- Produces:
  - `enum MemberState { Pending, Running, Done, Failed, Cancelled }`
  - `record MemberProgress(Guid ConnectionId, MemberState State, int? Rows, TimeSpan? Duration, string? Error)`
  - `record MemberResult(ConnectionConfig Connection, MemberState State, IReadOnlyList<StatementResult> Statements, TimeSpan Duration, string? Error)`
  - `record GroupRunOptions(int MaxRows = 1000, int MaxParallel = 4)`
  - `sealed class GroupRunRefusedException(string message) : Exception(message)`
  - `sealed class GroupRunner(Func<ConnectionConfig, CancellationToken, Task<ScriptConnection>> connectionFor)` with `Task<IReadOnlyList<MemberResult>> RunAsync(IReadOnlyList<ConnectionConfig> members, IReadOnlyList<ScriptStatement> statements, GroupRunOptions options, IProgress<MemberProgress>? progress, CancellationToken ct)` and `void CancelMember(Guid connectionId)`.

- [ ] **Step 1: Failing tests** (members = 3 temp SQLite files with the same table `t(id, name)` and different contents; a fake slow/failing `ScriptConnection` provider via the `connectionFor` delegate where needed): `Run_ReturnsOneResultPerMember_InMemberOrder`; `Run_ExecutesInParallel_UpToMaxParallel` (a gated member provider counts concurrent runs; with 5 members and MaxParallel 2 the peak is 2); `Run_RefusesAnyWriteStatement_BeforeRunningAnything` (`GroupRunRefusedException`; assert no member ran; cases: `DELETE FROM t`, `select 1; update t set name='x'`, `/* c */ INSERT ...`, `SELECT 1 INTO x FROM t` for the SQL Server kind where applicable); `Run_ReadOnlyIsForcedEvenWhenMemberFlagIsFalse` (option passed to the member run has `ReadOnly == true` and the member's `Kind`); `Run_AFailingMember_DoesNotStopTheOthers` (state `Failed`, error text kept, others `Done`); `Run_AnUnreachableMember_TimesOutOnItsOwn` (connectionFor never completes until cancelled: other members finish; `CancelMember` turns it `Cancelled`); `Run_CancelAll_MarksRunningAndPendingMembersCancelled_WithoutThrowing`; `Run_ReportsProgressPerMember` (Pending→Running→Done with rows and duration); `Run_RejectsMixedKinds`.
- [ ] **Step 2:** Run `dotnet test tests/DbExplorer.Core.Tests -p:UseSharedCompilation=false --filter GroupRunnerTests` → FAIL.
- [ ] **Step 3:** Implement `GroupRunner`: guard first (every statement text through `ReadOnlyGuard.IsWrite(text, kind)` for the group kind, all members must share the kind), then a `SemaphoreSlim(MaxParallel)` fan-out with one linked `CancellationTokenSource` per member (so `CancelMember` and cancel-all work), results gathered in member order, member exceptions captured as `Failed` (never thrown), progress posted per state change. The guard is authoritative here: do not rely on `ScriptOptions.ReadOnly` alone.
- [ ] **Step 4:** Run the filter, then the whole Core suite → PASS. Commit `feat(groups): parallel read-only group runner`.

---

### Task 3: Result merging and differences

**Files:**
- Create: `src/DbExplorer.Core/Groups/GroupResults.cs`
- Test: `tests/DbExplorer.Core.Tests/GroupResultsTests.cs`

**Interfaces:**
- Consumes: `ResultSet(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, bool Truncated)`.
- Produces:
  - `record MemberSet(string Database, ResultSet Set)`
  - `record MergedResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, IReadOnlyList<string> Warnings, bool Truncated)`
  - `static class GroupResults`:
    - `MergedResult Merge(IReadOnlyList<MemberSet> sets)` — columns = `"Database"` + the columns common to all sets (case-insensitive match, order of the first set); rows = every member's rows (member order) with the database name prepended; when column sets differ, a warning names the differing columns per member; `Truncated` = any member truncated.
    - `MergedResult Differences(IReadOnlyList<MemberSet> sets)` — columns = `"Present in"` + common columns; one row per distinct canonical row whose occurrence counts are not equal across ALL members; `Present in` = comma-separated names of members that have the row, annotated `name×count` when counts differ between the members that have it.
    - `string Canonical(object? value)` — comparison key: `null` → a reserved token distinct from `""`; integers/decimal/float/double → invariant normalised decimal text (`1`, `1.5`; so `1L`, `1`, `1m` and `1.0` are equal); `bool` → `1`/`0`; `DateTime` → invariant round-trip text in UTC when `Kind` is Utc, else as is; `DateTimeOffset` → UTC; `Guid` → lowercase `D`; `byte[]` → hex; `string` → ordinal as is (case-sensitive); anything else → invariant `ToString()`.

- [ ] **Step 1: Failing tests**: `Merge_PrependsDatabaseColumn_InMemberOrder`; `Merge_DifferentColumnSets_UsesCommonColumns_AndWarns`; `Merge_TruncatedWhenAnyMemberTruncated`; `Differences_IdenticalSets_ReturnsNothing`; `Differences_RowMissingInOneMember_IsListedWithPresentIn`; `Differences_DuplicateCountsDiffer_AreAnnotated` (A has the row twice, B once → `A×2, B×1`); `Differences_TreatsNullAndEmptyStringAsDifferent`; `Differences_NumbersOfDifferentRuntimeTypes_AreEqual` (`1L` vs `1` vs `1.0m`); `Differences_BinaryCompareByContent`; `Differences_BoolOneVsTrueAreEqual` (SQLite long 1 vs Postgres `true`); `Differences_StringsAreCaseSensitive`.
- [ ] **Step 2:** Run `--filter GroupResultsTests` → FAIL.
- [ ] **Step 3:** Implement with a dictionary from the canonical row key (tuple of canonical strings joined with an unambiguous separator/length prefix) to per-member counts; preserve first-appearance order for deterministic output.
- [ ] **Step 4:** Run the filter and the whole Core suite → PASS. Commit `feat(groups): merge and difference views for group results`.

---

### Task 4: Schema comparison

**Files:**
- Create: `src/DbExplorer.Core/Groups/SchemaComparer.cs`
- Test: `tests/DbExplorer.Core.Tests/SchemaComparerTests.cs`

**Interfaces:**
- Consumes: `TableInfo(TableRef Ref, bool IsView, IReadOnlyList<ColumnInfo> Columns, ...)`, `ColumnInfo(string Name, string DataType, ...)`, `TableRef.ToString()`.
- Produces:
  - `record SchemaMember(string Database, IReadOnlyList<TableInfo> Schema)`
  - `record ColumnDiff(string Table, string Column, string Kind, string? Expected, string? Actual)` with `Kind` one of `"Missing"`, `"Extra"`, `"TypeDiffers"`
  - `record SchemaDiff(string Database, IReadOnlyList<string> MissingTables, IReadOnlyList<string> ExtraTables, IReadOnlyList<ColumnDiff> ColumnDiffs)`
  - `static class SchemaComparer { IReadOnlyList<SchemaDiff> Compare(IReadOnlyList<SchemaMember> members); }` — reference = union of the members: a table is `Missing` in a member when at least one OTHER member has it, `Extra` when only this member has it; for tables present in ≥2 members: a column absent here but present elsewhere is `Missing`, present only here is `Extra`, `DataType` different from the most common type among members (tie → first member's) is `TypeDiffers` (Expected = the common type, Actual = this member's). Names compared case-insensitively; a view vs table mismatch is reported as a `TypeDiffers` on a pseudo column named `(object kind)`.

- [ ] **Step 1: Failing tests**: `Identical_Schemas_ReportNothing`; `MissingAndExtraTables_ArePerMember`; `ColumnMissingExtraAndTypeDiffers`; `TypeDiffers_UsesTheMostCommonType`; `NamesAreCaseInsensitive`; `ViewVersusTable_IsReported`; `SingleMember_ReportsNothing`.
- [ ] **Step 2:** Run `--filter SchemaComparerTests` → FAIL. **Step 3:** Implement. **Step 4:** run the filter + whole Core suite → PASS. Commit `feat(groups): schema difference comparer`.

---

### Task 5: Groups in the tree, group editor, schema-difference report

**Files:**
- Create: `src/DbExplorer.App/ViewModels/GroupTreeNodes.cs` (or extend `TreeNodes.cs`), `src/DbExplorer.App/ViewModels/GroupEditorViewModel.cs`, `src/DbExplorer.App/Views/GroupEditorDialog.axaml(.cs)`, `src/DbExplorer.App/Views/SchemaDiffDialog.axaml(.cs)`
- Modify: `src/DbExplorer.App/ViewModels/ConnectionTreeViewModel.cs` (groups section, commands, persistence via the existing `Persist` path which is a no-op in read-only-config mode), `src/DbExplorer.App/Views/MainWindow.axaml` (tree templates only; follow the redesign tokens), `IDialogService` (group editor + report dialogs)
- Test: `tests/DbExplorer.App.Tests/GroupTreeTests.cs`

**Interfaces:**
- Consumes: Task 1 (`ConnectionGroup`, `GroupRules`), Task 4 (`SchemaComparer`), `ConnectionTreeViewModel` (`Config`, `Persist`, `EnsureConnectedAsync(ConnectionConfig, CancellationToken)`, `DeleteConnection`), `SessionManager.GetOrOpenAsync`.
- Produces: tree section "Groups" (group node → member nodes that open/activate the real connection); commands `NewGroup`, `EditGroup(ConnectionGroup)`, `RenameGroup`, `DeleteGroup`, `AddToGroup(ConnectionConfig)`, `RemoveFromGroup(ConnectionConfig, ConnectionGroup)`, `CheckSchemaDifferences(ConnectionGroup)` (connects read-only to all members off the UI thread with spinner/cancel, loads schemas, shows the report); `GroupEditorViewModel(AppConfig cfg, ConnectionGroup? existing)` with `Name`, `Candidates` (connections of ONE engine: choosing the first member restricts the list to that kind), `Selected` (ordered), `string? Error` from `GroupRules.Validate`, `ConnectionGroup? Result`.

- [ ] **Step 1: Failing tests** (view-models, no UI): `Tree_ShowsGroupsSection_WithMemberNodes`; `Tree_NoGroups_ShowsNoSection`; `NewGroup_Persists_AndAppearsInTree`; `GroupEditor_FirstMemberRestrictsCandidatesToItsEngine`; `GroupEditor_InvalidNameOrMixedKinds_SetsError_AndNoResult`; `DeleteGroup_Persists`; `DeleteConnection_RemovesItFromGroups_AndPersists`; `PersistenceDisabled_GroupChangesApplyForTheSessionOnly_WithNotice`; `CheckSchemaDifferences_ShowsReportForLoadedSchemas` (fake sessions with differing `TableInfo`), `CheckSchemaDifferences_UnreachableMemberIsReported_NotFatal`.
- [ ] **Step 2:** Run `--filter GroupTreeTests` → FAIL. **Step 3:** Implement VMs, commands, dialogs and tree templates (icons from the redesign's `Icons.axaml`: add a `Group` geometry there). **Step 4:** run the filter and the whole App suite → PASS; run the app with a temp `--data-dir` and a synthetic config with 3 SQLite connections; screenshot headlessly (in-process only) the tree with a group. Commit `feat(groups): groups in the tree, group editor and schema difference report`.

---

### Task 6: Switch a tab between group members

**Files:**
- Modify: `src/DbExplorer.App/ViewModels/TabViewModel.cs` (make `Connection` switchable: `public ConnectionConfig Connection { get; protected set; }` raising `PropertyChanged` for `Connection`, `ChipColor`, `Title`), `TableTabViewModel*.cs`, `ScriptTabViewModel.cs`, `MainViewModel.cs`, `Views/TableTabView.axaml(.cs)`, `Views/ScriptTabView.axaml(.cs)`, `Views/MainShortcuts.cs`
- Test: `tests/DbExplorer.App.Tests/GroupSwitchTests.cs`

**Interfaces:**
- Consumes: Task 1 `GroupRules.GroupsOf/MembersOf`; `SessionManager`/`Tree.EnsureConnectedAsync`; existing `ConfirmLeaveAsync` (pending-edit guard), `ScriptConnection`, `TableTabViewModel` (`TableRef`, filters, `RawWhere`, `Sort`, `ReloadAsync`).
- Produces on `TabViewModel`: `IReadOnlyList<ConnectionConfig> GroupMembers` (members of the active group; empty when the connection is in no group), `ConnectionGroup? ActiveGroup` (settable; when the connection is in several groups the first is chosen and the UI offers a picker), `Task<bool> SwitchConnectionAsync(ConnectionConfig target)`, `Task<bool> SwitchRelativeAsync(int delta)` (wraps around). Shortcut: next/previous member — the builder picks chords not colliding with existing ones (`MainShortcuts`) and not breaking typing (suggest `Ctrl+Alt+PageDown/PageUp`, verify against the README table) and lists them in the README.

- [ ] **Step 1: Failing tests**: `Table_Switch_ReloadsSameTable_WithSameFilters_PageZero`; `Table_Switch_WithPendingEdits_PromptsFirst_CancelKeepsTheTab`; `Table_Switch_TableMissingInTarget_ShowsErrorStrip_AndKeepsPreviousRows`; `Table_Switch_ChipColorAndTitleFollowTheNewConnection`; `Script_Switch_KeepsTextAndResults_UsesNewConnectionFromNextRun` (the old dedicated connection is disposed, the next run opens the new one, assert via recording sessions); `Script_Switch_WhileRunning_IsRefused`; `Switch_TargetNotInGroup_IsRefused`; `Switch_Relative_WrapsAround`; `ConnectionInNoGroup_HasNoMembers_NoSelector`; `Switch_ToUnreachableMember_ShowsError_StaysOnPreviousConnection`.
- [ ] **Step 2:** Run `--filter GroupSwitchTests` → FAIL. **Step 3:** Implement (the selector is a ComboBox in each tab's toolbar bound to `GroupMembers`/`Connection`, shown only when `GroupMembers.Count > 1`; follow the redesign tokens). The switch resolves the live session through the tree like Reload does (so the tree shows the connection as connected), never hits `Session` of the old connection after the swap, and re-evaluates `CanEdit`/read-only for the new connection. **Step 4:** run the filter and the whole App suite → PASS. Commit `feat(groups): switch table and script tabs between group members`.

---

### Task 7: Run on group — status panel, per-member results, All and Differences

**Files:**
- Create: `src/DbExplorer.App/ViewModels/GroupRunViewModel.cs`
- Modify: `src/DbExplorer.App/ViewModels/ScriptTabViewModel.cs` (+ partial file if needed), `src/DbExplorer.App/Views/ScriptTabView.axaml(.cs)`, `Views/MainShortcuts.cs`
- Test: `tests/DbExplorer.App.Tests/GroupRunViewModelTests.cs`

**Interfaces:**
- Consumes: Task 2 `GroupRunner`, `GroupRunOptions`, `MemberProgress`, `MemberResult`, `GroupRunRefusedException`; Task 3 `GroupResults.Merge/Differences`, `MemberSet`; Task 6 `ActiveGroup`/`GroupMembers`; `ScriptTabViewModel` (`RowLimit`, `StatementAtCaret`/selection rules, `ErrorMessage`, `Close()`), `ResultGrid` (read-only mode), `ScriptConnection` per `Session`.
- Produces: `ScriptTabViewModel.RunOnGroupAsync(bool wholeScript)`; `GroupRunViewModel` with `ObservableCollection<MemberStatusViewModel> Members` (name, state, rows, ms, error), `CancelAllCommand`, `CancelMember(Guid)`, `ObservableCollection<StatementResultViewModel> PerMemberResults`, `MergedResult? All`, `bool ShowDifferences` (toggles `All` ↔ `GroupResults.Differences`), `string? Notice` (e.g. "Group runs are read-only; run writes on one database at a time", merge warnings, truncation per member). Button + shortcut for "Run on group" (statement/selection) and "Run script on group": the builder picks chords (suggest `Ctrl+Shift+Enter` / `Ctrl+Shift+F5`) and documents them. The tab owns one `ScriptConnection` per member (created lazily through `Tree.EnsureConnectedAsync`/`SessionManager`), disposed on tab close, Disconnect of that member, or group membership change.

- [ ] **Step 1: Failing tests** (view-model level with SQLite file members): `RunOnGroup_ShowsOneStatusPerMember_AndPerMemberResults`; `RunOnGroup_AllTab_HasDatabaseColumn_FirstColumn`; `RunOnGroup_DifferencesToggle_ShowsOnlyNonIdenticalRows`; `RunOnGroup_WriteStatement_IsRefused_NothingRuns_NoticeShown`; `RunOnGroup_ReadOnlyForcedEvenIfMemberIsNotReadOnly`; `RunOnGroup_UnreachableMember_ShowsErrorOnThatMemberOnly`; `RunOnGroup_CancelAll_StopsEverything_IsRunningFalse`; `RunOnGroup_CancelOneMember`; `RunOnGroup_UsesSelectionRulesOfCtrlEnter` (blank-line statement under caret, selection, whole script); `RunOnGroup_ClosingTheTab_CancelsAndDisposesMemberConnections`; `RunOnGroup_NotInAGroup_IsNotAvailable`; `RunOnGroup_RowLimitAppliesPerMember_WithTruncationNotice`.
- [ ] **Step 2:** Run `--filter GroupRunViewModelTests` → FAIL. **Step 3:** Implement VM + view: member status panel (icon per state), result tabs (one per member + "All" + Differences toggle) built on the existing read-only `ResultGrid`; all async work off the UI thread, results marshalled to the UI thread, no `async void` outside guarded handlers. **Step 4:** run the filter and the whole App suite → PASS. In-process screenshot of a 3-member run (temp data dir). Commit `feat(groups): run a query on a whole group with merged and difference results`.

---

### Task 8: Docs, startup check, final polish

**Files:**
- Modify: `README.md` (groups section: concept, tree, switching, Run on group, read-only guarantee, Differences, schema report, new shortcuts in the shortcuts table; known limitations: same-engine groups, read-only fan-out, one connection per member per tab), `docs/ui/README.md` if it exists (group icon/selector conventions)
- Test: `tests/DbExplorer.App.Tests` (README shortcut table vs `MainShortcuts` if such a test exists; otherwise none)

- [ ] **Step 1:** Update the README and verify every shortcut/claim against the code. **Step 2:** Run `scripts/measure-startup.ps1` (PowerShell 7, README describes it) against a Release build; compare with the README numbers (≈260 ms warm self-contained composite R2R); a regression > 30 ms must be investigated (nothing group-related may run before first paint). **Step 3:** Run the whole solution (`dotnet test -p:UseSharedCompilation=false`). **Step 4:** Commit `docs(groups): README and startup check`.
