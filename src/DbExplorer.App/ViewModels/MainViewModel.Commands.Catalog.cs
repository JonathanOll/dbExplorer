using System.Windows.Input;
using DbExplorer.App.Appearance;
using DbExplorer.App.Settings;
using DbExplorer.Core.Export;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// The built-in commands of the palette, one method per category. Each is rebuilt when the palette opens, so titles stay put
/// while availability follows the current tab and connections. Providers read memory only.
/// </summary>
public partial class MainViewModel
{
    private void RegisterBuiltInCommands(CommandRegistry registry)
    {
        registry.Add(ScriptCommands);
        registry.Add(TableCommands);
        registry.Add(GridCommands);
        registry.Add(CompareCommands);
        registry.Add(PlanCommands);
        registry.Add(DiagramCommands);
        registry.Add(NavigateCommands);
        registry.Add(FileCommands);
        registry.Add(TabCommands);
        registry.Add(ConnectionCommands);
        registry.Add(FolderCommands);
        registry.Add(GroupCommands);
        registry.Add(ViewCommands);
        registry.Add(AppearanceCommands);
        registry.Add(ApplicationCommands);
    }

    // ---- helpers ----

    private static AppCommand Entry(string id, string category, string title, string icon, Func<Task> run,
        string? disabled = null, string? shortcut = null, string? keywords = null, string? detail = null) =>
        new(id, category, title, icon) { Run = run, DisabledReason = disabled, Shortcut = shortcut, Keywords = keywords, Detail = detail };

    private static AppCommand Pick(string id, string category, string title, string icon, Func<IReadOnlyList<AppCommand>> choices,
        string? disabled = null, string? keywords = null) =>
        new(id, category, title, icon) { Choices = choices, DisabledReason = disabled, Keywords = keywords };

    private static Func<Task> Do(Action action) => () =>
    {
        action();
        return Task.CompletedTask;
    };

    private static Func<Task> Do(ICommand command, object? parameter = null) => Do(() => command.Execute(parameter));

    private static string OnOff(bool on) => on ? "on" : "off";

    // ---- script ----

    private IEnumerable<AppCommand> ScriptCommands()
    {
        var s = ActiveScript;
        var none = s is null ? "Open a script tab first" : null;
        var run = none ?? s!.RunDisabledReason;
        var noGroup = none ?? (s!.HasGroupSwitch ? null : "This connection is not in a group with other databases");

        yield return Entry("script.run-statement", "Script", "Run statement", "IconPlay", () => s!.RunStatementAsync(), run,
            "Ctrl+Enter", "execute query selection");
        yield return Entry("script.run-all", "Script", "Run whole script", "IconPlayAll", () => s!.RunScriptAsync(), run, "F5", "execute all");
        yield return Entry("script.stop", "Script", "Stop the run", "IconStop", Do(() => s!.Cancel()),
            none ?? (s!.IsBusy ? null : "Nothing is running"), "Esc", "cancel abort");
        yield return Entry("script.run-statement-group", "Script", "Run statement on the group", "IconGroup",
            () => s!.RunStatementOnGroupAsync(), noGroup ?? run, "Ctrl+Shift+Enter", "all databases compare");
        yield return Entry("script.run-all-group", "Script", "Run script on the group", "IconGroup",
            () => s!.RunScriptOnGroupAsync(), noGroup ?? run, "Ctrl+Shift+F5", "all databases compare");
        yield return Entry("script.close-group-run", "Script", "Close the group run panel", "IconClose", Do(() => s!.CloseGroupRunPanel()),
            none ?? (s!.GroupRun is null ? "No group run is shown" : s.IsGroupRunning ? "A group run is in progress" : null));
        yield return Entry("script.format", "Script", "Format SQL", "IconFormat", Do(() => s!.FormatSql()), none,
            "Ctrl+Shift+F", "beautify indent pretty");
        yield return Entry("script.estimated-plan", "Script", "Show estimated execution plan", "IconSchema",
            () => s!.ShowEstimatedPlanAsync(), run, "Ctrl+L", "explain");
        yield return Entry("script.actual-plan", "Script", "Include actual execution plan", "IconSchedule",
            Do(() => s!.ActualPlanEnabled = !s.ActualPlanEnabled),
            none ?? (s!.CanUseActualPlan ? null : "SQLite only has an estimated plan"), "Ctrl+M", "explain analyze toggle",
            s is null ? null : OnOff(s.ActualPlanEnabled));
        yield return Entry("script.statistics", "Script", "Include execution statistics", "IconCount",
            Do(() => s!.StatisticsEnabled = !s.StatisticsEnabled),
            none ?? (s!.CanUseStatistics ? null : "Only SQL Server has IO and time statistics"), keywords: "io time toggle",
            detail: s is null ? null : OnOff(s.StatisticsEnabled));
        yield return Pick("script.row-limit", "Script", "Set row limit…", "IconFilter",
            () => ScriptTabViewModel.RowLimitPresets.Select(limit => Entry($"script.row-limit.{limit}", "Script",
                StatementResultViewModel.RowsText(limit), "IconFilter", Do(() => s!.RowLimit = limit),
                detail: s!.RowLimit == limit ? "current" : null)).ToList(),
            none, "maximum rows");
        yield return Entry("script.reconnect", "Script", "Reconnect this script", "IconRefresh", () => s!.ReconnectAsync(),
            none ?? (s!.NeedsReconnect ? null : "The connection was not lost"));
        yield return Entry("file.save-script", "Script", "Save script", "IconSave", () => s!.SaveAsync(), none, "Ctrl+S", "write file");
        yield return Entry("file.save-script-as", "Script", "Save script as…", "IconSave", () => s!.SaveAsAsync(), none, keywords: "export file copy");
    }

    // ---- table ----

    private IEnumerable<AppCommand> TableCommands()
    {
        var t = ActiveTab as TableTabViewModel;
        var none = t is null ? "Open a table first" : null;
        var pending = none ?? (t!.HasPendingEdits ? null : "No pending changes");

        yield return Entry("table.reload", "Table", "Reload the data", "IconRefresh", () => RefreshActiveTableAsync(), none, "F5", "refresh");
        yield return Entry("table.jump-column", "Table", "Go to column…", "IconColumn", Do(JumpToColumn),
            none ?? (t!.Columns.Count == 0 ? "The columns are not loaded" : null), "Ctrl+L", "jump find");
        yield return Entry("table.apply-filters", "Table", "Apply filters", "IconFilter", () => t!.ApplyFiltersAsync(), none, keywords: "where search");
        yield return Entry("table.clear-filters", "Table", "Clear all filters", "IconFilter", () => t!.ClearFiltersAsync(),
            none ?? (t!.HasActiveFilters ? null : "No filter is applied"), keywords: "remove reset where");
        yield return Entry("table.count", "Table", "Count rows", "IconCount", () => t!.CountAsync(), none, keywords: "total");
        yield return Entry("table.next-page", "Table", "Next page", "IconChevronRight", () => t!.NextPage(),
            none ?? (t!.HasNextPage ? null : "This is the last page"), "Alt+Right");
        yield return Entry("table.previous-page", "Table", "Previous page", "IconChevronLeft", () => t!.PrevPage(),
            none ?? (t!.PageIndex > 0 ? null : "This is the first page"), "Alt+Left");
        yield return Entry("table.add-row", "Table", "Add a row", "IconAdd", Do(() => t!.AddRow()),
            none ?? (t!.CanInsert ? null : t.AddRowTip), keywords: "insert new");
        yield return Entry("table.review-save", "Table", "Review and save changes…", "IconSave", () => t!.ReviewAndSaveAsync(),
            pending, "Ctrl+S", "apply commit edits");
        yield return Entry("table.revert", "Table", "Revert pending changes", "IconUndo", Do(() => t!.Revert()), pending, keywords: "discard undo");
        yield return Entry("table.define-key", "Table", "Define a key…", "IconKey", () => t!.DefineKeyAsync(),
            none ?? (t!.CanDefineKey ? null : "Only for a table without primary key, on a writable connection"), keywords: "virtual primary");
    }

    // ---- copy and export of the grid on screen ----

    private IEnumerable<AppCommand> GridCommands()
    {
        const string category = "Grid";
        var grid = ActiveTab is TableTabViewModel or ScriptTabViewModel or CompareTabViewModel ? FindActiveGrid?.Invoke() : null;
        var none = grid is not null ? null
            : FindActiveGrid is null ? "Not available here" : "Show a table or a query result first";
        var noDialogs = none ?? (grid!.HasExportDialogs ? null : "Not available for this grid");

        yield return Entry("grid.copy", category, "Copy", "IconCopy", () => grid!.CopyAsync(header: false),
            none ?? (grid!.HasCurrentCellOrSelection ? null : "Select cells or rows first"), "Ctrl+C", "clipboard value selection");
        yield return Entry("grid.copy-headers", category, "Copy with headers", "IconCopy", () => grid!.CopyAsync(header: true),
            none, "Ctrl+Shift+C", "clipboard column names rows");
        yield return Entry("grid.advanced-copy", category, "Advanced copy…", "IconCopy", () => grid!.AdvancedCopyAsync(),
            noDialogs, "Ctrl+Alt+C", "clipboard delimiter quoting options");
        yield return Pick("grid.copy-as", category, "Copy as…", "IconCopy",
            () => ResultFormats.CopyAs.Select(kind =>
            {
                var name = GridCopy.Name(kind);
                return Entry($"grid.copy-as.{kind}", category, char.ToUpperInvariant(name[0]) + name[1..], "IconCopy",
                    () => grid!.CopyAsAsync(kind),
                    kind == ExportKind.SqlUpdate && !grid!.HasKeyColumns ? ResultFormats.NoKeyReason : null);
            }).ToList(),
            none, "clipboard format csv tsv json markdown sql insert update in list column names");
        yield return Entry("grid.export", category, "Export to a file…", "IconSave", () => grid!.ExportAsync(),
            noDialogs, "Ctrl+E", "save csv tsv json excel xlsx sql markdown");
    }

    // ---- execution plan ----

    private IEnumerable<AppCommand> PlanCommands()
    {
        var plan = ActiveScript?.SelectedResultTab as PlanResultTab;
        var none = plan is null ? "Show an execution plan first" : null;

        yield return Entry("plan.zoom-in", "Plan", "Zoom the plan in", "IconAdd", Do(() => plan!.Zoom *= 1.2), none, "Ctrl++");
        yield return Entry("plan.zoom-out", "Plan", "Zoom the plan out", "IconRemove", Do(() => plan!.Zoom /= 1.2), none, "Ctrl+-");
        yield return Entry("plan.zoom-reset", "Plan", "Zoom the plan to 100%", "IconSearch", Do(() => plan!.Zoom = 1), none);
        yield return Entry("plan.side-panel", "Plan", "Show or hide the plan details panel", "IconInfo",
            Do(() => plan!.ShowSidePanel = !plan.ShowSidePanel), none, keywords: "properties issues toggle");
        yield return Entry("plan.pin", "Plan", "Pin this plan as the baseline", "IconCompare", Do(() => plan!.PinPlanCommand.Execute(null)),
            none ?? (plan!.CanPin ? null : plan.PinTooltip), keywords: "compare");
        yield return Entry("plan.compare", "Plan", "Compare with the pinned plan", "IconCompare",
            Do(() => plan!.ComparePinnedCommand.Execute(null)),
            none ?? (plan!.CanComparePinned ? null : plan.HasPinned ? "This plan is the pinned one" : "Pin a plan first"));
        yield return Entry("plan.close-comparison", "Plan", "Close the plan comparison", "IconClose",
            Do(() => plan!.CloseComparisonCommand.Execute(null)), none ?? (plan!.IsComparing ? null : "No comparison is open"));
    }

    // ---- ER diagram ----

    private IEnumerable<AppCommand> DiagramCommands()
    {
        const string category = "Diagram";
        var table = ActiveTab as TableTabViewModel;
        var diagram = ActiveTab as DiagramTabViewModel;
        var none = diagram is null ? "Open an ER diagram first" : null;

        yield return Pick("diagram.open", category, "Show ER diagram of…", "IconDiagram",
            () => Connections("diagram.open", category, node => Do(() => OpenDiagram(node.Config, new DiagramRequest()))),
            HasConnections ? null : "Add a connection first", "schema relations foreign keys entity database");
        yield return Entry("diagram.neighbours", category, "Show ER diagram around this table", "IconDiagram",
            Do(() => OpenDiagram(table!.Connection, new DiagramRequest(Table: table.Table))),
            table is null ? "Open a table first" : table.IsView ? "A view has no relations to draw" : null,
            keywords: "neighbours relations foreign keys");
        yield return Entry("diagram.reload", category, "Reload the diagram", "IconRefresh", () => diagram!.ReloadAsync(), none,
            keywords: "refresh schema");
        yield return Entry("diagram.arrange", category, "Arrange the diagram again", "IconSchema", () => diagram!.ArrangeAsync(), none,
            keywords: "layout tidy");
    }

    // ---- go to ----

    private IEnumerable<AppCommand> NavigateCommands()
    {
        var noConnection = HasConnections ? null : "Add a connection first";
        yield return Entry("go.table", "Go to", "Go to table…", "IconTable", Do(ShowTablePicker), noConnection, "Ctrl+T",
            "open view procedure function quick");
        yield return Entry("go.table-anywhere", "Go to", "Go to table in all connections…", "IconDatabase",
            () => ShowAllTablesPickerAsync(), noConnection, "Ctrl+Shift+P", "open everywhere quick");
        yield return Entry("go.search", "Go to", "Search in scripts and data…", "IconSearch", Do(() => ShowSearch()),
            shortcut: "Ctrl+Alt+F", keywords: "find");
        yield return Entry("go.search-scripts", "Go to", "Search in scripts…", "IconSearch", Do(() => ShowSearch(SearchScope.Scripts)),
            keywords: "find text");
        yield return Entry("go.search-data", "Go to", "Search in data…", "IconSearch", Do(() => ShowSearch(SearchScope.Data)),
            noConnection, keywords: "find value");
        yield return Entry("go.history", "Go to", "Query history…", "IconHistory", () => OpenHistoryAsync(),
            _history is null ? "The history is not available" : null, "Ctrl+Shift+H", "past statements");
    }

    // ---- files ----

    private IEnumerable<AppCommand> FileCommands()
    {
        var noTarget = CurrentConnection is null ? "Select a connection in the navigator first" : null;
        var noLibrary = _library is null || _recent is null ? "No script library here" : null;

        yield return Entry("file.new-script", "File", "New SQL script", "IconScript", Do(() => NewScript()), noTarget, "Ctrl+N", "query editor");
        yield return Pick("file.new-script-on", "File", "New SQL script on…", "IconScript",
            () => Connections("file.new-script-on", "File", node => Do(() => Tree.NewScriptOn(node))),
            HasConnections ? null : "Add a connection first", "query editor connection");
        yield return Entry("file.open-script", "File", "Open script file…", "IconFileOpen", () => OpenScriptFileAsync(), noTarget, "Ctrl+O");
        yield return Pick("file.recent-script", "File", "Open recent script…", "IconFileOpen",
            () =>
            {
                RefreshRecentScripts();
                return RecentItems.Select(r => Entry("file.recent:" + r.Path, "File", r.Name, "IconScript",
                    () => OpenRecentScriptCommand.ExecuteAsync(r.Path), detail: r.Folder)).ToList();
            },
            noLibrary);
        yield return Entry("file.clear-recent", "File", "Clear recent scripts", "IconDelete", Do(ClearRecentScriptsCommand), noLibrary);
        yield return Entry("file.library-folder", "File", "Open script library folder", "IconFolderOpen", Do(OpenLibraryFolderCommand),
            noLibrary, keywords: "explorer scripts");
        yield return Entry("file.exit", "File", "Exit dbExplorer", "IconClose", Do(() => ExitRequested?.Invoke()),
            ExitRequested is null ? "Not available here" : null, "Alt+F4", "quit close application");
    }

    // ---- tabs ----

    private IEnumerable<AppCommand> TabCommands()
    {
        var tab = ActiveTab;
        var none = tab is null ? "No tab is open" : null;
        var several = Tabs.Count > 1 ? null : "There is no other tab";

        yield return Pick("tab.switch", "Tabs", "Switch to tab…", "IconTable",
            () => Tabs.Select((t, i) => Entry($"tab.switch.{i}", "Tabs", t.Title.TrimStart('•', ' '), t.IconKey, Do(() => ActiveTab = t),
                detail: ReferenceEquals(t, ActiveTab) ? "current" : t.Connection.Name)
                with { Environment = t.ConnectionEnvironment }).ToList(),
            none, "activate open");
        yield return Entry("tab.close", "Tabs", "Close tab", "IconClose", () => CloseActiveTabAsync(), none, "Ctrl+W");
        yield return Entry("tab.reopen", "Tabs", "Reopen closed tab", "IconUndo", () => ReopenClosedTabAsync(),
            ClosedTabCount > 0 ? null : "No tab was closed", "Ctrl+Shift+T", "restore undo");
        yield return Entry("tab.next", "Tabs", "Next tab", "IconChevronRight", Do(NextTab), several, "Ctrl+Tab");
        yield return Entry("tab.previous", "Tabs", "Previous tab", "IconChevronLeft", Do(PreviousTab), several, "Ctrl+Shift+Tab");
        yield return Entry("tab.close-others", "Tabs", "Close other tabs", "IconClose", () => CloseOtherTabsCommand.ExecuteAsync(tab),
            none ?? several);
        yield return Entry("tab.close-right", "Tabs", "Close tabs to the right", "IconClose",
            () => CloseTabsToTheRightCommand.ExecuteAsync(tab),
            none ?? (Tabs.IndexOf(tab!) < Tabs.Count - 1 ? null : "There is no tab to the right"));
        yield return Entry("tab.close-all", "Tabs", "Close all tabs", "IconClose", () => CloseAllTabsCommand.ExecuteAsync(null), none);

        var noGroup = none ?? (tab!.HasGroupSwitch ? null : "This connection is not in a group with other databases");
        yield return Pick("tab.switch-database", "Tabs", "Switch database of this tab…", "IconGroup",
            () => tab!.GroupMembers.Select(m => Entry($"tab.switch-database.{m.Id}", "Tabs", m.Name, "IconDatabase",
                () => tab.SwitchConnectionAsync(m),
                m.Id == tab.Connection.Id ? "This tab is already on it" : null) with { Environment = m.Environment }).ToList(),
            noGroup, "group member connection");
        yield return Entry("tab.next-database", "Tabs", "Next database of the group", "IconChevronRight", () => NextGroupMemberAsync(),
            noGroup, "Ctrl+Alt+PageDown");
        yield return Entry("tab.previous-database", "Tabs", "Previous database of the group", "IconChevronLeft",
            () => PreviousGroupMemberAsync(), noGroup, "Ctrl+Alt+PageUp");
    }

    // ---- connections ----

    /// <summary>One choice per configured connection, the current one first, then by name.</summary>
    private IReadOnlyList<AppCommand> Connections(string id, string category, Func<ConnectionNodeViewModel, Func<Task>> run,
        Func<ConnectionNodeViewModel, string?>? disabled = null)
    {
        var current = CurrentConnection?.Id;
        return Tree.Config.Connections
            .OrderByDescending(c => c.Id == current).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => Tree.NodeOf(c.Id)).OfType<ConnectionNodeViewModel>()
            .Select(node =>
            {
                var detail = string.Join(" · ", new[] { node.IsConnected ? "connected" : null, node.Config.Folder }.Where(x => x is not null));
                return Entry($"{id}.{node.Config.Id}", category, node.Config.Name, "IconDatabase", run(node), disabled?.Invoke(node),
                    keywords: node.Config.Folder, detail: detail.Length > 0 ? detail : null)
                    with { Environment = node.Config.Environment };
            })
            .ToList();
    }

    private IEnumerable<AppCommand> ConnectionCommands()
    {
        const string category = "Connection";
        var none = HasConnections ? null : "Add a connection first";
        var anyConnected = Tree.Config.Connections.Any(x => Tree.NodeOf(x.Id) is { IsConnected: true });
        var noneConnected = none ?? (anyConnected ? null : "Nothing is connected");

        AppCommand OnConnection(string id, string title, string icon, Func<ConnectionNodeViewModel, Func<Task>> run,
            string? why = null, Func<ConnectionNodeViewModel, string?>? disabled = null, string? keywords = null) =>
            Pick(id, category, title, icon, () => Connections(id, category, run, disabled), why ?? none, keywords);

        static string? NotConnected(ConnectionNodeViewModel n) => n.IsConnected ? null : "Not connected";

        yield return OnConnection("connection.connect", "Connect to…", "IconDatabase",
            node => () =>
            {
                Tree.SelectedNode = node;
                return Tree.ConnectAsync(node);
            }, disabled: n => n.IsConnected ? "Already connected" : null, keywords: "open server");
        yield return OnConnection("connection.disconnect", "Disconnect from…", "IconClose",
            node => Do(() => Tree.Disconnect(node.Config.Id)), noneConnected, NotConnected, "close");
        yield return Entry("connection.new", category, "New connection…", "IconDatabaseAdd",
            () => Tree.AddConnectionCommand.ExecuteAsync(null), keywords: "add server database postgres sqlite");
        yield return OnConnection("connection.go-table", "Go to table of…", "IconTable",
            node => Do(() => Tree.PickTableOn(node)), keywords: "quick open");
        yield return OnConnection("connection.reveal", "Select in navigator…", "IconLocate",
            node => Do(() =>
            {
                IsNavigatorVisible = true;
                Tree.SelectedNode = node;
            }), keywords: "show find connection");
        yield return OnConnection("connection.refresh", "Refresh schema of…", "IconRefresh",
            node => () => Tree.RefreshCommand.ExecuteAsync(node), noneConnected, NotConnected, "reload tables");
        yield return Entry("connection.refresh-selected", category, "Refresh the schema", "IconRefresh",
            () => Tree.RefreshSelectedCommand.ExecuteAsync(null),
            Tree.SelectedConnection is { } selected && Tree.NodeOf(selected.Id) is { IsConnected: true } ? null
                : "Select a connected connection in the navigator", keywords: "reload tables");
        yield return OnConnection("connection.edit", "Edit connection…", "IconSettings",
            node => () => Tree.EditConnectionCommand.ExecuteAsync(node), keywords: "properties password");
        yield return OnConnection("connection.duplicate", "Duplicate connection…", "IconCopy",
            node => Do(() => Tree.DuplicateConnection(node)), keywords: "clone");
        yield return OnConnection("connection.read-only", "Toggle read-only on…", "IconLock",
            node => Do(() => Tree.ToggleReadOnly(node)), keywords: "protect writes");
        // Two steps: the connection, then the folder it goes to.
        yield return Pick("connection.move", category, "Move connection to folder…", "IconFolder",
            () => Connections("connection.move", category, _ => () => Task.CompletedTask)
                .Select(choice => choice with { Run = null, Choices = () => FolderTargets(choice.Id) }).ToList(),
            none ?? (Tree.Config.Folders.Count == 0 ? "Create a folder first" : null));
        yield return OnConnection("connection.add-to-group", "Add connection to group…", "IconGroupAdd",
            node => () => Tree.AddToGroupCommand.ExecuteAsync(node));
        yield return OnConnection("connection.delete", "Delete connection…", "IconDelete",
            node => () => Tree.DeleteConnectionCommand.ExecuteAsync(node), keywords: "remove");
        yield return Entry("connection.import-dbeaver", category, "Import from DBeaver…", "IconImport",
            () => Tree.ImportFromDBeaverAsync());
        yield return Entry("connection.collapse", category, "Collapse all in the navigator", "IconCollapseAll",
            Do(Tree.CollapseAllCommand), keywords: "fold tree");
    }

    private IReadOnlyList<AppCommand> FolderTargets(string choiceId)
    {
        var id = Guid.Parse(choiceId[(choiceId.LastIndexOf('.') + 1)..]);
        if (Tree.Config.Connections.FirstOrDefault(x => x.Id == id) is not { } connection) return [];
        return new string?[] { null }.Concat(Tree.Config.Folders)
            .Select(folder => Entry($"{choiceId}.{folder}", "Connection", folder ?? "No folder (top level)", "IconFolder",
                Do(() => Tree.MoveToFolder(connection, folder)),
                string.Equals(connection.Folder, folder, StringComparison.OrdinalIgnoreCase) ? "It is already there" : null))
            .ToList();
    }

    // ---- folders ----

    private IEnumerable<AppCommand> FolderCommands()
    {
        const string category = "Folder";
        var why = !Tree.ShowFolders ? "Show folders in the navigator first"
            : Tree.Nodes.OfType<FolderNodeViewModel>().Any() ? null : "There is no folder yet";

        AppCommand OnFolder(string id, string title, string icon, Func<FolderNodeViewModel, Func<Task>> run,
            Func<FolderNodeViewModel, string?>? disabled = null) =>
            Pick(id, category, title, icon, () => Tree.Nodes.OfType<FolderNodeViewModel>()
                .Select(f => Entry($"{id}.{f.Name}", category, f.Name, "IconFolder", run(f), disabled?.Invoke(f))).ToList(), why);

        yield return Entry("folder.new", category, "New folder…", "IconFolderAdd", () => Tree.AddFolderCommand.ExecuteAsync(null),
            keywords: "add directory");
        yield return OnFolder("folder.rename", "Rename folder…", "IconFolder", f => () => Tree.RenameFolderCommand.ExecuteAsync(f));
        yield return OnFolder("folder.environment", "Set environment of folder connections…", "IconDatabase",
            f => () => Tree.SetFolderEnvironmentCommand.ExecuteAsync(f));
        yield return OnFolder("folder.move-up", "Move folder up…", "IconSortUp", f => Do(Tree.MoveFolderUpCommand, f),
            f => f.CanMoveUp ? null : f.MoveUpTip);
        yield return OnFolder("folder.move-down", "Move folder down…", "IconSortDown", f => Do(Tree.MoveFolderDownCommand, f),
            f => f.CanMoveDown ? null : f.MoveDownTip);
        yield return OnFolder("folder.delete", "Delete folder…", "IconDelete", f => () => Tree.DeleteFolderCommand.ExecuteAsync(f));
    }

    // ---- groups ----

    private IEnumerable<AppCommand> GroupCommands()
    {
        const string category = "Group";
        var why = Tree.Config.Groups.Count == 0 ? "There is no group yet" : null;

        AppCommand OnGroup(string id, string title, string icon, Func<ConnectionGroup, Func<Task>> run) =>
            Pick(id, category, title, icon, () => Tree.Config.Groups
                .Select(g => Entry($"{id}.{g.Id}", category, g.Name, "IconGroup", run(g),
                    detail: $"{GroupRules.MembersOf(Tree.Config, g).Count} databases")).ToList(), why);

        yield return Entry("group.new", category, "New group of databases…", "IconGroupAdd",
            () => Tree.NewGroupCommand.ExecuteAsync(null), keywords: "add linked");
        yield return OnGroup("group.connect", "Connect all databases of group…", "IconGroup", g => async () =>
        {
            foreach (var member in GroupRules.MembersOf(Tree.Config, g)) await Tree.EnsureConnectedAsync(member);
        });
        yield return OnGroup("group.schema-diff", "Check schema differences of group…", "IconCompare",
            g => () => Tree.CheckSchemaDifferencesAsync(g));
        yield return OnGroup("group.edit", "Edit group…", "IconSettings", g => () => Tree.EditGroupCommand.ExecuteAsync(g));
        yield return OnGroup("group.rename", "Rename group…", "IconGroup", g => () => Tree.RenameGroupCommand.ExecuteAsync(g));
        yield return OnGroup("group.delete", "Delete group…", "IconDelete", g => () => Tree.DeleteGroupCommand.ExecuteAsync(g));
    }

    // ---- view ----

    private IEnumerable<AppCommand> ViewCommands()
    {
        yield return Entry("view.navigator", "View", "Show or hide the navigator", "IconSchema", Do(ToggleNavigator),
            shortcut: "Ctrl+B", keywords: "sidebar tree toggle", detail: OnOff(IsNavigatorVisible));
        yield return Entry("view.reveal", "View", "Show active item in navigator", "IconLocate", Do(RevealActiveTabCommand),
            ActiveTab is null ? "No tab is open" : null, keywords: "locate reveal");
        yield return Entry("view.filter-navigator", "View", "Filter the navigator", "IconFilter", Do(() =>
        {
            IsNavigatorVisible = true;
            FocusNavigatorFilter?.Invoke();
        }), FocusNavigatorFilter is null ? "Not available here" : HasConnections ? null : "Add a connection first", keywords: "search tree");
        yield return Entry("view.folders", "View", "Show folders in the navigator", "IconFolder", Do(Tree.ToggleFoldersCommand),
            keywords: "toggle flat", detail: OnOff(Tree.ShowFolders));
        yield return Entry("view.groups", "View", "Show groups in the navigator", "IconGroup", Do(Tree.ToggleGroupsCommand),
            keywords: "toggle", detail: OnOff(Tree.ShowGroups));
        yield return Entry("view.scripts", "View", "Show scripts in the navigator", "IconScript", Do(Tree.ToggleScriptsCommand),
            keywords: "toggle library", detail: OnOff(Tree.ShowScripts));
    }

    // ---- appearance ----

    private IEnumerable<AppCommand> AppearanceCommands()
    {
        const string category = "Appearance";
        IReadOnlyList<AppCommand> Options<T>(string id, string icon, IEnumerable<(T Value, string Label)> values,
            Func<AppearanceSettings, T> get, Func<AppearanceSettings, T, AppearanceSettings> set) =>
            values.Select(v => Entry($"{id}.{v.Value}", category, v.Label, icon,
                Do(() => SettingsService.Instance.Update(s => s with { Appearance = set(s.Appearance, v.Value) })),
                detail: Equals(get(SettingsService.Instance.Current.Appearance), v.Value) ? "current" : null)).ToList();

        yield return Pick("appearance.theme", category, "Change theme…", "IconPalette",
            () => Options("appearance.theme", "IconPalette",
                Enum.GetValues<ThemeChoice>().Select(t => (t, t.ToString())), a => a.Theme, (a, v) => a with { Theme = v }),
            keywords: "dark light system mode colour color");
        yield return Pick("appearance.accent", category, "Change accent colour…", "IconPalette",
            () => Options("appearance.accent", "IconPalette",
                AccentPalette.Presets.Select(p => (p, p.ToString())), a => a.Accent, (a, v) => a with { Accent = v }),
            keywords: "color highlight blue green purple");
        yield return Pick("appearance.density", category, "Change row density…", "IconTable",
            () => Options("appearance.density", "IconTable",
                Enum.GetValues<GridDensity>().Select(d => (d, d.ToString())), a => a.Density, (a, v) => a with { Density = v }),
            keywords: "compact comfortable spacious height");
        yield return Pick("appearance.scale", category, "Change UI scale…", "IconSearch",
            () => Options("appearance.scale", "IconSearch",
                new[] { 80, 90, 100, 110, 125, 150, 175, 200 }
                    .Where(p => p >= AppearanceSettings.MinScalePercent && p <= AppearanceSettings.MaxScalePercent)
                    .Select(p => (p, $"{p}%")),
                a => a.UiScalePercent, (a, v) => a with { UiScalePercent = v }),
            keywords: "zoom size dpi font");
    }

    // ---- settings, upgrade, help ----

    /// <summary>
    /// The pages "Open settings page…" offers: the ids of <see cref="SettingsPages"/> (a test keeps them in step), listed here
    /// so that opening the palette does not build the pages.
    /// </summary>
    internal static readonly IReadOnlyList<(string Id, string Title, string Icon)> SettingsPageChoices =
    [
        ("general", "General", "IconSettings"),
        ("appearance", "Appearance", "IconPalette"),
        ("editor", "Editor", "IconScript"),
        ("results", "Results", "IconTable"),
        ("history", "Query history", "IconHistory"),
        ("format", "SQL formatting", "IconFormat"),
        ("shortcuts", "Shortcuts", "IconKeyboard"),
        ("claude", "Claude Code", "IconConsole"),
        ("updates", "Updates", "IconRefresh"),
        ("upgrade", "Database upgrade", "IconImport"),
    ];

    private IEnumerable<AppCommand> ApplicationCommands()
    {
        const string category = "Application";
        var noDialogs = _upgradeDialogs is null ? "Not available here" : null;

        yield return Entry("app.settings", category, "Settings…", "IconSettings", () => OpenOptionsCommand.ExecuteAsync(null),
            noDialogs, "Ctrl+,", "options preferences");
        yield return Pick("app.settings-page", category, "Open settings page…", "IconSettings",
            () => SettingsPageChoices.Where(p => (p.Id != "history" || _history is not null) && (p.Id != "claude" || _claude is not null))
                .Select(p => Entry("app.settings-page." + p.Id, category, p.Title, p.Icon,
                    () => OpenOptionsCommand.ExecuteAsync(p.Id))).ToList(),
            noDialogs, "options preferences general appearance editor results history format shortcuts claude updates upgrade");
        // The folder is only checked when the command runs: it may be on a slow share.
        yield return Entry("app.upgrade", category, "Upgrade database…", "IconImport", () =>
            {
                RefreshUpgradeAvailability();
                return OpenUpgradeCommand.ExecuteAsync(null);
            },
            noDialogs ?? (string.IsNullOrWhiteSpace(Tree.Config.DbUpdatePath) ? "Set the dbupdate folder in Settings, Database upgrade"
                : CurrentConnection is null ? "Select a connection first" : null), keywords: "migration scripts dbupdate");
        yield return Entry("app.claude-code", category, "Open in Claude Code", "IconConsole",
            () => OpenInClaudeCodeCommand.ExecuteAsync(null), _claude is null ? "Not available here" : null,
            keywords: "ai assistant mcp terminal anthropic");
        yield return Entry("app.shortcuts", category, "Keyboard shortcuts", "IconKeyboard", Do(ShowShortcuts), shortcut: "F1",
            keywords: "help keys");
        yield return Entry("app.auto-update", category, "Check for updates automatically", "IconRefresh",
            Do(() => Update.AutoCheckEnabled = !Update.AutoCheckEnabled), keywords: "toggle version",
            detail: OnOff(Update.AutoCheckEnabled));
    }
}
