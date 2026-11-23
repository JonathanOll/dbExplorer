using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Metadata;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly MetadataCache? _cache;

    /// <param name="cache">Metadata cache searched by Ctrl+Shift+P; without it that search reports "not available".</param>
    public MainViewModel(ConnectionTreeViewModel tree, StatusModel status, MetadataCache? cache = null)
    {
        Tree = tree;
        Status = status;
        _cache = cache;
        Tree.TableOpener = OpenTable;
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmptyState));
        // The tree rebuilds (Reset first) whenever groups or connections change: open tabs repaint their database selector.
        Tree.Nodes.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                foreach (var tab in Tabs) tab.NotifyGroupsChanged();
        };
        Tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionTreeViewModel.SelectedConnection)) OnPropertyChanged(nameof(CurrentConnection));
        };
    }

    /// <summary>True while no tab is open: the editor area shows the welcome panel instead.</summary>
    public bool IsEmptyState => Tabs.Count == 0;

    /// <summary>The connection the status bar names: the one of the active tab, else the one selected in the navigator.</summary>
    public ConnectionConfig? CurrentConnection => ActiveTab?.Connection ?? Tree.SelectedConnection;

    public ConnectionTreeViewModel Tree { get; }

    public StatusModel Status { get; }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    [ObservableProperty] private TabViewModel? _activeTab;

    /// <summary>Persistent warning shown above the content (e.g. the config could not be read and is not saved).</summary>
    [ObservableProperty] private string? _banner;

    /// <summary>
    /// Creates the tab for a table. Later features replace this with the real table view; until then the
    /// shell shows a placeholder so the tab plumbing can be exercised.
    /// </summary>
    public Func<Session, TableRef, TabViewModel> TableTabFactory { get; set; } =
        (session, table) => new PlaceholderTabViewModel(session.Config, table);

    /// <summary>
    /// Makes tables open as real <see cref="TableTabViewModel"/> tabs (loading their first page). Kept out of the
    /// constructor so shell tests can use placeholder tabs without touching a database.
    /// </summary>
    public void UseTableTabs(SessionManager sessions, IEditDialogs? editDialogs = null)
    {
        TableTabFactory = (session, table) => new TableTabViewModel(session, table, new TableTabHooks(
            ct => ResolveScriptSessionAsync(session.Config.Id, ct),   // through the tree: Reload after Disconnect reconnects its node too
            (s, t, filters) => OpenTable(s, t, filters),
            editDialogs,
            () => Tree.Config,
            Tree.SetVirtualKey,
            SwitchHooks));
    }

    private GroupSwitchHooks? _switchHooks;

    /// <summary>What a tab needs to switch between group members: the live config and sessions resolved through the tree.</summary>
    private GroupSwitchHooks SwitchHooks => _switchHooks ??= new GroupSwitchHooks(() => Tree.Config, ResolveScriptSessionAsync);

    /// <summary>Opens a new tab for <paramref name="t"/> on the session and makes it active.</summary>
    public Task OpenTable(Session s, TableRef t) => OpenTable(s, t, null);

    /// <summary>
    /// Opens a new tab for <paramref name="t"/> with initial column filters (<c>column -&gt; filter text</c>, e.g. a
    /// followed foreign key). Table tabs start loading right away; failures show in the tab, never here.
    /// </summary>
    public Task OpenTable(Session s, TableRef t, IReadOnlyDictionary<string, string>? filters) => OpenTable(s, t, filters, null);

    private Task OpenTable(Session s, TableRef t, IReadOnlyDictionary<string, string>? filters, string? rawWhere)
    {
        var tab = TableTabFactory(s, t);
        if (tab is TableTabViewModel table)
        {
            if (filters is not null)
                foreach (var (column, text) in filters) table.ColumnFilters[column] = text;
            if (rawWhere is not null) table.RawWhere = rawWhere;
        }
        Tabs.Add(tab);
        ActiveTab = tab;
        if (tab is TableTabViewModel loading) _ = loading.LoadAsync();
        return Task.CompletedTask;
    }

    // ---- script tabs (Ctrl+N, Ctrl+O) ----

    private IScriptFiles? _scriptFiles;
    private ScriptSessionStore? _scriptStore;
    private IEditDialogs? _scriptDialogs;
    private int _scriptCounter;

    /// <summary>
    /// Gives script tabs their file pickers, the store that keeps unsaved text across restarts and the Save / Discard /
    /// Cancel prompt shown when a script with unsaved text is closed.
    /// </summary>
    public void UseScriptTabs(IScriptFiles files, ScriptSessionStore? store, IEditDialogs? dialogs = null)
    {
        _scriptFiles = files;
        _scriptStore = store;
        _scriptDialogs = dialogs;
    }

    partial void OnActiveTabChanged(TabViewModel? oldValue, TabViewModel? newValue)
    {
        // Leaving a script tab is a good moment to persist it (the debounce may not have fired yet).
        (oldValue as ScriptTabViewModel)?.FlushPersist();
        // The status bar names the connection of the active tab, which changes when the tab is switched to another group member.
        if (oldValue is not null) oldValue.PropertyChanged -= OnActiveTabPropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnActiveTabPropertyChanged;
        OnPropertyChanged(nameof(CurrentConnection));
    }

    private void OnActiveTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.Connection)) OnPropertyChanged(nameof(CurrentConnection));
    }

    /// <summary>
    /// Ctrl+N: opens an empty script tab on the connection of the active tab, else the one selected in the tree.
    /// Returns null, after a status message, when there is no such connection. Nothing is connected until it runs.
    /// </summary>
    public ScriptTabViewModel? NewScript()
    {
        if (ScriptTarget() is not { } connection)
        {
            Status.Info("Select a connection in the tree or open a tab first, then press Ctrl+N.");
            return null;
        }

        var tab = CreateScriptTab(connection, null);
        Tabs.Add(tab);
        ActiveTab = tab;
        return tab;
    }

    [RelayCommand]
    private void NewScriptTab() => NewScript();

    /// <summary>Ctrl+O: picks a script file and opens it in a new script tab (or in the empty script tab in front).</summary>
    [RelayCommand]
    public async Task OpenScriptFileAsync()
    {
        try
        {
            if (_scriptFiles is null)
            {
                Status.Info("Opening script files is not available.");
                return;
            }
            if (ScriptTarget() is not { } connection)
            {
                Status.Info("Select a connection in the tree or open a tab first, then press Ctrl+O.");
                return;
            }

            var path = await _scriptFiles.PickOpenAsync();
            if (path is null) return;

            var tab = ActiveTab is ScriptTabViewModel { FilePath: null } empty && string.IsNullOrWhiteSpace(empty.Text)
                ? empty
                : null;
            if (tab is null)
            {
                tab = CreateScriptTab(connection, null);
                Tabs.Add(tab);
            }
            ActiveTab = tab;
            await tab.OpenAsync(path);
        }
        catch (Exception e)
        {
            Status.Error($"Cannot open the script: {ErrorText.Describe(e)}");
        }
    }

    /// <summary>
    /// Brings back the unsaved scripts of the last run whose connection still exists (the others stay in the store).
    /// They are not connected: a restored tab connects when it first runs. Returns how many were restored.
    /// </summary>
    public int RestoreScripts()
    {
        if (_scriptStore is not { } store) return 0;
        var restored = 0;
        try
        {
            foreach (var saved in store.LoadAll())
            {
                var config = Tree.Config.Connections.FirstOrDefault(c => c.Id == saved.ConnectionId);
                if (config is null) continue;
                var tab = CreateScriptTab(config, saved);
                Tabs.Add(tab);
                restored++;
            }
        }
        catch (Exception e)
        {
            Status.Error($"Cannot restore the unsaved scripts: {ErrorText.Describe(e)}");
        }

        if (restored > 0)
        {
            ActiveTab ??= Tabs.OfType<ScriptTabViewModel>().FirstOrDefault();
            Status.Info($"Restored {restored} unsaved script(s).");
        }
        return restored;
    }

    /// <summary>Writes the unsaved text of every script tab now (the app is closing).</summary>
    public void PersistScripts()
    {
        foreach (var tab in Tabs.OfType<ScriptTabViewModel>()) tab.FlushPersist();
    }

    /// <summary>The connection a new script goes to: the active tab's, else the tree selection's. Always its latest saved settings.</summary>
    private ConnectionConfig? ScriptTarget()
    {
        var target = ActiveTab?.Connection ?? Tree.SelectedConnection;
        if (target is null) return null;
        return Tree.Config.Connections.FirstOrDefault(c => c.Id == target.Id) ?? target;
    }

    private ScriptTabViewModel CreateScriptTab(ConnectionConfig connection, SavedScript? restored)
    {
        var hooks = new ScriptTabHooks(
            ct => ResolveScriptSessionAsync(connection.Id, ct), _scriptFiles, _scriptStore, Status, _scriptDialogs, SwitchHooks);
        return new ScriptTabViewModel(connection, Tree.FindSession(connection.Id), hooks, ++_scriptCounter, restored);
    }

    /// <summary>
    /// The live session of a connection with its latest saved settings, connecting it first through the tree (spinner, schema
    /// and Connected state of its node follow). Null when the connection was deleted; throws when it cannot connect.
    /// Used by script tabs and by Reload of table tabs.
    /// </summary>
    private async Task<Session?> ResolveScriptSessionAsync(Guid connectionId, CancellationToken ct)
    {
        var config = Tree.Config.Connections.FirstOrDefault(c => c.Id == connectionId);
        if (config is null) return null;
        return await Tree.EnsureConnectedAsync(config, ct)
            ?? throw new InvalidOperationException($"{config.Name} is not available (see the status bar).");
    }

    // ---- quick open (Ctrl+T, Ctrl+Shift+P, Ctrl+L) ----

    // The text last typed in the Ctrl+T box, offered again (selected) the next time it opens. Memory only: not saved to disk.
    private string _lastTableQuery = "";

    /// <summary>The overlay currently shown, or null. A fresh view model per opening: nothing is remembered.</summary>
    [ObservableProperty] private QuickOpenViewModel? _quickOpen;

    /// <summary>Ctrl+T: tables and views of the connection of the active tab (or, with no tab, the one selected in the tree).</summary>
    [RelayCommand]
    public void ShowTablePicker()
    {
        try
        {
            ConnectionConfig? target = ActiveTab?.Connection ?? Tree.SelectedConnection;
            if (target is null)
            {
                Status.Info("Select a connection in the tree or open a table first, then press Ctrl+T.");
                return;
            }

            var session = Tree.FindSession(target.Id);
            if (session is null)
            {
                Status.Info($"{target.Name} is not connected. Connect to it first (double-click it in the tree).");
                return;
            }

            var picker = QuickOpenViewModel.ForTables(session, OpenTable, Status, _lastTableQuery);
            picker.CloseRequested += () => _lastTableQuery = picker.LastQuery;   // kept on Esc too: the next Ctrl+T starts from it
            Show(picker);
        }
        catch (Exception e)
        {
            Status.Error($"Cannot open the table picker: {ErrorText.Describe(e)}");
        }
    }

    /// <summary>Ctrl+Shift+P: tables of every connection that has a metadata cache. Never connects by itself.</summary>
    [RelayCommand]
    public async Task ShowAllTablesPickerAsync()
    {
        try
        {
            if (_cache is not { } cache)
            {
                Status.Info("The metadata cache is not available.");
                return;
            }

            var configs = Tree.Config.Connections.ToList(); // snapshot on the UI thread; the pool thread must not read the live list
            var vm = await Task.Run(() =>
                QuickOpenViewModel.ForAllConnections(cache, configs, Tree.EnsureConnectedAsync, OpenTable, Status));
            Show(vm);
        }
        catch (Exception e)
        {
            Status.Error($"Cannot open the table picker: {ErrorText.Describe(e)}");
        }
    }

    /// <summary>Ctrl+L: column jump over <paramref name="names"/>; the chosen index is passed to <paramref name="jumpTo"/>.</summary>
    public void ShowColumnPicker(IReadOnlyList<string> names, Action<int> jumpTo) =>
        Show(QuickOpenViewModel.ForColumns(names, jumpTo, Status));

    /// <summary>
    /// Ctrl+L: the column picker of the active table tab; choosing a column scrolls the grid to it (see
    /// <see cref="TableTabViewModel.RequestFocusColumn"/>). With any other tab, or none, only a status message.
    /// </summary>
    [RelayCommand]
    public void JumpToColumn()
    {
        try
        {
            if (ActiveTab is not TableTabViewModel tab)
            {
                Status.Info("Open a table first, then press Ctrl+L to jump to one of its columns.");
                return;
            }
            if (tab.Columns.Count == 0)
            {
                Status.Info($"{tab.Table.Name} has no columns to jump to.");
                return;
            }
            ShowColumnPicker(tab.Columns.Select(c => c.Name).ToList(), tab.RequestFocusColumn);
        }
        catch (Exception e)
        {
            Status.Error($"Cannot open the column picker: {ErrorText.Describe(e)}");
        }
    }

    private void Show(QuickOpenViewModel vm)
    {
        vm.CloseRequested += () =>
        {
            if (ReferenceEquals(QuickOpen, vm)) QuickOpen = null;
        };
        QuickOpen = vm;
    }

    private bool _confirmingExit;

    /// <summary>
    /// Asked when the window is about to close: runs <see cref="TabViewModel.ConfirmExitAsync"/> of every tab in turn,
    /// so each tab with unsaved work prompts (Save / Discard / Cancel) and any Cancel aborts the close (false). Tabs with
    /// nothing to ask answer synchronously; dirty scripts do not ask while their autosave works (it brings them back at
    /// the next start). Generic on purpose: any tab type that overrides ConfirmCloseAsync takes part.
    /// Never throws: a failing prompt keeps the window open and reports in the status bar. A second request while the
    /// prompts are still showing is refused.
    /// </summary>
    public async Task<bool> ConfirmExitAsync()
    {
        if (_confirmingExit || _closingTab) return false;
        _confirmingExit = true;
        var agreed = new List<TabViewModel>();
        var allAgreed = false;
        try
        {
            foreach (var tab in Tabs.ToList())
            {
                if (!await AskToExitAsync(tab)) return false;
                agreed.Add(tab);
            }
            allAgreed = true;
            return true;
        }
        catch (Exception e)
        {
            Status.Error($"Cannot close: {ErrorText.Describe(e)}");
            return false;
        }
        finally
        {
            _confirmingExit = false;
            if (!allAgreed)   // the window stays open: tabs that said yes (e.g. Discard) are ordinary open tabs again
                foreach (var tab in agreed) tab.CloseAborted();
        }
    }

    /// <summary>
    /// <see cref="TabViewModel.ConfirmCloseAsync"/> of the tab, after showing the tab when it is going to ask something:
    /// the prompt is always about the tab on screen, also when it is closed from the window X or from an inactive tab.
    /// </summary>
    private Task<bool> AskToCloseAsync(TabViewModel tab)
    {
        if (tab.NeedsCloseConfirmation && !ReferenceEquals(ActiveTab, tab)) ActiveTab = tab;
        return tab.ConfirmCloseAsync();
    }

    /// <summary>
    /// <see cref="TabViewModel.ConfirmExitAsync"/> of the tab (the window is closing), shown first when it will ask.
    /// Scripts do not ask while their autosave works, so they are not brought to the front.
    /// </summary>
    private Task<bool> AskToExitAsync(TabViewModel tab)
    {
        if (tab.NeedsExitConfirmation && !ReferenceEquals(ActiveTab, tab)) ActiveTab = tab;
        return tab.ConfirmExitAsync();
    }

    private bool _closingTab;

    /// <summary>
    /// Closes the tab after its confirmation (Save / Discard / Cancel when it has unsaved work). Never throws; a close
    /// that is already being asked about, or an exit prompt in progress, makes a second request a no-op.
    /// </summary>
    [RelayCommand]
    private async Task CloseTabAsync(TabViewModel? tab)
    {
        if (tab is null || _closingTab || _confirmingExit) return;
        _closingTab = true;
        try
        {
            if (!await AskToCloseAsync(tab)) return;   // e.g. unsaved edits: the user chose Cancel
            var index = Tabs.IndexOf(tab);
            if (index < 0) return;
            RememberClosed(tab);
            (tab as ScriptTabViewModel)?.Close();
            Tabs.RemoveAt(index);
            if (ReferenceEquals(ActiveTab, tab) || ActiveTab is null)
                ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }
        catch (Exception e)
        {
            tab.CloseAborted();
            Status.Error($"Cannot close the tab: {ErrorText.Describe(e)}");
        }
        finally
        {
            _closingTab = false;
        }
    }

    // ---- reopen closed tabs (Ctrl+Shift+T) ----

    private abstract record ClosedTab(Guid ConnectionId);

    private sealed record ClosedTable(Guid ConnectionId, TableRef Table, IReadOnlyDictionary<string, string> Filters, string? RawWhere)
        : ClosedTab(ConnectionId);

    private sealed record ClosedScript(Guid ConnectionId, string Text, string? FilePath) : ClosedTab(ConnectionId);

    /// <summary>How many closed tabs are remembered; the oldest are forgotten first.</summary>
    public const int MaxClosedTabs = 50;

    // Oldest first in the list; Ctrl+Shift+T takes from the end: the tab closed last comes back first, like a browser.
    private readonly List<ClosedTab> _closedTabs = [];
    private bool _reopening;

    /// <summary>How many closed tabs can be reopened.</summary>
    public int ClosedTabCount => _closedTabs.Count;

    /// <summary>Keeps what is needed to reopen a tab that was just closed: a table with its filters, or a script with its text and file.</summary>
    private void RememberClosed(TabViewModel tab)
    {
        ClosedTab? entry = tab switch
        {
            TableTabViewModel t => new ClosedTable(t.Connection.Id, t.Table,
                new Dictionary<string, string>(t.ColumnFilters, StringComparer.OrdinalIgnoreCase),
                string.IsNullOrWhiteSpace(t.RawWhere) ? null : t.RawWhere),
            ScriptTabViewModel s when !string.IsNullOrWhiteSpace(s.Text) || s.FilePath is not null =>
                new ClosedScript(s.Connection.Id, s.Text, s.FilePath),
            _ => null,   // an empty script or a placeholder: nothing worth bringing back
        };
        if (entry is null) return;
        _closedTabs.Add(entry);
        if (_closedTabs.Count > MaxClosedTabs) _closedTabs.RemoveRange(0, _closedTabs.Count - MaxClosedTabs);
    }

    /// <summary>
    /// Ctrl+Shift+T: reopens the tab closed most recently (like a browser; pressing again goes further back), connecting its
    /// connection when needed.
    /// Entries whose connection was deleted are dropped; an entry whose connection cannot be reached stays for the next try.
    /// Never throws.
    /// </summary>
    [RelayCommand]
    public async Task ReopenClosedTabAsync()
    {
        if (_reopening) return;
        _reopening = true;
        try
        {
            while (_closedTabs.Count > 0)
            {
                var entry = _closedTabs[^1];
                var config = Tree.Config.Connections.FirstOrDefault(c => c.Id == entry.ConnectionId);
                if (config is null)
                {
                    _closedTabs.RemoveAt(_closedTabs.Count - 1);   // its connection was deleted
                    continue;
                }

                switch (entry)
                {
                    case ClosedTable closed:
                        if (await Tree.EnsureConnectedAsync(config) is not { } session) return;   // reported by the tree; kept for later
                        _closedTabs.Remove(entry);
                        await OpenTable(session, closed.Table, closed.Filters, closed.RawWhere);
                        break;
                    case ClosedScript closed:
                        _closedTabs.Remove(entry);
                        var tab = CreateScriptTab(config, new SavedScript(Guid.NewGuid(), config.Id, closed.Text, closed.FilePath));
                        Tabs.Add(tab);
                        ActiveTab = tab;
                        break;
                }
                return;
            }
            Status.Info("No closed tab to reopen.");
        }
        catch (Exception e)
        {
            Status.Error($"Cannot reopen the tab: {ErrorText.Describe(e)}");
        }
        finally
        {
            _reopening = false;
        }
    }

    // ---- tab shortcuts (Ctrl+W, Ctrl+Tab, Ctrl+Shift+Tab, F5) ----

    /// <summary>Ctrl+W: closes the active tab through the same path as its x button, so unsaved work is asked about.</summary>
    [RelayCommand]
    public Task CloseActiveTabAsync() => ActiveTab is { } tab ? CloseTabAsync(tab) : Task.CompletedTask;

    // ---- group member shortcuts (Ctrl+Alt+PageDown, Ctrl+Alt+PageUp) ----

    /// <summary>Ctrl+Alt+PageDown: the active tab moves to the next database of its group, wrapping around.</summary>
    [RelayCommand]
    public Task NextGroupMemberAsync() => SwitchActiveTabAsync(+1);

    /// <summary>Ctrl+Alt+PageUp: the active tab moves to the previous database of its group, wrapping around.</summary>
    [RelayCommand]
    public Task PreviousGroupMemberAsync() => SwitchActiveTabAsync(-1);

    private async Task SwitchActiveTabAsync(int delta)
    {
        if (ActiveTab is not { } tab) return;
        if (!tab.HasGroupSwitch)
        {
            Status.Info($"{tab.Connection.Name} is not in a group with other databases: nothing to switch to.");
            return;
        }
        try
        {
            await tab.SwitchRelativeAsync(delta);
        }
        catch (Exception e)
        {
            Status.Error($"Cannot switch database: {ErrorText.Describe(e)}");
        }
    }

    /// <summary>Ctrl+Tab: the next tab, wrapping around.</summary>
    [RelayCommand]
    public void NextTab() => MoveTab(+1);

    /// <summary>Ctrl+Shift+Tab: the previous tab, wrapping around.</summary>
    [RelayCommand]
    public void PreviousTab() => MoveTab(-1);

    private void MoveTab(int step)
    {
        if (Tabs.Count == 0) return;
        var index = ActiveTab is null ? -1 : Tabs.IndexOf(ActiveTab);
        ActiveTab = index < 0
            ? Tabs[step > 0 ? 0 : Tabs.Count - 1]
            : Tabs[(index + step + Tabs.Count) % Tabs.Count];
    }

    /// <summary>
    /// F5 in a table tab: reloads the page on screen (asking about pending edits first, as the refresh button does).
    /// Does nothing for other tabs: F5 in a script tab runs the script and is handled by the editor.
    /// </summary>
    [RelayCommand]
    public async Task RefreshActiveTableAsync()
    {
        if (ActiveTab is not TableTabViewModel tab) return;
        try
        {
            await tab.RefreshAsync();
        }
        catch (Exception e)
        {
            Status.Error($"Cannot refresh: {ErrorText.Describe(e)}");
        }
    }
}

/// <summary>Stand-in tab until the table view exists.</summary>
public sealed class PlaceholderTabViewModel(ConnectionConfig connection, TableRef table) : TabViewModel(connection)
{
    public TableRef Table { get; } = table;
    public override string Title => $"{Connection.Name} · {Table.Name}";
}

