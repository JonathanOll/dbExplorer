# dbExplorer UI: design tokens and conventions

The look is DBeaver-inspired but light: one tinted navigator, a darker document tab strip, a clean content area, thin borders
and a visible splitter, so the regions of the window are told apart at a glance. Everything is defined in three files under
`src/DbExplorer.App/Styles/`, loaded at startup from `App.axaml`:

| File | Content |
| --- | --- |
| `Theme.axaml` | colour tokens (light and dark `ThemeDictionaries`), sizes, fonts, and the re-pointing of the Fluent control brushes to the tokens |
| `Icons.axaml` | about 40 vector icons as `StreamGeometry` (24x24, Material-style). No icon font, no bitmaps |
| `Controls.axaml` | control themes (tool button, document tabs, secondary tabs) and style classes (banners, badges, key caps, bands) |

Screenshots of every screen, before and after, light and dark: [`before/`](before) and [`after/`](after).
Regenerate them with `dotnet run --project tools/UiShots -- <folder>` (in-process, headless, synthetic data in a temp folder,
no real window and no input). See "Screenshots" below.

## Rules

1. **No colour in a view.** Use a `Dbx*` brush with `{DynamicResource ...}` (dynamic, so a live Windows light/dark switch
   repaints). Geometries, sizes and fonts may be `StaticResource`. In code, get a brush with `GetResourceObservable` or a style
   class, never a hex value (the only literals left are the translucent edit-state overlays of grid cells).
2. **Icons are vectors from `Icons.axaml`**: `<PathIcon Data="{StaticResource IconTable}" />`, tinted with `Foreground`.
   A toolbar button is `Theme="{StaticResource DbxToolButton}"` with `Tag="{StaticResource IconX}"` (icon) and optional
   `Content` (text). Every icon-only button has a tooltip that includes its shortcut.
3. **Disabled means dimmed + a tooltip that says why** (`ToolTip.ShowOnDisabled="True"`).
4. **Status is coloured and has an icon**: banners use `Border.banner` + `error` / `warn` / `info` and a `PathIcon.bi`.
5. **Dialogs**: header band (`DbxPanelHeaderBg`), body on `DbxContentBg`, footer band (`DbxWindowBg`) with the buttons on the
   right; the primary button has `Classes="accent"`. Default / cancel buttons are never changed by styling.
6. **Keep startup lean**: no heavy templates, effects, large fonts or images. The AvaloniaEdit theme stays inside
   `ScriptTabView` (lazy). A drop shadow only on the quick-open card.

## Tokens

Sizes: UI font 13 px (Segoe UI, the system font), small 11.5, grid 12.5 px in Consolas, tree and grid rows 24 px, toolbar 34 px,
corner radius 3 (cards 8). Accent: `#3B6EA5` light, `#4F8FD0` dark.

| Token | Light | Dark | Use |
| --- | --- | --- | --- |
| `DbxWindowBg` | `#E6EBF1` | `#1A1D21` | window, status bar, dialog footers |
| `DbxNavBg` | `#EEF2F7` | `#21252B` | navigator (tinted) |
| `DbxPanelHeaderBg` | `#DDE4ED` | `#2B3038` | panel title strips, dialog headers, status bar |
| `DbxTabStripBg` | `#D3DBE6` | `#14161A` | document tab strip (darker than content) |
| `DbxContentBg` | `#FFFFFF` | `#272B31` | editor / grid area, active tab |
| `DbxToolbarBg` | `#F5F7FA` | `#2E333A` | toolbars, secondary tab strip, cards |
| `DbxInputBg` | `#FFFFFF` | `#1E2126` | text boxes, list frames |
| `DbxBorder` / `DbxBorderSubtle` | `#BCC6D3` / `#DAE0E8` | `#444B55` / `#343A43` | region borders / inner lines |
| `DbxSplitter` | `#C9D1DC` | `#3C434D` | grid splitters (accent on hover) |
| `DbxText` / `DbxTextDim` / `DbxTextFaint` | `#1B2430` / `#566272` / `#7F8B9B` | `#E3E8EE` / `#A0AAB8` / `#76828F` | text levels |
| `DbxAccent` / `DbxAccentStrong` | `#3B6EA5` / `#2D5887` | `#4F8FD0` / `#74A9E2` | accent strip, links, focus |
| `DbxAccentButtonBg` / `DbxOnAccent` | `#3B6EA5` / `#FFFFFF` | `#3A78BB` / `#FFFFFF` | primary buttons |
| `DbxAccentSoft` | `#D2E2F4` | `#2D4A6B` | selected navigator row, hovered card |
| `DbxHover` / `DbxPressed` | `#E0E6EE` / `#CFD8E3` | `#323841` / `#3C434E` | hover / pressed |
| `DbxGridHeaderBg` / `DbxGridFilterBg` | `#E2E8F0` / `#F6F8FB` | `#2F353D` / `#23272D` | name band over lighter filter band |
| `DbxGridLine` / `DbxGridZebra` | `#E7EBF1` / `#F4F7FB` | `#333941` / `#2C3137` | row lines, alternate rows |
| `DbxRowHover` / `DbxRowSelected` / `DbxRowSelectedInactive` | `#EDF2F8` / `#CCDFF4` / `#DCE6F1` | `#30363E` / `#2E4C72` / `#303F52` | grid rows |
| `DbxError` + `Bg` / `Border` / `Text` | `#C62828` `#FDECEA` `#F0B4AF` `#8A1C1C` | `#F2766E` `#43231F` `#7C3B37` `#FFB8B2` | error banner and icon |
| `DbxWarn` + `Bg` / `Border` / `Text` | `#B26A00` `#FFF3D6` `#EBCE87` `#664000` | `#E8B04C` `#40351A` `#7A6530` `#F5D78F` | warning banner, read-only badge |
| `DbxInfo` + `Bg` / `Border` / `Text` | `#2467A8` `#E4EFFB` `#B3D1EF` `#163F69` | `#7DB4EC` `#1F3249` `#3B5F88` `#B9D7F6` | info banner |
| `DbxSuccess`, `DbxRun`, `DbxStop` | `#2A8C4C`, `#2A8C4C`, `#C62828` | `#4CC27A`, `#4CC27A`, `#F2766E` | connected dot, run, cancel |
| `DbxEnginePostgres` / `SqlServer` / `Sqlite` | `#336FA8` / `#A02A30` / `#14907F` | `#5B9BD5` / `#E26D6D` / `#3FC1B0` | engine icons |
| `DbxIconFolder` / `Table` / `View` / `Key` | `#D69E2E` `#4C739E` `#8A64B8` `#C7971A` | `#E2B24F` `#7FA3CC` `#B49BE0` `#E0BD4A` | icon tints |
| `DbxLink` | `#2B63A8` | `#6FAAE8` | foreign-key values, links |
| `DbxScrim` | `#66101820` | `#A0000000` | quick-open backdrop |

Grid cell states (translucent so they read on both themes): edited cell amber `#66F2B600`, new row green `#4429A329`,
deleted row red `#55B3261E` with strike-through, NULL dim italic.

## Components

- **Navigator**: `Border.panelHeader` (title + `DbxToolButton` icon buttons), `TreeView` with 24 px rows. Row = colour chip,
  engine icon (`EngineIcon`), name (bold when connected), green dot, lock (read-only), `failed` badge. Folders show a
  connection count.
- **Document tabs**: `DbxDocTabControl` / `DbxDocTabItem`; `TabStripPanel` shrinks tabs instead of wrapping. Active tab = content
  colour + 3 px accent strip. Header = colour chip, type icon (`TabViewModel.IconKey`), title, dirty dot
  (`HasUnsavedWork`), lock, close button (only on the hovered or selected tab).
- **Secondary tabs** (Data / Structure, script results): `DbxSubTabControl` / `DbxSubTabItem`, underline style.
- **Table tab**: banners, then the tab strip, then one toolbar (reload, paging, count | add, delete, save, revert | define key,
  read-only badge), the WHERE row, the grid. The grid header is a darker name band over a lighter filter band.
- **Script tab**: toolbar (run, run all, cancel | run on group, script on group, only for a connection in a group | row limit |
  open, save | connection badge), editor (per-theme syntax colours), splitter, result tabs with a result line (`N rows · X ms`),
  error banners. Chords: `Ctrl+Enter` / `F5` run here, `Ctrl+Shift+Enter` / `Ctrl+Shift+F5` run on the group (`ScriptTabKeys`).
- **Group run panel** (below the editor, replaces the result tabs while a group run is shown): a toolbar strip with one `badge` per
  database (state icon: `IconSchedule` waiting, `IconRefresh` running, `IconCheck` done, `IconError` failed, `IconStop` cancelled;
  name, rows and time or error, a close button that cancels that database) and "Cancel all"; a `banner warn` with the notices; then
  secondary tabs: "All" (a "Differences only" check box, a read-only `ResultGrid` with a leading `Database` / `Present in` column,
  a result line) and one tab per database (the same content as a statement result tab).
- **Database selector** (`GroupSwitcher`, in table and script tabs): group icon, a group picker (only when the connection is in
  several groups), previous / next chevrons and a combo of the group's databases (engine icon, name, lock for read-only). Shown
  only when the tab's connection is in a group with other members: in the table tab as a toolbar strip above the banners, in the
  script tab in place of the connection badge. Ctrl+Alt+PageDown / PageUp switch to the next / previous member.
- **Status bar**: severity icon + message, busy spinner + cancel, active connection (engine icon, name, engine, read-only
  badge), shortcut hints.
- **Quick open**: shadowed card, big search box, icon + label + dim detail rows, keyboard-hint footer.

## Screenshots

`tools/UiShots` is a dev tool outside the solution. It starts the real `App` on Avalonia's headless platform with Skia,
builds a synthetic data folder in the temp directory (two folders, seven connections, a SQLite file with a 500 row table and
foreign keys), renders each scene and writes a PNG. Nothing is sent to the desktop and no real config is read.

```powershell
dotnet run --project tools/UiShots -- <outDir> [scene,scene...|all] [light|dark]
dotnet run --project tools/UiShots -- --scroll 3        # scroll benchmark of a 200 x 20 grid, prints ms per frame
```

Scenes: `empty`, `main-table`, `table-edit`, `table-structure`, `script`, `script-error`, `quick-open`, `connection-dialog`,
`sql-preview`, `virtual-key`, `confirm-dialog`, `live-theme-switch` (flips the theme while the window shows),
`main-table-125` (1024x640 DIP, the 1280x800 window at 125 percent scaling), and for linked groups `groups-tree` (the Groups
section of the navigator), `group-editor`, `group-editor-warning` (one member + a name clash: warning and error bands) and
`group-schema-diff` (schema-difference report with an unreachable member), `group-switch-table` and `group-switch-script`
(the database selector in a table and a script tab), `group-run` and `group-run-differences` (a read-only run of one statement on
three databases: status panel and All / Differences). The `group-*` scenes use a demo config with three
groups; the Group icon is `IconGroup` / `IconGroupAdd` in `Icons.axaml`. The navigator is 304 px wide by default (it holds one
more toolbar button than the "before" images). The "before" images were made from the same tool
at the previous UI (commit `024c5ff`, with the one grid fix of commit `a5d3399`, without which a second table tab showed no grid).
If writing a PNG straight into the repository folder fails on your machine, render into another folder and copy.
