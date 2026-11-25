# Development

Building, testing and publishing dbExplorer, and how its start-up time was measured. Rules for contributors and agents are
in [`CLAUDE.md`](../CLAUDE.md); UI conventions in [`ui/README.md`](ui/README.md).

## Build, test, publish

Requirements: Windows 10/11, the .NET 10 SDK, and PowerShell 7 (`pwsh`) to run `publish.ps1` and `scripts\measure-startup.ps1`
(they will not run in Windows PowerShell 5.1).

```powershell
dotnet build DbExplorer.sln -c Release
dotnet test  DbExplorer.sln                 # 980+ tests; DB integration tests are skipped unless configured, see below
dotnet run --project src/DbExplorer.App -c Release
```

Fastest-launching build, into `dist\`:

```powershell
.\publish.ps1                      # self-contained, ReadyToRun composite, ~160 MB, launches in ~0.26 s
.\publish.ps1 -FrameworkDependent  # ~57 MB, needs the .NET 10 runtime, launches in ~0.32 s
```

`publish.ps1` cleans `dist\` and the `obj\` folders, publishes `win-x64`, prints the exe path and size, then launches the
published exe 5 times against a throw-away data dir and prints the startup numbers (use `-Runs N` for more).
Do not enable trimming: Avalonia bindings and the database providers use reflection.

Script of the installer: `pwsh .\scripts\test-install.ps1 [-Engine pwsh]` runs the real `install.ps1` against throw-away
folders and a throw-away registry key (it needs network, downloads the latest release a few times, and checks that
nothing else was touched; the GitHub CLI is only needed for its `-UseGh` and fallback scenarios).

## UI screenshots

`dotnet run --project tools/UiShots -- <outDir> <scene>[,scene] [light|dark]` renders headless PNGs of the scenes defined in
[`tools/UiShots/Program.cs`](../tools/UiShots/Program.cs). The published ones are in [`ui/after/`](ui/after).

## Integration tests against real databases

The unit tests use fakes and SQLite. The Postgres and SQL Server driver tests also have integration variants that are
skipped unless an environment variable holds a connection string to a scratch database. They create uniquely named
throw-away schemas and drop them afterwards, but do not point them at a database you care about.

```powershell
$env:DBX_PG    = 'Host=localhost;Port=5432;Database=scratch;Username=postgres;Password=...'   # Npgsql connection string, PostgreSQL 11+
$env:DBX_MSSQL = 'Server=localhost;Database=scratch;User Id=sa;Password=...;TrustServerCertificate=True'   # ADO.NET connection string
dotnet test tests/DbExplorer.Core.Tests
```

## Startup performance

The pass/fail bar from the design is a usable window in under about 1 s. Measured on the dev machine (Windows 11,
Release, win-x64) with `scripts\measure-startup.ps1`: it launches the exe with a throw-away `--data-dir` holding a
synthetic 8-connection config, polls the process's own top-level windows until one is visible (no input is ever sent),
reads the working set 3 s later, kills the process. Times are process start to main window visible. A trace inside the
app showed the first painted frame lands about 55 ms after that moment. Median of 10 warm launches.

| Build | Warm median (min-max) | First launch of a fresh build ("cold") | Working set |
| --- | --- | --- | --- |
| Baseline: framework-dependent, JIT (the build before this work) | 716 ms (704-833) | 4 037 ms (2 315 for a fresh publish of the same code) | 89 MB |
| + ReadyToRun (app and all dependencies) | 430 ms (415-443) | 1 343 ms | 82 MB |
| + config load on a worker thread | ~385 ms | ~1 350 ms | 77-82 MB |
| + ReadyToRun composite, framework-dependent | ~325 ms (316-336) | 4 085-4 204 ms | 82 MB |
| **+ ReadyToRun composite, self-contained (published default)** | **260 ms (256-290)** | **3 747-8 730 ms** | **84 MB** |

Restoring open tabs costs nothing before the window is visible: the session file is read on the same worker thread as the
config, and the tabs are rebuilt after the window opened (as shells; only the active table tab connects and queries, in
the background). A/B on a plain Release build (`measure-startup.ps1 -SessionTabs 6`: six table tabs on unreachable servers
plus a script, 12 launches per series, two rounds, the machine was noisy): warm min 766 / 874 ms before, 754 / 863 ms with
the change and no session file, 766 / 768 ms with a six-tab session; medians moved by the machine's noise (785-1099 ms in
every series), not by the feature.

The warm column is the one that matters day to day. The "cold" column is the very first launch after a build or publish
(a new copy of the files); it is dominated by Windows Defender's one-time scan of the new files, not by our code, and
**the composite images make it worse** (about 4-9 s instead of 1.3 s with plain ReadyToRun: the composite image is one
large new file, 24 MB framework-dependent, ~100 MB self-contained). It happens once per publish, and `publish.ps1`'s own
measurement launch absorbs it, so the first launch you do yourself is already a warm one. A true cold start after a
reboot (file cache empty, Defender already done) could not be measured without administrator tools (RAMMap), so the
"under 1 s from a cold disk" bar is verified for warm launches only. If a slow first launch after each publish matters
more than 60 ms per launch, publish with plain ReadyToRun instead (remove `PublishReadyToRunComposite`).
What was tried, and what was kept:

- **ReadyToRun: kept.** The biggest win (716 to 430 ms): the app's own code is no longer JIT-ed (0 of the ~2600 JIT-ed
  methods were ours; they are Avalonia generics and P/Invoke stubs).
- **ReadyToRun composite: kept.** Putting app, Avalonia and the dependencies in one image lets the compiler precompile
  Avalonia's generic instantiations over its own types: JIT-ed methods 2609 to ~1200, about 385 to ~330 ms (A/B on the same
  build, measured before the worker-thread config load).
- **Self-contained (with the runtime in the composite): kept as the publish default.** A further ~65 ms (325 to 260 ms)
  for 160 MB of disk instead of 57 MB; `-FrameworkDependent` is the small alternative.
- **Config load on a worker thread: kept.** Overlaps the ~45 ms config read with Avalonia's ~90 ms platform setup
  (428 to 385 ms). Warming Avalonia control types on a worker thread was tried and rejected: their static constructors
  need platform services of the UI thread (`TypeInitializationException`, process crash).
- **Tiered compilation settings: no effect, left as is.** `TieredPGO=false` and `TieredCompilationQuickJitForLoops=true`
  (already in the csproj) are neutral at startup (PGO on: 430 ms, off: 430 ms); `TieredCompilation=0` is slower
  (589 ms), `TC_QuickJitForLoops=0` is slower (493 ms), `gcConcurrent=0` and a 64 MB gen0 budget do not help.
- **Rendering: software kept as the default.** GPU/ANGLE (`DBEXPLORER_RENDERING=gpu`) starts ~100 ms later (367 ms vs
  260 ms) and uses about 80 MB more RAM (164 MB vs 84 MB). Scrolling a 200 row x 20 column grid by script (360 frames,
  rows and columns) was the same in both: mean frame 17.0 ms software vs 17.7 ms GPU, p95 ~30 ms in both, so the frame
  time is UI-thread layout, not painting.
- **Avalonia/runtime options: rejected, no measurable gain.** `UseWin32().UseSkia()` instead of `UsePlatformDetect()`,
  an explicit default font, `RedirectionSurface` composition only, `InvariantGlobalization`, `UseNls`. There is no custom
  font or `WithInterFont` (the system font is used).
- **Provider assemblies:** Npgsql and Microsoft.Data.SqlClient are not loaded at startup (checked from the loaded-assembly
  list); they load on the first connection of that kind.
- **Linked groups, reopen-closed-tab and fuzzy search: nothing runs before the first paint.** Groups are a few lines of the
  small `config.json` read on the worker thread; the Groups section of the tree is built in memory from it (no I/O, no
  database), and the run, switch, schema-check, reopen and quick-open code only runs on a command. A/B of the published exe
  against the build before this work, interleaved on the same (noisier than above) machine: 316 / 318 ms vs 303 / 323 ms.
- **Trimming / single-file: not used** (Avalonia bindings and the providers use reflection, and it did not look necessary).
