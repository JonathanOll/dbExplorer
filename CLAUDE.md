# dbExplorer: rules for agents

Windows desktop DB explorer (PostgreSQL, SQL Server, SQLite). .NET 10, Avalonia 11, MVVM with CommunityToolkit.Mvvm,
native ADO.NET drivers. Start-up speed and a calm, consistent UI are product features: protect both.

## Workflow

- After each development, once it is finished and `dotnet test` is green, commit and push directly to `main` (no branch,
  no PR). Never commit half-done work or failing tests. End commit messages with the attribution lines of the session.
- Commit messages: Conventional Commits (`feat(plan): ...`, `fix(grid): ...`). One topic per commit.
- Build and test from the repo root: `dotnet build` then `dotnet test`. `QuickOpenTests.Rank_FiveThousandItems_StaysFast`
  is timing-sensitive: if it fails only in the full run, rerun it alone before concluding anything.
- Look at the result of UI work: `dotnet run --project tools/UiShots -- <outDir> <scene>[,scene] [light|dark]` renders
  headless PNGs (scenes are in `tools/UiShots/Program.cs`). Open the PNG, light and dark. Final shots go to `docs/ui/after/`.
- Files are LF. Windows + Git Bash: write long multi-line scripts to a file instead of a heredoc with quotes.

## Orchestration: develop through sub-agents

The main agent is the orchestrator: it keeps its context clean for decisions, review and integration. As much as possible,
delegate the work itself to sub-agents (`Agent` tool) instead of doing it inline:

- Exploration, "where is X", broad reads: an exploration sub-agent that returns file:line answers, not file dumps.
- Implementation of a well-bounded slice (a parser, a view model, a view, a test class): one sub-agent per slice, with a
  self-contained brief: goal, files to touch, the rules above, the build/test command, what to report back. Independent
  slices run in parallel (separate files, no shared edits); dependent ones run in order.
- Verification (full `dotnet test`, UiShots screenshots and their review, code review of the diff): a sub-agent that
  reports pass/fail and only the failing output.
- The orchestrator reads the sub-agents' summaries, checks the diff (`git diff --stat`, the key files), integrates, runs the
  final build and test, then commits and pushes. It does not re-do or re-read what a sub-agent already settled.
- Small edits (a few lines in one or two files) are faster inline: do not spawn an agent for them.

## Layout (dependencies point down: App -> Core, never the reverse)

| Path | Content |
| --- | --- |
| `src/DbExplorer.Core` | No UI, no Avalonia. `Drivers/` (one `IDbDriver` per engine: all SQL dialect differences live there), `Sessions/` (one `Session` per open connection, `SessionManager`), `Scripting/` (splitter, runner, read-only guard, per-tab `ScriptConnection`, completion, statistics), `Plans/` (execution plans: parsers, layout, capture), `Querying/` (table paging, filters, edits), `Groups/`, `Upgrade/`, `Updates/`, `Config/`, `Model/` |
| `src/DbExplorer.App` | `ViewModels/` (all state and logic, UI-free and testable), `Views/` (`*.axaml` + thin code-behind), `Styles/` (theme tokens, icons, control styles), `Updates/`, start-up code at the root |
| `tests/DbExplorer.Core.Tests`, `tests/DbExplorer.App.Tests` | xUnit. App tests use Avalonia headless (`[AvaloniaFact]`) for views; view models are tested without a window |
| `tools/UiShots` | Headless screenshot generator |
| `docs/ui/README.md` | **UI rules: read before touching any view.** `docs/superpowers/` holds design specs and plans |

## Code rules

- Logic goes in view models or Core, never in code-behind. Code-behind only does what needs a visual (focus, scrolling,
  clipboard, file pickers, hit tests) and wraps `async void` handlers in try/catch.
- One idea per file; follow the neighbouring files' naming (`FooViewModel`, `FooView`, `FooDialog`, `Foo.axaml.cs`).
  Dialogs go through `IDialogService` / `IEditDialogs` / `IUpgradeDialogs` so tests can fake them.
- Engine differences belong in `IDbDriver` or a `switch` on `DbKind` inside Core, not in the view models.
- Database work runs off the UI thread; results come back through a channel or `await`. Never block the UI thread on a
  provider (SQLite runs synchronously). Always honour cancellation. User statements have no automatic command timeout.
- Write statements must pass the read-only guard (`ReadOnlyGuard`) on connections flagged read-only; an estimated plan runs nothing.
- A new persisted setting goes in `AppConfig` **and** is copied explicitly in `ConfigStore.Load` (it rebuilds the record field
  by field: a forgotten line silently loses the setting). Old files must still load (default values).
- Comments say why, not what. Public members get a one-line `///` summary when the intent is not obvious.

## UI rules (details in `docs/ui/README.md`)

- No hex colour in a view: `{DynamicResource Dbx...}` tokens only (live light/dark switch). Icons are the monochrome vectors
  in `Styles/Icons.axaml`. One primary button per toolbar or dialog. Disabled controls say why in a tooltip.
- Every icon-only button has a tooltip with its shortcut and an `AutomationProperties.Name`. Keyboard focus stays visible.
- Keep start-up lean: nothing heavy before first paint, no connection before first paint, lazy tree children.
- Result tabs of a script implement `IResultTab` and get a `DataTemplate` in `ScriptTabView.axaml`.
- Shortcuts are declared in three places that must agree: `MainShortcuts` / `ScriptTabKeys` (behaviour), `ShortcutCatalog`
  and the script shortcuts flyout (help), the table in `docs/shortcuts.md`.

## Tests

- Every behaviour change comes with a test in the matching project; bug fixes start with a test that fails.
- Prefer real SQLite files (`TreeFixture.CreateSqliteFile`, `ScriptTabFixture`) over mocks. SQL Server / PostgreSQL integration
  tests are opt-in through environment variables (`DBX_MSSQL`, ...), see `docs/development.md`: do not assume a server is available.
- Plan parsers are tested on hand-written showplan XML / EXPLAIN JSON (`tests/DbExplorer.Core.Tests/PlanTests.cs`).
