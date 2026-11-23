# dbExplorer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A lightweight Windows desktop database explorer/editor (PostgreSQL, SQL Server, SQLite) with folders, instant table/column jump, FK navigation, filters, staged row editing and a script runner.

**Architecture:** `DbExplorer.Core` (no UI: config, drivers, metadata, query/edit builders, script runner) is fully unit-tested; `DbExplorer.App` (Avalonia, MVVM) is a thin shell over Core, with ViewModels tested headlessly and grids verified by running the app. One `IDbDriver` per engine hides dialect differences.

**Tech Stack:** .NET 10, Avalonia 11.x (latest stable at scaffold time) + Avalonia.Controls.DataGrid + AvaloniaEdit, CommunityToolkit.Mvvm, Npgsql, Microsoft.Data.SqlClient, Microsoft.Data.Sqlite, xUnit. Windows only.

**Spec:** `docs/superpowers/specs/2026-11-20-dbexplorer-design.md`

## Global Constraints

- Windows 11, .NET 10; no Java, no Electron. UI language English; identifiers/comments English.
- Targets: cold start < ~1 s, idle RAM < ~150 MB (pass/fail bar).
- Config: JSON at `%APPDATA%\dbExplorer\config.json`; metadata cache under `%APPDATA%\dbExplorer\cache\`. Passwords encrypted with DPAPI (current user), never clear text on disk.
- Auth v1: user/password only. SQL Server connections expose `Encrypt` and `TrustServerCertificate`.
- Connection = one database. Tree: folder > connection > schema > table/view (SQLite: no schema). Folders one level only.
- Page size 200 rows, OFFSET paging; total row count only on demand. Script result row limit 1000 (configurable). Views are read-only.
- Shortcuts: `Ctrl+T` tables of active connection; `Ctrl+Shift+T` all cached connections (never connects by itself); `Ctrl+L` column jump; `Ctrl+N` new script; `Ctrl+Enter` run statement/selection; `F5` run script; `Ctrl+S` save edits (table view) / save file (script).
- Nothing is written without explicit save + SQL preview. Per-connection `ReadOnly` flag (default false) disables editing and refuses write statements in scripts.
- All filters/WHERE are executed server-side with parameters.
- Git commits end with: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Review Focus

Failure modes the spec implies; each has a pinning test in the owning task:
1. Identifiers with spaces, reserved words, quotes (`"Order Details"`, `[a]]b]`) — Task 3 (quoting), Task 6.
2. NULL vs empty string, and typed values (int, guid, datetime2, bool) in filters and edits — Task 6, Task 7.
3. Composite PKs/FKs and FKs pointing to another schema or the same table — Task 4/5, Task 7.
4. Row changed or deleted by someone else between load and save (0 rows affected) — Task 7.
5. Unreachable host / wrong password / cancelled query must not freeze the UI or crash — Task 8, Task 10.

---

## File Structure

```
DbExplorer.sln
src/DbExplorer.Core/
  Model/        ConnectionConfig.cs AppConfig.cs Schema.cs
  Config/       ConfigStore.cs ISecretProtector.cs DpapiSecretProtector.cs DBeaverImporter.cs
  Drivers/      IDbDriver.cs SqliteDriver.cs PostgresDriver.cs SqlServerDriver.cs DriverFactory.cs
  Querying/     FilterParser.cs TableQueryBuilder.cs KeyResolver.cs EditPlanner.cs EditApplier.cs
  Scripting/    ScriptSplitter.cs ReadOnlyGuard.cs ScriptRunner.cs
  Metadata/     MetadataCache.cs FuzzyMatcher.cs
  Sessions/     SessionManager.cs
src/DbExplorer.App/   Avalonia app: App.axaml, Views/, ViewModels/
tests/DbExplorer.Core.Tests/
tests/DbExplorer.App.Tests/    (ViewModel tests)
```

---

### Task 1: Solution scaffold, config model and store

**Files:**
- Create: `DbExplorer.sln`, `src/DbExplorer.Core/*.csproj`, `tests/DbExplorer.Core.Tests/*.csproj`, `Model/ConnectionConfig.cs`, `Model/AppConfig.cs`, `Config/ConfigStore.cs`, `Config/ISecretProtector.cs`, `Config/DpapiSecretProtector.cs`, `.gitignore`
- Test: `tests/DbExplorer.Core.Tests/ConfigStoreTests.cs`

**Interfaces:**
- Produces:
  - `enum DbKind { Postgres, SqlServer, Sqlite }`
  - `record ConnectionConfig(Guid Id, string Name, DbKind Kind, string? Folder, string? Host, int? Port, string Database, string? User, string? ProtectedPassword, bool Encrypt, bool TrustServerCertificate, bool ReadOnly)` (for SQLite, `Database` is the file path)
  - `record AppConfig(List<string> Folders, List<ConnectionConfig> Connections, Dictionary<Guid, Dictionary<string, string[]>> VirtualKeys)` (virtual keys: connection id → `"schema.table"` → key columns)
  - `interface ISecretProtector { string Protect(string plain); string Unprotect(string protectedValue); }` + `DpapiSecretProtector` (`ProtectedData`, CurrentUser, base64)
  - `class ConfigStore(string path, ISecretProtector protector)` with `AppConfig Load()` (empty config if file missing), `void Save(AppConfig)` (atomic: write temp then replace)

- [ ] **Step 1:** `git init`, create solution + the two projects (net10.0), `.gitignore` (bin/obj/.vs). Verify `dotnet build` passes.
- [ ] **Step 2: Failing tests** in `ConfigStoreTests`: `Load_MissingFile_ReturnsEmptyConfig`, `SaveThenLoad_RoundTripsFoldersConnectionsAndVirtualKeys`, `Save_NeverWritesPlainPassword` (build config via `protector.Protect("s3cret")`, assert the file text does not contain `s3cret`), `DpapiProtector_RoundTrips` (Windows only).
- [ ] **Step 3:** Implement types and `ConfigStore` (System.Text.Json, indented, enums as strings).
- [ ] **Step 4:** `dotnet test` → all pass. Commit.

---

### Task 2: DBeaver importer

**Files:**
- Create: `src/DbExplorer.Core/Config/DBeaverImporter.cs`
- Test: `tests/DbExplorer.Core.Tests/DBeaverImporterTests.cs`, `tests/DbExplorer.Core.Tests/Fixtures/data-sources.json` (copy of the user's file with hosts anonymised)

**Interfaces:**
- Consumes: `AppConfig`, `ConnectionConfig`, `ISecretProtector` (Task 1)
- Produces: `static class DBeaverImporter` with `ImportResult Import(string dbeaverDir, AppConfig target, ISecretProtector p)`; `record ImportResult(int Added, int Skipped, IReadOnlyList<string> Warnings)`. Default `dbeaverDir`: `%APPDATA%\DBeaverData\workspace6\General\.dbeaver`.

- [ ] **Step 1: Failing tests:** `Import_ReadsFoldersAndConnections` (fixture → 4 folders, 8 connections; "Acme local" is SqlServer, host `127.0.0.1`, port 1433, db `acme`, folder `Acme`, `TrustServerCertificate == true`; "GlobexAuth" is Postgres port 5454 with `Folder == null`; "Admin SQLite" is Sqlite with `Database == @"C:\Temp\_Acme\adminDB.db"`); `Import_DecryptsCredentials` (test encrypts `{"<id>":{"#connection":{"user":"sa","password":"pw"}}}` with the key below into `credentials-config.json`, expects `User == "sa"` and `Unprotect(ProtectedPassword) == "pw"`); `Import_RunTwice_SkipsExistingNames`; `Import_ImportsVirtualKeys` (the fixture's `virtual-models` entry → `VirtualKeys[id]["acme.liquibase.DATABASECHANGELOG"]`-style entry with all columns marked: store as the table's key with columns left empty meaning "use all columns" — builder: represent as `["*"]`).
- [ ] **Step 2:** Implement. `credentials-config.json` = AES-128-CBC, PKCS7, key bytes hex `babb4a9f774ab853c96c2d653dfe544a`, first 16 bytes of file are the IV, rest is ciphertext, plaintext is JSON. Map provider `postgresql`→Postgres, `sqlserver`→SqlServer, `sqlite`→Sqlite; missing credentials file → warning, not failure.
- [ ] **Step 3:** Run tests (pass). Manual check: run the importer against the real DBeaver dir in a throwaway test marked `[Fact(Skip = "manual")]`, confirm 8 imported. Commit.

---

### Task 3: Driver abstraction and schema model + SQLite driver

**Files:**
- Create: `Model/Schema.cs`, `Drivers/IDbDriver.cs`, `Drivers/SqliteDriver.cs`, `Drivers/DriverFactory.cs`
- Test: `tests/DbExplorer.Core.Tests/SqliteDriverTests.cs`

**Interfaces:**
- Produces:
  - `record TableRef(string? Schema, string Name)` (`ToString()` = `schema.name` or `name`)
  - `record ColumnInfo(string Name, string DataType, Type ClrType, bool Nullable, bool IsPrimaryKey, int Ordinal)`
  - `record ForeignKeyInfo(string Name, IReadOnlyList<string> Columns, TableRef RefTable, IReadOnlyList<string> RefColumns)`
  - `record IndexInfo(string Name, bool Unique, IReadOnlyList<string> Columns)`
  - `record TableInfo(TableRef Ref, bool IsView, IReadOnlyList<ColumnInfo> Columns, IReadOnlyList<ForeignKeyInfo> ForeignKeys, IReadOnlyList<IndexInfo> Indexes)`
  - `interface IDbDriver`: `DbKind Kind`; `string Quote(string identifier)`; `string QuoteTable(TableRef t)`; `string CastToText(string quotedColumn)`; `string EscapeLike(string text)`; `string PageSql(string selectSql, string orderBy, int offset, int limit)` (SQL Server: `ORDER BY` + `OFFSET n ROWS FETCH NEXT m ROWS ONLY`; others: `LIMIT m OFFSET n`; offset/limit inlined as validated ints); `DbConnection CreateConnection(ConnectionConfig cfg, string? plainPassword)`; `Task<IReadOnlyList<TableInfo>> LoadSchemaAsync(DbConnection c, CancellationToken ct)`; `string ParamPrefix` (`"@"`)
  - `static class DriverFactory { static IDbDriver For(DbKind kind) }`

- [ ] **Step 1: Failing tests** against a temp-file SQLite DB created in the fixture: `LoadSchema_ReturnsTablesViewsColumnsPk` (composite PK reported on both columns), `LoadSchema_ReturnsForeignKeys` (composite FK + self-referencing FK), `LoadSchema_ReturnsIndexes`, `Quote_EscapesEmbeddedQuotes` (`a"b` → `"a""b"`), `QuoteTable_WithSpaces`, `PageSql_UsesLimitOffset`, `CreateConnection_UsesFileDatabase`.
- [ ] **Step 2:** Implement interface, model, `SqliteDriver` (PRAGMA `table_info`, `foreign_key_list`, `index_list`/`index_info`; map declared types to `ClrType`; `sqlite_master` for views), factory.
- [ ] **Step 3:** `dotnet test` passes. Commit.

---

### Task 4: PostgreSQL driver

**Files:** Create `Drivers/PostgresDriver.cs`; Test `tests/DbExplorer.Core.Tests/PostgresDriverTests.cs`

**Interfaces:** Consumes `IDbDriver` and schema records (Task 3). Produces `PostgresDriver : IDbDriver`; `DriverFactory.For(DbKind.Postgres)` returns it.

- [ ] **Step 1: Failing tests.** Pure (always run): `Quote_DoublesQuotes`, `PageSql_UsesLimitOffset`, `CastToText_UsesDoubleColon` (`"c"::text`), `CreateConnection_BuildsConnectionString` (host/port/db/user, timeout 10 s). Integration (xUnit `[SkippableFact]`, skipped unless env `DBX_PG` holds a connection string): `LoadSchema_ReturnsCrossSchemaForeignKey`, `LoadSchema_ReturnsCompositePk`.
- [ ] **Step 2:** Implement with Npgsql; schema via `information_schema`/`pg_catalog` in a fixed number of bulk queries (no per-table queries); exclude `pg_catalog`, `information_schema`.
- [ ] **Step 3:** Run tests; run integration tests against the user's local Postgres (`localhost:5432/backoffice`) if reachable. Commit.

---

### Task 5: SQL Server driver

**Files:** Create `Drivers/SqlServerDriver.cs`; Test `tests/DbExplorer.Core.Tests/SqlServerDriverTests.cs`

**Interfaces:** same shape as Task 4; `DriverFactory.For(DbKind.SqlServer)`.

- [ ] **Step 1: Failing tests.** Pure: `Quote_BracketsAndEscapesClosingBracket` (`a]b` → `[a]]b]`), `PageSql_UsesOffsetFetch` (expects `ORDER BY` kept, `OFFSET 200 ROWS FETCH NEXT 200 ROWS ONLY`), `CastToText_UsesNvarcharMax`, `CreateConnection_AppliesEncryptAndTrustServerCertificate` (inspect `SqlConnectionStringBuilder`). Integration (skipped unless env `DBX_MSSQL`): `LoadSchema_ReturnsFkAcrossSchemas`, `LoadSchema_MapsUniqueidentifierToGuid`.
- [ ] **Step 2:** Implement with Microsoft.Data.SqlClient; schema via `sys.tables/views/columns/foreign_keys/foreign_key_columns/indexes/index_columns` in bulk queries; connect timeout 10 s.
- [ ] **Step 3:** Run tests; run integration against local `127.0.0.1:1433/acme` if reachable. Commit.

---

### Task 6: Filter parsing and table query builder

**Files:** Create `Querying/FilterParser.cs`, `Querying/TableQueryBuilder.cs`; Test `tests/DbExplorer.Core.Tests/QueryBuilderTests.cs`

**Interfaces:**
- Consumes: `IDbDriver`, `TableInfo`, `ColumnInfo` (Task 3)
- Produces:
  - `enum FilterOp { Contains, Eq, Ne, Lt, Gt, Le, Ge, IsNull, NotNull, In }`
  - `record FilterExpr(FilterOp Op, IReadOnlyList<string> Values)`; `static FilterExpr? FilterParser.Parse(string text)` (empty/whitespace → null)
  - `record ColumnFilter(string Column, string Text)`; `record SortSpec(string Column, bool Descending)`
  - `record TableQuery(TableInfo Table, IReadOnlyList<ColumnFilter> Filters, string? RawWhere, IReadOnlyList<SortSpec> Sort, int Offset, int Limit)`
  - `record SqlParam(string Name, object? Value)`; `record SqlCommandSpec(string Sql, IReadOnlyList<SqlParam> Params)`
  - `static class TableQueryBuilder { SqlCommandSpec Build(IDbDriver d, TableQuery q); SqlCommandSpec BuildCount(IDbDriver d, TableQuery q); }`

- [ ] **Step 1: Failing tests:** `Parse_PlainText_IsContains`; `Parse_Operators` (`=5`, `!=x`, `<10`, `>=3`, `<=3`, `>1` ); `Parse_NullAndNotNull` (`null`, `!null`); `Parse_In` (`in (a, b ,c)` → 3 values); `Parse_Empty_ReturnsNull`; `Build_ConvertsValueToColumnClrType` (int column `=5` → param value `5` as `int`; guid column; unparseable `=abc` on int column → throws `FilterException` with the column name); `Build_ContainsEscapesLikeWildcards` (`50%` matches literally); `Build_QuotesIdentifiersWithSpaces` (`"Order Details"`); `Build_EmptyStringFilterIsNotNullFilter` (`=` with empty value means equals empty string, distinct from `null`); `Build_CombinesFiltersAndRawWhereWithAnd` (raw where wrapped in parentheses, appended verbatim, not parameterised); `Build_SqlServerWithoutSort_UsesPkThenSelectNull` (ORDER BY pk, or `(SELECT NULL)` when no key); `BuildCount_HasNoPaging`.
- [ ] **Step 2:** Implement. Contains uses `d.CastToText(col) LIKE @p ESCAPE '\'`; parameter names `@p0..`.
- [ ] **Step 3:** `dotnet test` passes. Commit.

---

### Task 7: Edit planning and applying

**Files:** Create `Querying/KeyResolver.cs`, `Querying/EditPlanner.cs`, `Querying/EditApplier.cs`; Test `tests/DbExplorer.Core.Tests/EditTests.cs`

**Interfaces:**
- Consumes: `IDbDriver`, `TableInfo`, `SqlCommandSpec`, `AppConfig.VirtualKeys`, `ConnectionConfig.ReadOnly`
- Produces:
  - `static IReadOnlyList<string>? KeyResolver.Resolve(TableInfo t, ConnectionConfig c, AppConfig cfg)` → primary key columns, else virtual key (`["*"]` expands to all columns), else `null` (= read-only). Views, and `c.ReadOnly`, always return `null`.
  - `abstract record RowEdit`; `record UpdateRow(IReadOnlyDictionary<string, object?> Key, IReadOnlyDictionary<string, object?> Changes) : RowEdit`; `record InsertRow(IReadOnlyDictionary<string, object?> Values) : RowEdit`; `record DeleteRow(IReadOnlyDictionary<string, object?> Key) : RowEdit`
  - `record PlannedStatement(SqlCommandSpec Command, string PreviewSql)` (`PreviewSql` has values inlined and escaped for display only)
  - `static IReadOnlyList<PlannedStatement> EditPlanner.Plan(IDbDriver d, TableInfo t, IReadOnlyList<string> keyColumns, IReadOnlyList<RowEdit> edits)`
  - `class StaleRowException(PlannedStatement stmt) : Exception`
  - `static Task EditApplier.ApplyAsync(DbConnection c, IReadOnlyList<PlannedStatement> stmts, CancellationToken ct)` — one transaction; if a statement affects 0 rows, roll back everything and throw `StaleRowException`.

- [ ] **Step 1: Failing tests:** `Resolve_UsesPrimaryKey`; `Resolve_FallsBackToVirtualKey`; `Resolve_VirtualStar_ExpandsToAllColumns`; `Resolve_NoKey_ReturnsNull`; `Resolve_ReadOnlyConnectionOrView_ReturnsNull`; `Plan_Update_WhereUsesAllKeyColumns` (composite key); `Plan_NullKeyValue_UsesIsNull` (virtual keys with nullable columns); `Plan_Update_NullDiffersFromEmptyString` (set to `null` vs `""`); `Plan_Insert_And_Delete`; `Preview_EscapesSingleQuotes`; `Apply_StaleRow_RollsBackAndThrows` (SQLite: 2 updates, second row deleted beforehand → first update not persisted); `Apply_Success_Commits`. Use a SQLite temp DB.
- [ ] **Step 2:** Implement.
- [ ] **Step 3:** `dotnet test` passes. Commit.

---

### Task 8: Script splitter, read-only guard, script runner

**Files:** Create `Scripting/ScriptSplitter.cs`, `Scripting/ReadOnlyGuard.cs`, `Scripting/ScriptRunner.cs`; Test `tests/DbExplorer.Core.Tests/ScriptingTests.cs`

**Interfaces:**
- Consumes: `DbKind`, `DbConnection`
- Produces:
  - `record ScriptStatement(string Text, int Start, int End)`; `static IReadOnlyList<ScriptStatement> ScriptSplitter.Split(string script, DbKind kind)` — `Start/End` are character offsets in the script (used for "statement under cursor": `static ScriptStatement? StatementAt(IReadOnlyList<ScriptStatement>, int offset)`)
  - `static bool ReadOnlyGuard.IsWrite(string sql)` — true for anything whose first keyword (after comments) is not `SELECT`, `WITH` (without a following write verb), `EXPLAIN`, `SHOW`, `PRAGMA` (read forms), `VALUES`
  - `record ResultSet(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, bool Truncated)`; `record StatementResult(ScriptStatement Statement, IReadOnlyList<ResultSet> Sets, int? RowsAffected, TimeSpan Duration, string? Error)`
  - `record ScriptOptions(int MaxRows = 1000, bool ReadOnly = false)`
  - `static IAsyncEnumerable<StatementResult> ScriptRunner.RunAsync(DbConnection c, IReadOnlyList<ScriptStatement> stmts, ScriptOptions o, CancellationToken ct)` — a failing statement yields a result with `Error` set and stops the script; cancellation cancels the running command (`DbCommand.Cancel`) and ends the enumeration without throwing to the UI.

- [ ] **Step 1: Failing tests:** `Split_Semicolons`; `Split_IgnoresSemicolonsInStringsCommentsAndDollarQuotes`; `Split_SqlServerGo_OnOwnLineOnly` (`GO` inside a string or identifier is not a separator; `go` case-insensitive); `Split_TrailingWithoutSemicolon`; `StatementAt_CursorInsideAndBetween`; `Guard_Select_Allowed`; `Guard_Insert_Update_Delete_Drop_Refused`; `Guard_CommentThenDelete_Refused`; `Guard_WithCteFollowedByDelete_Refused`; `Run_ReturnsRowsAndColumns`; `Run_MultipleResultSets` (SQLite single set per statement is fine; test multiple statements); `Run_TruncatesAtMaxRows` (`Truncated == true`); `Run_ErrorStopsScript_AndReportsMessage`; `Run_ReadOnly_RefusesWrites_WithoutExecuting`; `Run_Cancel_DoesNotThrow` (long recursive CTE on SQLite).
- [ ] **Step 2:** Implement a character-level state machine for the splitter; runner uses `ExecuteReader` + `NextResult` loops and `RecordsAffected`.
- [ ] **Step 3:** `dotnet test` passes. Commit.

---

### Task 9: Metadata cache and fuzzy matcher

**Files:** Create `Metadata/MetadataCache.cs`, `Metadata/FuzzyMatcher.cs`; Test `tests/DbExplorer.Core.Tests/MetadataTests.cs`

**Interfaces:**
- Consumes: `TableInfo`, `TableRef`
- Produces:
  - `record CachedTable(TableRef Ref, bool IsView, string[] Columns)`
  - `class MetadataCache(string dir)`: `void Save(Guid connectionId, IReadOnlyList<TableInfo> schema)`; `IReadOnlyList<CachedTable> Load(Guid connectionId)` (empty if none or corrupt file); `void Delete(Guid connectionId)`
  - `static int? FuzzyMatcher.Score(string query, string candidate)` — case-insensitive subsequence match, higher is better, `null` = no match; prefix and word-boundary/camelCase matches and shorter candidates score higher; `static IReadOnlyList<T> FuzzyMatcher.Rank<T>(string query, IEnumerable<T> items, Func<T, string> text, int take)`

- [ ] **Step 1: Failing tests:** `Cache_SaveLoad_RoundTrips`; `Cache_CorruptFile_ReturnsEmpty`; `Score_NoMatch_ReturnsNull`; `Score_PrefixBeatsScattered` (`usr` vs `users` > vs `unusual_records`); `Score_CamelCaseInitials` (`pd` matches `ProductDetail`); `Score_IsCaseInsensitive`; `Rank_OrdersByScoreThenName_AndTakes`; `Rank_EmptyQuery_ReturnsFirstNAlphabetical`.
- [ ] **Step 2:** Implement (JSON file per connection id).
- [ ] **Step 3:** `dotnet test` passes. Commit.

---

### Task 10: Session manager and Avalonia shell (tree, connection dialog, import)

**Files:**
- Create: `Sessions/SessionManager.cs`; `src/DbExplorer.App/` (Avalonia app project, `App.axaml`, `Views/MainWindow.axaml`, `Views/ConnectionDialog.axaml`, `ViewModels/MainViewModel.cs`, `ViewModels/ConnectionTreeViewModel.cs`, `ViewModels/ConnectionDialogViewModel.cs`); `tests/DbExplorer.App.Tests/`
- Test: `tests/DbExplorer.Core.Tests/SessionManagerTests.cs`, `tests/DbExplorer.App.Tests/ConnectionTreeViewModelTests.cs`

**Interfaces:**
- Consumes: `ConfigStore`, `DBeaverImporter`, `DriverFactory`, `MetadataCache`, `ISecretProtector`
- Produces:
  - `class Session { ConnectionConfig Config; IDbDriver Driver; IReadOnlyList<TableInfo> Schema; Task<DbConnection> OpenConnectionAsync(CancellationToken ct) }` (new pooled connection per operation)
  - `class SessionManager(ConfigStore, MetadataCache, ISecretProtector)`: `Task<Session> GetOrOpenAsync(ConnectionConfig c, CancellationToken ct)` — on first open: connect, `LoadSchemaAsync`, save to `MetadataCache`; later connects refresh schema in the background; `IReadOnlyList<Session> Open`; `void Close(Guid id)`
  - `ConnectionTreeViewModel`: nodes Folder → Connection → Schema → Table/View (schema nodes loaded from `Session.Schema` once connected); commands `AddConnection`, `EditConnection`, `DeleteConnection`, `AddFolder`, `RenameFolder`, `DeleteFolder`, `MoveToFolder(connection, folder)` (also by drag and drop), `ImportFromDBeaver`, `ToggleReadOnly`; every mutation persists via `ConfigStore.Save`
  - `MainViewModel` exposes `ObservableCollection<TabViewModel> Tabs`, `TabViewModel? ActiveTab`, `Task OpenTable(Session s, TableRef t)`; `abstract class TabViewModel { string Title; ConnectionConfig Connection; }`

- [ ] **Step 1: Failing tests:** Core: `GetOrOpen_LoadsSchemaAndWritesCache` (SQLite); `GetOrOpen_SecondCall_ReturnsSameSession`; `GetOrOpen_WrongPath_ThrowsAndLeavesNoSession`; `GetOrOpen_UnreachableHost_FailsWithin15Seconds` (Postgres to `10.255.255.1`, asserts timeout handled, marked `Trait("Category","Slow")`). App VM: `Tree_GroupsConnectionsByFolder_AndPutsUnfolderedAtRoot`; `MoveToFolder_Persists`; `DeleteFolder_MovesConnectionsToRoot`; `Import_AddsConnectionsAndRefreshesTree`; `Connect_Failure_SetsErrorStateWithoutThrowing`.
- [ ] **Step 2:** Implement Core `SessionManager`. Scaffold the Avalonia app (Fluent theme, follows system light/dark), main window = left tree + tab area (empty placeholder), connection dialog (name, kind, host, port, database/file path with browse, user, password, folder, encrypt, trust server certificate, read-only, "Test connection" button), import button, status bar showing connection errors. All connect/schema calls are awaited off the UI thread with a visible spinner.
- [ ] **Step 3:** `dotnet test`; then run the app (`dotnet run --project src/DbExplorer.App`), import the real DBeaver config, expand a folder and connect to the SQLite connection. Note cold-start time and RAM in the commit message. Commit.

---

### Task 11: Table tab — data grid, filters, FK navigation, structure, column jump

**Files:** Create `ViewModels/TableTabViewModel.cs`, `ViewModels/RowViewModel.cs`, `ViewModels/StructureViewModel.cs`, `Views/TableTabView.axaml`; Test `tests/DbExplorer.App.Tests/TableTabViewModelTests.cs`

**Interfaces:**
- Consumes: `Session`, `TableQueryBuilder`, `FilterParser`, `TableInfo`, `MainViewModel.OpenTable`
- Produces:
  - `RowViewModel { object?[] Values; object?[] Original; bool IsNew; bool IsDeleted; bool IsDirty(int col) }`
  - `TableTabViewModel : TabViewModel` with: `Task LoadAsync()`, `ObservableCollection<RowViewModel> Rows`, `Dictionary<string,string> ColumnFilters`, `string? RawWhere`, `SortSpec? Sort`, `int PageIndex`, `Task NextPage()/PrevPage()`, `Task<long> CountAsync()` (on demand only), `Task ApplyFiltersAsync()` (also bound to Enter in a filter box; debounced never — explicit), `Task FilterByCellAsync(int rowIndex, string column)` (sets `Column = value`, or `null` filter for NULL), `Task FollowForeignKeyAsync(int rowIndex, ForeignKeyInfo fk)` → opens new tab for `fk.RefTable` with filters on `fk.RefColumns` equal to the row's values, `IReadOnlyList<ColumnInfo> Columns`, `int? FindColumn(string name)` for column jump, `StructureViewModel Structure`
  - View: Data/Structure sub-tabs; filter row implemented as a TextBox inside each column header template; FK columns show a `↗` link affordance; `Ctrl+L` opens the column picker (reuse Task 13's overlay) and scrolls/selects that column; `Ctrl+T` etc. handled in Task 13.

- [ ] **Step 1: Failing VM tests** (SQLite fixture with `customers`, `orders(customer_id → customers.id)` plus a 500-row table): `Load_ReturnsFirst200Rows`; `NextPage_ReturnsRows200To399`; `ColumnFilter_AppliesServerSide` (filter text hits rows beyond the first page); `RawWhere_AndColumnFilter_AreCombined`; `FilterByCell_NullValue_UsesNullFilter`; `FollowForeignKey_OpensTabWithFilter` (composite FK uses all columns; self-referencing FK opens new tab of the same table); `FollowForeignKey_NullValue_DoesNothing`; `Count_OnlyWhenRequested` (no COUNT executed on load — assert via recording driver); `SortToggle_AscDescNone`; `FindColumn_CaseInsensitive`; `BadFilterValue_SetsErrorMessage_KeepsPreviousRows`.
- [ ] **Step 2:** Implement VM + view. Grid: Avalonia DataGrid with columns generated from `Columns` at load, `RowViewModel` indexer binding, virtualised rows. Display rules: NULL shown as dim `NULL`, binary as hex preview (first 32 bytes), long text truncated in cell with full value in a tooltip / detail panel.
- [ ] **Step 3:** Structure sub-tab: columns (name, type, nullable, PK marker, FK target link), indexes, foreign keys list; clicking an FK target opens that table. Test `Structure_ListsColumnsKeysAndIndexes`.
- [ ] **Step 4:** `dotnet test`; run the app on a real SQL Server/Postgres connection, open a table with FK, follow it, filter, sort, page. Commit.

---

### Task 12: Staged editing UI

**Files:** Modify `ViewModels/TableTabViewModel.cs`, `Views/TableTabView.axaml`; Create `Views/SqlPreviewDialog.axaml`, `Views/VirtualKeyDialog.axaml`; Test `tests/DbExplorer.App.Tests/EditingViewModelTests.cs`

**Interfaces:**
- Consumes: `KeyResolver`, `EditPlanner`, `EditApplier`, `RowViewModel`
- Produces on `TableTabViewModel`: `bool CanEdit` (false when `KeyResolver.Resolve` returns null), `IReadOnlyList<string>? KeyColumns`, `void SetCell(int row, int col, object? value)`, `void AddRow()`, `void DeleteRow(int row)`, `bool HasPendingEdits`, `IReadOnlyList<PlannedStatement> BuildPreview()`, `Task SaveAsync()`, `void Revert()`, `void SetVirtualKey(IReadOnlyList<string> columns)` (persists in `AppConfig.VirtualKeys`)

- [ ] **Step 1: Failing tests:** `SetCell_MarksCellDirty_AndHasPendingEdits`; `SetCell_BackToOriginal_ClearsDirty`; `Save_AppliesAndReloadsPage`; `Save_StaleRow_ShowsConflictAndKeepsEdits`; `ReadOnlyConnection_CanEditFalse`; `NoKey_CanEditFalse_UntilVirtualKeySet`; `SetVirtualKey_PersistsToConfig`; `SetCell_TextToIntColumn_InvalidValue_Rejected`; `EmptyStringVsNull_InCellEditor` (a dedicated "Set NULL" gesture, empty text means empty string for text columns); `Revert_RestoresOriginals`; `Navigating_WithPendingEdits_Prompts` (page change / filter apply asks to save or discard).
- [ ] **Step 2:** Implement. Dirty cells get an accent background; `Ctrl+S` opens `SqlPreviewDialog` (shows `PreviewSql` list, Apply/Cancel); `Esc` in grid reverts the current cell; toolbar Add/Delete row; read-only badge in the tab header when `CanEdit` is false with a "Define key…" link opening `VirtualKeyDialog`.
- [ ] **Step 3:** `dotnet test`; manually edit a row on the SQLite DB and on a test table of the local Postgres, confirm preview SQL and result. Commit.

---

### Task 13: Quick-open overlay (Ctrl+T, Ctrl+Shift+T, Ctrl+L)

**Files:** Create `ViewModels/QuickOpenViewModel.cs`, `Views/QuickOpenOverlay.axaml`; Modify `Views/MainWindow.axaml(.cs)` (key bindings); Test `tests/DbExplorer.App.Tests/QuickOpenTests.cs`

**Interfaces:**
- Consumes: `FuzzyMatcher`, `MetadataCache`, `SessionManager`, `MainViewModel`
- Produces: `record QuickOpenItem(string Label, string? Detail, Func<Task> Open)`; `QuickOpenViewModel` with `string Query`, `IReadOnlyList<QuickOpenItem> Results` (top 50, re-ranked on every keystroke, synchronous), `int SelectedIndex`, `Task ConfirmAsync()`; factories `ForTables(Session)` (live schema), `ForAllConnections(MetadataCache, configs)` (labels `connection · schema.table`, selecting connects first), `ForColumns(TableTabViewModel)`.

- [ ] **Step 1: Failing tests:** `ForTables_RanksFuzzy_AndOpensTable`; `ForAllConnections_UsesCacheOnly_DoesNotConnect` (assert `SessionManager` untouched until confirm); `ForAllConnections_ConnectionNeverOpened_HasNoResults`; `ForAllConnections_ConfirmConnectsThenOpens`; `ForColumns_ReturnsMatchingColumn`; `Results_UpDownEnterEscape` (index wraps, Esc clears).
- [ ] **Step 2:** Implement overlay (centered, auto-focused text box, list below, Up/Down/Enter/Esc), wire `Ctrl+T`, `Ctrl+Shift+T` at the window; `Ctrl+L` only when a table tab is active. When no tab is active, `Ctrl+T` uses the connection selected in the tree.
- [ ] **Step 3:** `dotnet test`; manual: open palette, type a partial table name, confirm result opens in < 100 ms perceived. Commit.

---

### Task 14: Script editor tab

**Files:** Create `ViewModels/ScriptTabViewModel.cs`, `Views/ScriptTabView.axaml`, `Scripting/ScriptSessionStore.cs` (in Core, persists unsaved scripts); Test `tests/DbExplorer.App.Tests/ScriptTabViewModelTests.cs`, `tests/DbExplorer.Core.Tests/ScriptSessionStoreTests.cs`

**Interfaces:**
- Consumes: `ScriptSplitter`, `ScriptRunner`, `ReadOnlyGuard`, `Session`, `ConnectionConfig.ReadOnly`
- Produces:
  - `ScriptTabViewModel : TabViewModel` with `string Text`, `string? FilePath`, `int CaretOffset`, `(int Start,int End)? Selection`, `Task RunStatementAsync()` (`Ctrl+Enter`: selection if any, else statement at caret), `Task RunScriptAsync()` (`F5`), `void Cancel()`, `ObservableCollection<StatementResultViewModel> Results`, `bool IsRunning`, `Task SaveAsync(string? path)`, `Task OpenAsync(string path)`
  - `class ScriptSessionStore(string dir)`: `void Save(Guid tabId, Guid connectionId, string text, string? filePath)`, `IReadOnlyList<SavedScript> LoadAll()`, `void Delete(Guid tabId)`; app restores scripts on start.

- [ ] **Step 1: Failing tests:** `RunStatement_UsesStatementUnderCaret`; `RunStatement_UsesSelection`; `RunScript_RunsAllAndCollectsResults`; `RunScript_StopsAtFirstError_ShowsMessage`; `ReadOnlyConnection_RefusesWrite`; `Cancel_StopsRunning_IsRunningFalse`; `Run_DoesNotBlockUiThread` (VM exposes `IsRunning` true while awaiting a slow query); `Save_WritesFile_AndOpen_ReadsIt`; `SessionStore_RoundTrips_AndRestoresUnsavedText`.
- [ ] **Step 2:** Implement. View: AvaloniaEdit editor with SQL highlighting (built-in SQL definition), results area with one DataGrid per result set (reuse the read-only grid control from Task 11), status line `N rows · 12 ms` or `N rows affected`, "load more" when `Truncated`. `Ctrl+N` opens a script tab on the active connection (prompt to pick when none).
- [ ] **Step 3:** `dotnet test`; manual run on SQLite, Postgres, and SQL Server (a script with `GO`). Commit.

---

### Task 15: Polish, performance check, publish

**Files:** Create `README.md`, `publish.ps1`; Modify app icon/title, `src/DbExplorer.App/*.csproj` (publish settings)

- [ ] **Step 1:** Keyboard/UX pass: tab close (`Ctrl+W`), next/prev tab (`Ctrl+Tab`), refresh (`F5` in table tab, reload schema), error toasts instead of dialogs for connection failures, remember window size/position and open tabs' connections (not table data).
- [ ] **Step 2:** Measure and record in README: cold start to window shown, idle RAM with 3 connections and 3 table tabs open (Task Manager "Memory"), scrolling a 200-row grid. If RAM > 150 MB or start > ~1 s, profile (trim unused Avalonia/AvaloniaEdit assets, ReadyToRun, avoid loading schemas eagerly) and re-measure.
- [ ] **Step 3:** `publish.ps1`: `dotnet publish -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true` into `dist\`; test that the published exe starts on a clean config dir and imports DBeaver config.
- [ ] **Step 4:** Run the whole suite `dotnet test`, confirm all pass (integration tests skipped unless env vars set; run them once with `DBX_PG` and `DBX_MSSQL` pointing at local servers). Commit.
