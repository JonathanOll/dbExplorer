# dbExplorer UI: design tokens and conventions

The look is calm and neutral: **one chrome tone** (window, navigator, panel headers, tab strip, status bar) around a lighter
**content area** (editor, grid, toolbars, inputs). Regions are separated by 1 px hairlines and spacing, not by five greys.
Greys are cool with a faint blue tint; a single accent (indigo blue) marks selection, focus, the primary button and the active tab.
Shapes are soft (radius 6 for controls, 10 for cards and flyouts, pill badges). Everything is defined in three files under
`src/DbExplorer.App/Styles/`, loaded at startup from `App.axaml`:

| File | Content |
| --- | --- |
| `Theme.axaml` | colour tokens (light and dark `ThemeDictionaries`), sizes, fonts, radii, and the re-pointing of the Fluent control brushes to the tokens |
| `Icons.axaml` | about 40 vector icons as `StreamGeometry` (24x24, Material-style). No icon font, no bitmaps |
| `Controls.axaml` | control themes (tool button, document tabs, sub tabs), style classes (buttons, inputs, bands, banners, badges, key caps, cards, empty state, spinner) |

Screenshots of every screen, light and dark: [`before/`](before) (original), [`after/`](after).
Regenerate with `dotnet run --project tools/UiShots -- <folder>` (in-process, headless, synthetic data in a temp folder,
no real window and no input). See "Screenshots" below.

## Rules

1. **No colour in a view.** Use a `Dbx*` brush with `{DynamicResource ...}` (dynamic, so a live Windows light/dark switch
   repaints). Geometries, sizes and fonts may be `StaticResource`. In code, get a brush with `GetResourceObservable` or a style
   class, never a hex value (grid cell states: use `DbxCellEdited` / `DbxCellNew` / `DbxCellDeleted` / `DbxCellError`).
2. **Icons are monochrome vectors from `Icons.axaml`**: `<PathIcon Data="{StaticResource IconTable}" />`, tinted with `Foreground`
   (`DbxIcon`, or inherit the text colour). Colour only carries meaning: run green, stop and error red, warning amber, info and
   accent blue, success green. Engine icons (`EngineIcon`) are neutral: the per-connection colour chip is the only colour identity
   of a connection. Table and view differ by shape (`IconTable` / `IconView`), not by hue.
3. **Buttons**: one primary per dialog or toolbar (`Classes="accent"` or `primary`), the rest secondary (default), `subtle` or
   `ghost`; toolbars use `Theme="{StaticResource DbxToolButton}"` with `Tag="{StaticResource IconX}"` (icon) and optional
   `Content`. Every icon-only button has a tooltip that includes its shortcut. Destructive confirmation = `Classes="danger"`.
   Default / cancel buttons are never changed by styling.
4. **Disabled means dimmed + a tooltip that says why** (`ToolTip.ShowOnDisabled="True"`).
5. **Status is coloured and has an icon**: banners use `Border.banner` + `error` / `warn` / `info` / `success` and a `PathIcon.bi`.
   Inline field validation: the input gets `Classes="error"` and a `TextBlock.fielderror` line under it.
6. **Dialogs**: header band (`DbxPanelHeaderBg`), body on `DbxContentBg`, footer band (`DbxWindowBg`) with the buttons on the
   right; the primary button has `Classes="accent"`.
7. **Keyboard focus is always visible**: Button, ToggleButton, CheckBox, RadioButton, ListBoxItem, TreeViewItem get a 2 px accent
   ring (`FocusAdorner`, keyboard only); TextBox / ComboBox get an accent border plus a soft glow; tabs an inset ring. Do not set
   `Focusable="False"` or your own focus visuals on a control a user can tab to.
8. **Keep startup lean**: no heavy templates, effects, large fonts or images. Transitions are 120 ms on Background / BorderBrush
   only. The AvaloniaEdit theme stays inside `ScriptTabView` (lazy). A drop shadow only on the overlay cards (quick open, search, command palette).
9. **Settings override tokens: views must not hard-code what a setting changes.** These values are replaced at run time by
   `AppearanceApplier` (Settings dialog, `Appearance/`): the accent family (`DbxAccent`, `DbxAccentStrong`, `DbxAccentButtonBg`,
   `DbxAccentButtonHover`, `DbxOnAccent`, `DbxAccentSoft`, `DbxAccentSoftHover`, `DbxFocusRing`, `DbxLink`, plus the tinted
   `DbxRowHover` / `DbxRowSelected` / `DbxRowSelectedInactive` / `DbxEditorSelection` / `DbxEditorCurrentLine`), `DbxGridRowHeight`,
   `DbxRowHeight`, `TreeViewItemMinHeight` (density 24 / 28 / 32), `DbxGridFontSize`, `DbxEditorFont`, `DbxEditorFontSize`,
   `DbxGridZebra` (transparent when shading is off) and `DbxUiScale`. A view reads them with `{DynamicResource ...}` and never
   writes `FontSize="13"` or a fixed row height for the SQL editor or the grids. Accent colours are changed in place on the
   existing brushes, so never copy an accent brush into a field: bind it. Every accent preset keeps 4.5:1 between `DbxOnAccent`
   and `DbxAccentButtonBg` (test `AccentContrastTests`).
10. **UI scale is a layout transform around each window's content** (`UiScaleHost`, `LayoutTransformControl` bound to
   `DbxUiScale`, inserted around the window's content presenter). Chosen over scaling the font tokens because hard-coded sizes,
   icons and row heights would not follow a font change, and over a render transform because that does not re-measure
   (clipping). Popups (menus, combo lists, tooltips) are separate top-levels and stay at 100 %. Check a new view at 125 %
   (`main-table-settings`, `script-settings` scenes).

## Sizes and shapes

UI font 13 px (Segoe UI, the system font), small 11.5, grid 12.5 px in Consolas, h1 20, h2 15. Tree row pitch 28, grid row 28,
toolbar 40, document tab strip 38, sub tab strip 36, text box / combo 32, button 30.

| Token | Value | Use |
| --- | --- | --- |
| `DbxRadius` (`ControlCornerRadius`) | 6 | buttons, inputs, menu items, tree and list rows, tooltips |
| `DbxRadiusSm` | 4 | key caps, small chips |
| `DbxCardRadius` (`OverlayCornerRadius`) | 10 | cards, dialogs, flyouts, menus, combo popups, quick-open card |
| `DbxPillRadius` | 99 | badges |
| `DbxRowHeight`, `DbxGridRowHeight` | 28 (**configurable**: 24 / 28 / 32) | tree pitch, data grid rows |
| `DbxToolbarHeight` / `DbxTabHeight` / `DbxSubTabHeight` / `DbxControlHeight` | 40 / 38 / 36 / 32 | |
| `DbxFontSize` / `DbxSmallFontSize` / `DbxGridFontSize` / `DbxH1FontSize` / `DbxH2FontSize` | 13 / 11.5 / 12.5 / 20 / 15 | |
| `DbxMonoFont`, `DbxCardShadow`, `DbxFocusGlow`, `DbxFocusGlowError`, `DbxFocusInset` | | font and box shadows (focus glow of inputs, inset ring of tabs) |
| `DbxEditorFont`, `DbxEditorFontSize`, `DbxUiScale` | Consolas, 13, 1.0 | **configurable** (Settings > Editor / Appearance), see rule 9 and 10 |

## Tokens

| Token | Light | Dark | Use |
| --- | --- | --- | --- |
| `DbxWindowBg` | `#F3F5F8` | `#181A1F` | window, status bar, dialog footers (chrome) |
| `DbxNavBg` / `DbxPanelHeaderBg` / `DbxTabStripBg` | = `DbxWindowBg` | = `DbxWindowBg` | navigator, panel headers, document tab strip: same chrome family, kept as separate names |
| `DbxContentBg` | `#FFFFFF` | `#1F2228` | editor / grid area, active tab, dialog body |
| `DbxToolbarBg` | `#FFFFFF` | `#1F2228` | toolbars, sub tab strip (= content on purpose; a hairline separates) |
| `DbxInputBg` | `#FFFFFF` | `#1A1D22` | text boxes, combo boxes |
| `DbxSurface` | `#FFFFFF` | `#262A32` | raised: cards, tooltips, menus, flyouts, combo popups |
| `DbxBorder` / `DbxBorderSubtle` / `DbxBorderStrong` | `#D3D8E0` / `#E5E8EE` / `#8992A1` | `#343841` / `#2A2E35` / `#6D7685` | control borders / hairlines between regions / check box and radio outlines (3:1) |
| `DbxSplitter` | `#F3F5F8` | `#181A1F` | splitter (chrome, accent on hover) |
| `DbxText` / `DbxTextDim` / `DbxTextFaint` | `#1B222C` / `#556070` / `#5E6877` | `#E6E9EE` / `#A4ACB9` / `#949DAB` | text levels. Dim and faint are at least 4.5:1 on window, content, input, toolbar and surface in both themes |
| `DbxAccent` / `DbxAccentStrong` | `#3E63DD` / `#3354C5` | `#7C97F5` / `#9BB0F8` | accent strip, links, selection marks / pressed |
| `DbxAccentButtonBg` / `DbxAccentButtonHover` / `DbxOnAccent` | `#3E63DD` / `#3556CC` / `#FFFFFF` | `#4565E0` / `#4C6BE3` / `#FFFFFF` | primary button |
| `DbxAccentSoft` / `DbxAccentSoftHover` | `#E4EAFD` / `#D8E1FB` | `#29335E` / `#313D70` | selected tree / list row, selected choice, hovered card |
| `DbxFocusRing` | `#3E63DD` | `#8CA3F7` | keyboard focus ring (4.8:1 or more on the backgrounds) |
| `DbxHover` / `DbxPressed` / `DbxButtonBg` | `#E9ECF1` / `#DEE2E9` / `#FCFCFD` | `#2B2F37` / `#343942` / `#2A2E36` | hover / pressed / secondary button fill |
| `DbxLink` | `#3556C4` | `#8AA3F7` | foreign-key values, links |
| `DbxGridHeaderBg` / `DbxGridFilterBg` | `#F5F7FA` / `#FBFCFD` | `#252930` / `#1D2025` | name band over the lighter filter band |
| `DbxGridLine` / `DbxGridZebra` | `#EDEFF3` / `#FAFBFD` | `#2B2F36` / `#22252C` | row lines, alternate rows |
| `DbxRowHover` / `DbxRowSelected` / `DbxRowSelectedInactive` | `#F1F4FA` / `#DCE5FD` / `#E8EDF8` | `#262A33` / `#2C3A6E` / `#2A3050` | grid rows |
| `DbxCellEdited` / `DbxCellNew` / `DbxCellDeleted` / `DbxCellError` | `#55F2B600` / `#4429A329` / `#44D32F2F` / `#C8372D` | `#55F2B600` / `#5538C26A` / `#66E5534B` / `#F2766E` | grid cell states: edited amber, new row green, deleted row red (with strike-through), error border. Translucent, so they read over zebra and selection; `DbxCellError` is opaque (border) |
| `DbxEditorSelection` / `DbxEditorCurrentLine` / `DbxEditorGutterText` / `DbxEditorGutterBorder` | `#C9D7FB` / `#F2F5FD` / `#8892A2` / `#E5E8EE` | `#34437F` / `#262B3A` / `#707A8A` / `#2A2E35` | SQL editor (the AvaloniaEdit theme in `ScriptTabView` should read these, not the grid tokens) |
| `DbxError` + `Bg` / `Border` / `Text` | `#C8372D` `#FDEDEB` `#F3C2BD` `#8F2A22` | `#F2766E` `#3A1F1E` `#6E3631` `#FFA39B` | error banner, badge, icon, `fielderror` |
| `DbxWarn` + `Bg` / `Border` / `Text` | `#B45F06` `#FFF5DC` `#EFD595` `#6B4200` | `#E8B04C` `#3A2E14` `#6E5A2A` `#F2C46D` | warning banner, read-only badge |
| `DbxInfo` + `Bg` / `Border` / `Text` | `#3E63DD` `#EAEFFD` `#C5D2F8` `#243B8F` | `#8CA3F7` `#222B52` `#3B4A8A` `#B3C2FB` | info banner |
| `DbxSuccess` + `Bg` / `Border` / `Text` | `#1A8044` `#E8F6EC` `#BCE0C9` `#165F36` | `#4CC27A` `#17301F` `#2A5C3A` `#85DDA5` | success banner and badge, connected dot |
| `DbxRun`, `DbxStop` | `#1A8044`, `#C8372D` | `#4CC27A`, `#F2766E` | run, cancel icons |
| `DbxDanger` / `DbxDangerHover` | `#C8372D` / `#B32F26` | `#C9382F` / `#CC3C33` | `Button.danger` fill (white text, 5:1) |
| `DbxIcon` | `#5F6B7B` | `#9AA3B2` | default icon colour |
| `DbxIconFolder` / `Table` / `View` / `Key` | `#A98B57` `#5E7391` `#7F70AE` `#B08718` | `#BBA371` `#8CA0BD` `#A196CC` `#D1AE4A` | muted icon tints (tables and views are told apart by shape) |
| `DbxThumb` / `DbxThumbHover` / `DbxThumbPressed` | `#C2C8D2` / `#98A1AF` / `#76808F` | `#4A505B` / `#6B7380` / `#8A92A0` | scroll bar thumb (not used by views) |
| `DbxScrim` | `#4D0F1626` | `#99000000` | quick-open backdrop |

The `DbxEnginePostgres` / `SqlServer` / `Sqlite` tokens are gone: engines are not colour coded.

## Vocabulary (classes and themes)

Themes (`Theme="{StaticResource ...}"`): `DbxToolButton`, `DbxDocTabControl` + `DbxDocTabItem`, `DbxSubTabControl` + `DbxSubTabItem`.

| Class | On | What it does |
| --- | --- | --- |
| *(none)* | `Button` | secondary: soft fill, thin border, rounded 6, min height 30 |
| `accent` (alias `primary`) | `Button`, `DbxToolButton` | primary: filled accent, white text |
| `danger` | `Button`, `DbxToolButton` | destructive confirmation: filled red, white text |
| `subtle` | `Button` | quiet filled, no border |
| `ghost` | `Button` | transparent, no border, rounded hover (plain text button in a toolbar) |
| `link` | `Button` | link colour text |
| `card` | `Button` | whole clickable card (welcome actions), hover = accent soft |
| `run` `stop` `link` `tint` `close` | `DbxToolButton` | coloured icon (green / red / link / accent) or 10 px close glyph |
| `segment` | `ToggleButton` | option chip, checked = accent soft fill + accent border. Put several in a horizontal `StackPanel` (Spacing 6) |
| `choice` | `ListBoxItem` | selectable card (engine picker): selected = accent soft fill + accent border. Use a horizontal `ListBox` |
| `error` | `TextBox`, `ComboBox` | error border and error focus glow; add `TextBlock.fielderror` under it |
| `bare` | `TextBox` | no frame, no glow (search box inside a card; `search` is also accepted) |
| `fielderror` | `TextBlock` | 11.5 px error text for an inline validation line |
| `h1` `h2` `title` `section` `dim` `faint` `small` | `TextBlock` | 20 semi-bold / 15 semi-bold / semi-bold / 11.5 caps-style dim label / dim / faint / 11.5 |
| `panelHeader` | `Border` | panel title strip: chrome, hairline below, 40 high |
| `toolbar` | `Border` | toolbar band: content colour, hairline below, 40 high, padding 8,4 |
| `vsep` | `Border` | 1 px vertical separator between toolbar groups |
| `banner` + `error` `warn` `info` `success` | `Border` | status band; its icon is a `PathIcon` with `Classes="bi"` |
| `badge` (+ `warn` `error` `info`/`ok` `success`) | `Border` | pill chip |
| `kbd` | `Border` > `TextBlock` | key cap; also fine inside a tooltip (`ToolTip.Tip` = horizontal `StackPanel` of a text and a `Border.kbd`) |
| `surface` | `Border` | raised panel: surface colour, hairline, radius 8, no padding |
| `card` | `Border` | `surface` + padding 16 + radius 10: standard container for a group of content |
| `empty` | `StackPanel` | empty state: centered, max width 380; children: `PathIcon` (36 px, faint), `TextBlock.h2` title, `TextBlock.dim` hint, optional `Button.accent` |
| `spin` | `PathIcon` | rotates while its own `IsVisible` is true (bind `IsVisible` on the icon itself, not on a parent) |
| `thin` | `ProgressBar` | 3 px bar |
| `danger` | `MenuItem` | error-coloured text and icon (Delete). Gesture text is already dim, right-aligned |
| `dim` | `PathIcon` | `DbxIcon` colour |

Stock controls that are restyled without a class: `TextBox`, `ComboBox` (+ popup), `CheckBox`, `RadioButton`, `ToggleButton`,
`ListBoxItem`, `TreeViewItem` (rounded rows with 2 px side margin), `MenuItem` / `ContextMenu` / `MenuFlyout`, `ToolTip`,
`ScrollBar` (slim, no arrows, thumb grows on hover), `ProgressBar`, `GridSplitter`, `DataGrid` (header, rows, selection,
current-cell frame: 2 px accent, inside the cell).

## Components

- **Navigator**: header "Connections" (normal case) with one "+" menu (connection, folder, group, DBeaver import), Refresh and
  Collapse; a filter box under it (clear button, `Esc` clears, `Down` enters the tree, `Ctrl+F` focuses it). `TreeView` with 28 px
  rows and rounded accent-soft selection. A connection row is the shared `ConnectionRow` (colour chip chosen by the user, a neutral
  engine icon with a state dot: green connected, ring disconnected, red failed; a trailing lock, spinner or failure icon; a
  tooltip with host, database and state). Folders and schemas show counts. The Groups section comes last. Empty state ("No
  connections yet") and "No match" state. Context menus are state-aware (Connect / Retry / Disconnect) with icons and gestures;
  `Delete` is `MenuItem.danger`. Drag a connection onto a folder or a group (the target is highlighted). Drag a folder before or
  after another folder (a 2 px accent insertion line; no nesting); the folder menu also has "Move up" / "Move down".
- **Environment** ([docs/environments.md](../environments.md)): always `views:EnvironmentBadge` (a word on a status-tinted pill:
  success, info, warn, error for DEV, TEST, PRE-PROD, PROD), wherever a connection is named. Script and table tabs of a
  production connection add a `banner error` line. The write confirmation (`ProductionDialog`) focuses Cancel. Scenes: `environment-*`.
- **Document tabs**: `DbxDocTabControl` / `DbxDocTabItem` (main window: `MainDocTabControl`, with a "+" new script button and a tab
  context menu: close, close others, close to the right, close all, reopen). Chrome strip with a hairline; tabs have rounded top
  corners; the active tab takes the content colour and a short accent bar on top. Header = colour chip, type icon
  (`TabViewModel.IconKey`), title, dirty dot (`HasUnsavedWork`), lock, close button.
- **Secondary tabs** (Data / Structure, script results): `DbxSubTabControl` / `DbxSubTabItem`, clean underline with a rounded
  accent bar under the active tab and a soft hover pill.
- **Table tab**: banners, the Data / Structure tabs, then an actions-only toolbar (reload, Filters toggle with a count | add row,
  delete (becomes "Delete 3 rows") | define key and the read-only badge docked right), the WHERE row (clear button, "Apply filters",
  "Not applied yet"), filter chips with "Clear all", the grid, a **pending-changes bar** (counts, "Revert", "Review and save…")
  and a **status strip** (paging `Alt+Left` / `Alt+Right`, "Rows 1–200 of N", "Count rows", elapsed time, "N rows selected").
  Overlays for loading, empty, no match and failed loads; a warning strip when the rows shown are from the previous query.
  The grid header is a name band (type-aware, sort in accent, context menu) over a lighter filter band with typed hints. Edited
  cells are bold with a "Was: …" tooltip; empty cells of a new row say `DEFAULT`. `Ctrl+C` / `Ctrl+Shift+C` copy, `Delete`, `Insert`,
  `Alt+Enter` opens the value viewer (full text, JSON indented, hex for binary).
- **Structure sub-tab**: columns, foreign keys and indexes share the height (`SharedHeightPanel`): a short section takes
  what it needs, the long ones split the rest and scroll their own rows (`ContentHeightDataGrid`). Never give a `DataGrid`
  an unbounded height (inside a `StackPanel` or a `ScrollViewer`): rows become unreachable and the scroll jumps.
- **Copy and export** (both grids, [docs/export.md](../export.md)): drag, `Alt+click` or `Shift+arrows` select a rectangle,
  `Ctrl+click` on a header or `Ctrl+Space` whole columns (cells lit like selected rows). The cell menu starts with Copy, Copy with
  headers, Advanced copy, "Copy as", Export and Select column. `ExportDialog` and `AdvancedCopyDialog` have the options on the
  left and a monospace preview on the right. Scenes: `grid-selection`, `export-dialog`, `export-dialog-sql`, `advanced-copy`.
- **Result comparison** (`CompareView`, [docs/compare.md](../compare.md)): a document tab. Toolbar (wraps): previous / next
  difference (`Shift+F8` / `F8`) with "2 of 6", "Only differences", the Key / Position segments, a Columns flyout, the text
  options and "Swap A and B". Under it the two sides, count badges, the matching rule, then warning banners (loaded rows only,
  duplicate keys) and an info banner (columns of one side). The grid marks rows through `IMarkedRow`: the first column says
  what happened in words, changed cells are bold and read "old → new", removed rows are struck through. Scenes: `compare`,
  `compare-differences`.
- **Script tab**: one-row toolbar: Run (accent) and Run all, "Run on group" menu, row-limit dropdown with presets, Statistics
  toggle, icon-only Open and Save, progress + elapsed time + Stop only while running, connection badge (+ read-only badge) and a
  shortcuts flyout. Editor with find (`Ctrl+F`), `Ctrl+/` comments, gutter marks per statement that ran. Results: a run summary
  line, result tabs titled `1 · SELECT … FROM orders`, a result line (`N rows · X ms`, "Showing first 1,000 rows" with
  "Raise limit"), error banners with "Go to statement" and "Copy" (amber with a lock when the read-only guard blocks a statement).
  Statements with nothing to show get no tab; a run made only of them shows a check mark with the outcome instead.
  Chords: `Ctrl+Enter` / `F5` run here, `Ctrl+Shift+Enter` / `Ctrl+Shift+F5` run on the group, `Esc` stops a run, `F6` switches pane,
  `Ctrl+1…9` pick a result tab (`ScriptTabKeys`).
- **Claude panel** (`ClaudePanelView`, 400 px right of the editor and results, 380 in the Upgrade dialog; only while the
  integration is on): title and access level, close; the request box (Generate), or what will be sent with its exact text
  (first question of a connection), then the proposed change (removed on error tint, added on success tint) above the answer.
  One primary button per step (Continue, Send to Claude, Apply). Scenes: `claude-preview`, `claude-diff`,
  `claude-generate`, `claude-plan`, `claude-upgrade`.
- **Group run panel** (below the editor, replaces the result tabs while a group run is shown): a header with a summary and a
  Close button; one clickable state chip per database (spinner while running, check, error, stop); info and warning banners
  split by severity; an "All rows | Differences" segmented control with a "N differ" / "Identical" chip, and one tab per database.
- **Database selector** (`GroupSwitcher`, in table and script tabs): group icon, a group picker (only when the connection is in
  several groups), previous / next chevrons and a combo of the group's databases. Shown only when the tab's connection is in a
  group with other members. Ctrl+Alt+PageDown / PageUp switch to the next / previous member.
- **Status bar**: severity icon + message (ellipsis; info and success fade after 8 s; warnings and errors stay with a dismiss
  button; click for the last 30 messages), busy spinner + Cancel only when the operation can be cancelled, the active connection
  with its state dot and a tooltip, a shortcuts button (`F1`, one list shared with the welcome page: `ShortcutCatalog`) and Settings.
- **ER diagram** (`DiagramView`): one-row toolbar (wraps in a narrow tab): zoom and Fit, the Columns segments, the schema
  filter flyout or the Depth stepper with "Show all"; on the right the table search with its "2 of 5" counter, Arrange,
  reload and "Export PNG…". The canvas is `FreeCanvas`, shared with the execution plan; `DiagramSurface` paints the cards
  (surface fill, header band, key icon, `FK` in link colour) from the theme tokens, accent for the selection and the hovered
  relations, the others dimmed. A footer counts tables and relations and says how to move and open them.
- **Quick open**: shadowed card, search box, table name (matched letters in bold) + schema, a connection badge ("connection /
  database" when they differ), an empty state, a result counter ("50 of 1,284"), keyboard-hint footer. Every typed word is
  marked on its own, in the name and in the context (`HighlightText`; the context chip marks only words it contains).
  `Ctrl+T` connects first when needed; when its search extends to the other connections, a `banner info` above the list says
  why and the rows of this connection get the badge too. Scenes: `quick-open`, `quick-open-extended`, `quick-open-words`.
- **Command palette** (`Ctrl+Shift+K`, `CommandPaletteOverlay`): a card like quick open (660 wide): search box (with the
  chosen command as a "back" chip on a second step), section captions (Recently used, Commands, Not available here), 34 px rows
  with icon, title (matched letters bold), a faint detail or the reason a command is greyed, category, key cap, chevron when a
  choice follows; keyboard-hint footer with a count. Commands live in `MainViewModel.Registry`
  (`MainViewModel.Commands.Catalog.cs`): a feature adds an `AppCommand` there or calls `Registry.Add`; providers read memory only.
  Scenes: `command-palette`, `command-palette-search`, `command-palette-matches`, `command-palette-choices`.
- **Search** (`Ctrl+Alt+F`, `SearchOverlay`): a card like quick open that stays up while a search runs: search box with the `Aa` /
  `ab` / `.*` toggles (scripts) or the match-mode combo (data), one accent Search button that becomes Stop while running, scope
  chips (Scripts | Data), the data filters (connection, schema, table name) and an info banner about long searches, a thin progress
  bar, results grouped under file / table headers (unsaved badge, match count), the hit in bold, a status line with spinner and key
  hints, a warn band when results are capped. Scenes: `search`, `search-data`.
- **Settings dialog** (`SettingsDialog`, Tools > Settings, `Ctrl+,`): a left list of category pages with a search box, the page on
  the right (title, subtitle, "Reset to defaults"), one "Close" button, no OK / Cancel (changes apply and save at once). A page is
  a `SettingsPageViewModel` listed in `SettingsPages.Create`; its rows are `SettingItemViewModel`s (toggle = check box, choice =
  segmented chips or a combo box, number = spin box that clamps) built with `Make.Toggle` / `Make.Choice` / `Make.Number`, which
  read and write through `SettingsService`. A generic page needs no XAML; a page with its own layout adds a `DataTemplate` before the
  generic one in `SettingsDialog.axaml`. The persisted values are sections of `AppSettings` (Core). Do not add an empty page.
- **Dialogs**: shared `DialogHeader` / `DialogFooter` (`DialogParts.cs`): icon tile, title, subtitle; footer with verb buttons
  ("Add connection", "Create group", "Save"). Inline field errors (`TextBox.error` + `TextBlock.fielderror`). Destructive
  confirmations use `ConfirmRequest` with `DialogTone.Danger`: a red verb button, a red icon, Cancel is the default and focused.

## Screenshots

`tools/UiShots` is a dev tool outside the solution. It starts the real `App` on Avalonia's headless platform with Skia,
builds a synthetic data folder in the temp directory (two folders, seven connections, a SQLite file with a 500 row table and
foreign keys), renders each scene and writes a PNG. Nothing is sent to the desktop and no real config is read.

```powershell
dotnet run --project tools/UiShots -- <outDir> [scene,scene...|all] [light|dark]
dotnet run --project tools/UiShots -- --scroll 3        # scroll benchmark of a 200 x 20 grid, prints ms per frame
```

Scenes: `er-diagram`, `er-diagram-focus`, `er-diagram-hover`, `er-diagram-large`, `er-diagram-export`, `empty`, `main-table`, `table-edit`, `table-structure`, `table-structure-wide`, `script`, `script-error`, `quick-open`, `quick-open-extended`, `quick-open-words`, `connection-dialog`, `connection-dialog-entra`, `connection-dialog-ssh`, `connection-dialog-tls`, `host-key-dialog`,
`sql-preview`, `virtual-key`, `history` (the Query history window), `search`, `search-data`, `format-sql`, `confirm-dialog`, `live-theme-switch` (flips the theme while the window shows),
`main-table-125` (1024x640 DIP, the 1280x800 window at 125 percent scaling), the Settings dialog (`settings-appearance`,
`settings-editor`, `settings-results`, `settings-history`, `settings-format`, `settings-shortcuts`, `settings-upgrade`, `settings-search`, `settings-appearance-125`), `settings-claude`, `settings-claude-activity`, `script-claude`,
`claude-preview`, `claude-diff`, `claude-generate`, `claude-plan`, `claude-upgrade`,
`main-table-settings` and `script-settings` (UI scale 125 %, purple accent, Compact density), the script preview of the Upgrade
dialog (`upgrade-scripts`, `upgrade-scripts-filter`, `upgrade-scripts-nomatch`), and for linked groups `groups-tree` (the Groups
section of the navigator), `group-editor`, `group-editor-warning` (one member + a name clash: warning and error bands) and
`group-schema-diff` (schema-difference report with an unreachable member), `group-switch-table` and `group-switch-script`
(the database selector in a table and a script tab), `group-run` and `group-run-differences` (a read-only run of one statement on
three databases: status panel and All / Differences). The `group-*` scenes use a demo config with three
groups; the Group icon is `IconGroup` / `IconGroupAdd` in `Icons.axaml`. The navigator is 304 px wide by default.
The "before" images were made from the same tool at the original UI (commit `024c5ff`, with the one grid fix of commit `a5d3399`,
without which a second table tab showed no grid). If writing a PNG straight into the repository folder fails on your machine,
render into another folder and copy.
