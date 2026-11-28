using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Config;
using DbExplorer.Core.Metadata;
using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;
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
        Tree.DefinitionOpener = (session, name, sql) => OpenDefinition(session, name, sql);
        Tree.ScriptOpener = () => NewScript();
        Tree.TablePickerOpener = ShowTablePicker;
        Tree.DiagramOpener = (connection, request) => OpenDiagram(connection, request);
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmptyState));
        WatchForCompare();
        // The tree rebuilds (Reset first) whenever groups or connections change: open tabs repaint their database selector.
        Tree.Nodes.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                foreach (var tab in Tabs) tab.NotifyGroupsChanged();
        };
        Tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionTreeViewModel.SelectedConnection))
            {
                { OnPropertyChanged(nameof(CurrentConnection)); OnPropertyChanged(nameof(CurrentConnectionNode)); }
                OnPropertyChanged(nameof(HasTarget));
                OnPropertyChanged(nameof(WelcomeHint));
                OnPropertyChanged(nameof(WindowTitle));
                // with no tab open, the selection is what the status bar names: a message about another connection goes
                if (ActiveTab is null) Status.ClearOtherConnections(CurrentConnection?.Id);
            }
        };
        Tree.Nodes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasConnections));
    }

    /// <summary>True when at least one connection is configured.</summary>
    public bool HasConnections => Tree.Config.Connections.Count > 0;

    /// <summary>True when "Open a table" / "New SQL script" have a connection to work on (the active tab's, else the selected one).</summary>
    public bool HasTarget => CurrentConnection is not null;

    /// <summary>The welcome page line that says what the two connection-bound actions will use, or why they wait.</summary>
    public string WelcomeHint => !HasConnections ? "Add a connection first: the table and script actions work on it."
        : CurrentConnection is { } c ? $"Selected connection: {c.Name}" : "Select a connection in the navigator first.";

    /// <summary>Window title: the active tab and its connection, then the app name.</summary>
    public string WindowTitle => ActiveTab is { } tab
        ? $"{(tab.HasUnsavedWork ? "● " : "")}{tab.Title.TrimStart('•', ' ')} – dbExplorer"
        : "dbExplorer";

    /// <summary>False while the navigator is hidden (Ctrl+B).</summary>
    [ObservableProperty] private bool _isNavigatorVisible = true;

    [RelayCommand]
    public void ToggleNavigator() => IsNavigatorVisible = !IsNavigatorVisible;

    /// <summary>The "config problem" banner can be dismissed for this run (the problem itself stays: changes are still not saved).</summary>
    [RelayCommand]
    public void DismissBanner() => Banner = null;

    /// <summary>True while the keyboard shortcuts card is showing (F1).</summary>
    [ObservableProperty] private bool _isShortcutsVisible;

    [RelayCommand]
    public void ShowShortcuts() => IsShortcutsVisible = true;

    [RelayCommand]
    public void HideShortcuts() => IsShortcutsVisible = false;

    /// <summary>True while no tab is open: the editor area shows the welcome panel instead.</summary>
    public bool IsEmptyState => Tabs.Count == 0;

    /// <summary>The connection the status bar names: the one of the active tab, else the one selected in the navigator.</summary>
    /// <summary>The active tab when it is a script (the Script menu acts on it); null otherwise.</summary>
    public ScriptTabViewModel? ActiveScript => ActiveTab as ScriptTabViewModel;

    public bool HasActiveScript => ActiveTab is ScriptTabViewModel;

    public bool HasActiveTable => ActiveTab is TableTabViewModel;

    private const string NoScriptTab = "Open a script tab first";

    /// <summary>Tooltip of the Script menu entries: why they are disabled, null while a script tab is active.</summary>
    public string? ScriptMenuTip => HasActiveScript ? null : NoScriptTab;

    /// <summary>Tooltip of File, Save script as: what it does, or why it is disabled.</summary>
    public string SaveAsMenuTip => HasActiveScript ? "Choose any file with the system file dialog" : NoScriptTab;

    /// <summary>Tooltip of View, Go to column: why it is disabled, null while a table tab is active.</summary>
    public string? TableMenuTip => HasActiveTable ? null : "Open a table first";

    /// <summary>Why "Include actual plan" is disabled, null when it can be used.</summary>
    public string? ActualPlanMenuTip => ActiveScript is not { } s ? NoScriptTab
        : s.CanUseActualPlan ? null : "SQLite only has an estimated plan";

    /// <summary>Why "Include statistics" is disabled, null when it can be used.</summary>
    public string? StatisticsMenuTip => ActiveScript is not { } s ? NoScriptTab
        : s.CanUseStatistics ? null : "Only SQL Server has IO and time statistics";

    private void NotifyMenuTips()
    {
        OnPropertyChanged(nameof(ScriptMenuTip));
        OnPropertyChanged(nameof(SaveAsMenuTip));
        OnPropertyChanged(nameof(TableMenuTip));
        OnPropertyChanged(nameof(ActualPlanMenuTip));
        OnPropertyChanged(nameof(StatisticsMenuTip));
    }

    public ConnectionConfig? CurrentConnection => ActiveTab?.Connection ?? Tree.SelectedConnection;

    /// <summary>The navigator node of <see cref="CurrentConnection"/> (its state and tooltip): what the status bar shows.</summary>
    public ConnectionNodeViewModel? CurrentConnectionNode => CurrentConnection is { } c ? Tree.NodeOf(c.Id) : null;

    public ConnectionTreeViewModel Tree { get; }

    public StatusModel Status { get; }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    [ObservableProperty] private TabViewModel? _activeTab;

    /// <summary>Persistent warning shown above the content (e.g. the config could not be read and is not saved).</summary>
    [ObservableProperty] private string? _banner;

    /// <summary>The "new version available" banner; inert (never visible) until <see cref="UseUpdates"/> gives it a checker.</summary>
    public UpdateViewModel Update { get; private set; } = new();

    /// <summary>Installs the update view model the window binds to.</summary>
    public void UseUpdates(UpdateViewModel update)
    {
        Update = update;
        OnPropertyChanged(nameof(Update));
    }

    /// <summary>
    /// A close that <see cref="ConfirmExitAsync"/> agreed to will not happen after all (the update could not start): tabs that
    /// said yes (a script the user chose to discard) go back to normal. Safe on every tab.
    /// </summary>
    public void ExitAborted()
    {
        foreach (var tab in Tabs) tab.CloseAborted();
    }

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
        TableTabHooks Hooks(Guid connectionId) => new(
            ct => ResolveScriptSessionAsync(connectionId, ct),   // through the tree: Reload after Disconnect reconnects its node too
            (s, t, filters) => OpenTable(s, t, filters),
            editDialogs,
            () => Tree.Config,
            Tree.SetVirtualKey,
            SwitchHooks,
            Guard,
            Status);

        TableTabFactory = (session, table) => new TableTabViewModel(session, table, Hooks(session.Config.Id));
        RestoredTableTabFactory = (config, table) => new TableTabViewModel(config, table, Hooks(config.Id));
    }

    /// <summary>Creates the tab of a table restored from the last run: no session, loads when first selected. Set by <see cref="UseTableTabs"/>.</summary>
    public Func<ConnectionConfig, TableRef, TableTabViewModel>? RestoredTableTabFactory { get; set; }

    private IProductionDialogs? _productionDialogs;
    private ProductionGuard? _productionGuard;

    /// <summary>Installs the confirmation of writes on production; without it they are refused.</summary>
    public void UseProductionDialogs(IProductionDialogs dialogs) => _productionDialogs = dialogs;

    /// <summary>The gate shared by every tab and the upgrade; it reads the latest saved settings of a connection.</summary>
    private ProductionGuard Guard => _productionGuard ??= new ProductionGuard(
        _productionDialogs, id => Tree.Config.Connections.FirstOrDefault(c => c.Id == id));

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
        { OnPropertyChanged(nameof(CurrentConnection)); OnPropertyChanged(nameof(CurrentConnectionNode)); }
        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(WelcomeHint));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(ActiveScript));
        OnPropertyChanged(nameof(HasActiveScript));
        OnPropertyChanged(nameof(HasActiveTable));
        NotifyMenuTips();
        Status.ClearOtherConnections(CurrentConnection?.Id);
        // A restored table tab connects and loads when it is first selected (never before).
        if (!_restoringTabs && newValue is TableTabViewModel { IsDeferred: true } deferred) _ = deferred.EnsureLoadedAsync();
        ScheduleSessionSave();
    }

    private void OnActiveTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.Connection))
        {
            OnPropertyChanged(nameof(CurrentConnection));
            OnPropertyChanged(nameof(CurrentConnectionNode));
            NotifyMenuTips();
        }
        if (e.PropertyName is nameof(TabViewModel.Title) or nameof(TabViewModel.HasUnsavedWork)) OnPropertyChanged(nameof(WindowTitle));
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

    // ---- tabs survive closing the app (session.json) ----

    private TabSessionStore? _sessionStore;
    private SavedTabs? _savedSession;
    private bool _restoringTabs;
    private bool _sessionRestored;   // writing starts once the restore ran: an early close must not wipe the saved session
    private readonly object _sessionGate = new();
    private int _sessionVersion;
    private CancellationTokenSource? _sessionTimer;

    /// <summary>How long after the last change the tab session is written (about a second).</summary>
    public TimeSpan SessionSaveDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gives the shell the file that remembers the open tabs and the session read from it at startup (null: no usable file).
    /// Nothing is restored or written until <see cref="RestoreTabs"/> runs. Skip this call to disable the feature.
    /// </summary>
    public void UseTabSession(TabSessionStore store, SavedTabs? saved)
    {
        _sessionStore = store;
        _savedSession = saved;
        Tabs.CollectionChanged += OnTabsChanged;
        foreach (var tab in Tabs) tab.PropertyChanged += OnTabChanged;
    }

    private void OnTabsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (TabViewModel tab in e.OldItems) tab.PropertyChanged -= OnTabChanged;
        if (e.NewItems is not null) foreach (TabViewModel tab in e.NewItems) tab.PropertyChanged += OnTabChanged;
        ScheduleSessionSave();
    }

    // What the session holds: the connection (group switch), a table's filters, where and sort (and Rows: a query was applied),
    // a script's file. A script's text is not here (its autosave has it).
    private void OnTabChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.Connection) or nameof(TableTabViewModel.Sort) or nameof(TableTabViewModel.RawWhere)
            or nameof(TableTabViewModel.ColumnFilters) or nameof(TableTabViewModel.Rows) or nameof(ScriptTabViewModel.FilePath)
            or nameof(TabViewModel.HasUnsavedWork))
            ScheduleSessionSave();
    }

    /// <summary>
    /// Reopens the tabs of the last run: from the session file when there is one, else (first run after an upgrade, or an
    /// unusable file) only the unsaved scripts, as before. Only builds tab shells: table tabs stay unloaded (no connection, no
    /// query) until first selected, and the active one starts loading when it becomes active. Tabs whose connection no
    /// longer exists are skipped. Returns how many tabs came back. Never throws.
    /// </summary>
    public int RestoreTabs()
    {
        if (_sessionStore is null) return RestoreScripts();
        try
        {
            if (_savedSession is not { } saved)
            {
                var legacy = RestoreScripts();
                _sessionRestored = true;
                ScheduleSessionSave();   // the first session file
                return legacy;
            }
            return RestoreFrom(saved);
        }
        catch (Exception e)
        {
            // Writes stay disabled for this run (_sessionRestored is not set): the tabs now open are only a part of the saved
            // list, and the next change would replace the intact file with it.
            Status.Error($"Cannot restore the tabs: {ErrorText.Describe(e)}. Your session file is left as it was.");
            return Tabs.Count;
        }
    }

    private int RestoreFrom(SavedTabs saved)
    {
        IReadOnlyList<SavedScript> unsaved = [];
        try { unsaved = _scriptStore?.LoadAll() ?? []; }
        catch (Exception e) { Status.Error($"Cannot read the unsaved scripts: {ErrorText.Describe(e)}"); }
        var unsavedById = unsaved.GroupBy(s => s.TabId).ToDictionary(g => g.Key, g => g.First());

        TabViewModel? active = null;
        var forgotten = 0;   // connection deleted: the file forgets these tabs
        var adopted = 0;
        var kept = 0;
        var problems = new List<string>();   // things the user must hear about (these tabs stay in the file until the next change)
        _restoringTabs = true;   // the tab control selects the first tab it is given: that selection must not start a load
        try
        {
            for (var i = 0; i < saved.Tabs.Count; i++)
            {
                var entry = saved.Tabs[i];
                try
                {
                    var config = Tree.Config.Connections.FirstOrDefault(c => c.Id == entry.ConnectionId);
                    if (config is null)
                    {
                        forgotten++;
                        continue;
                    }
                    var tab = CreateRestored(config, entry, unsavedById, problems);
                    if (tab is null) continue;
                    Tabs.Add(tab);
                    if (i == saved.ActiveIndex) active = tab;
                }
                catch (Exception e)
                {
                    problems.Add($"A tab could not be restored: {ErrorText.Describe(e)}");
                }
            }

            // Autosave copies the session does not reference: the app was killed (or the session could not be written) after a
            // script was last saved. Those written after the session file are brought back, after its tabs and never active;
            // older ones stay on disk untouched. (A closed or discarded script has no copy left.)
            if (_scriptStore is { } store)
            {
                var referenced = saved.Tabs.Where(t => t.ScriptId is not null).Select(t => t.ScriptId!.Value).ToHashSet();
                var sessionWritten = File.GetLastWriteTimeUtc(_sessionStore!.FilePath);
                foreach (var copy in unsavedById.Values.Where(s => !referenced.Contains(s.TabId)))
                {
                    try
                    {
                        var config = Tree.Config.Connections.FirstOrDefault(c => c.Id == copy.ConnectionId);
                        if (config is not null && store.LastWriteUtc(copy.TabId) > sessionWritten)
                        {
                            Tabs.Add(CreateScriptTab(config, copy));
                            adopted++;
                        }
                        else
                        {
                            kept++;
                        }
                    }
                    catch (Exception)
                    {
                        kept++;
                    }
                }
            }
            ActiveTab = active ?? Tabs.FirstOrDefault();
        }
        finally
        {
            _restoringTabs = false;
        }
        // Only the active tab loads now (the others when first selected).
        if (ActiveTab is TableTabViewModel { IsDeferred: true } current) _ = current.EnsureLoadedAsync();

        _sessionRestored = true;   // only reached when the restore as a whole worked: see RestoreTabs
        var notes = new List<string>();
        if (forgotten > 0) notes.Add($"{forgotten} tab(s) were not restored: their connection no longer exists.");
        if (adopted > 0) notes.Add($"Restored {adopted} unsaved script(s) that were not in the session.");
        if (kept > 0) notes.Add($"{kept} unsaved script(s) are kept in {_scriptStore!.Folder} (not restored).");
        notes.AddRange(problems);
        if (notes.Count > 0)
        {
            var text = string.Join(" ", notes);
            if (problems.Count > 0) Status.Error(text);
            else Status.Info(text);
        }
        if (forgotten > 0 || adopted > 0) ScheduleSessionSave();   // the file now says what is open
        return Tabs.Count;
    }

    /// <summary>The tab of a saved entry, or null (after adding the reason to <paramref name="problems"/>, unless it is silent). May throw.</summary>
    private TabViewModel? CreateRestored(ConnectionConfig config, SavedTab entry, Dictionary<Guid, SavedScript> unsaved, List<string> problems)
    {
        switch (entry.Kind)
        {
            case SavedTabKind.Table:
                if (string.IsNullOrEmpty(entry.Table))
                {
                    problems.Add("A saved table tab has no table name and was not restored.");
                    return null;
                }
                if (RestoredTableTabFactory is not { } create)
                {
                    problems.Add("Table tabs cannot be restored here.");
                    return null;
                }
                return CreateRestoredTable(create, config, entry);
            case SavedTabKind.Script:
                return CreateRestoredScript(config, entry, unsaved, problems);
            default:
                problems.Add($"A saved tab of kind {entry.Kind} was not restored.");
                return null;
        }
    }

    private static TableTabViewModel CreateRestoredTable(
        Func<ConnectionConfig, TableRef, TableTabViewModel> create, ConnectionConfig config, SavedTab entry)
    {
        var tab = create(config, new TableRef(entry.Schema, entry.Table!));
        if (entry.Filters is not null)
            foreach (var (column, text) in entry.Filters)
                if (!string.IsNullOrEmpty(text)) tab.ColumnFilters[column] = text;
        tab.RawWhere = entry.RawWhere;
        if (!string.IsNullOrEmpty(entry.SortColumn)) tab.Sort = new SortSpec(entry.SortColumn, entry.SortDescending);
        return tab;
    }

    private ScriptTabViewModel? CreateRestoredScript(
        ConnectionConfig config, SavedTab entry, Dictionary<Guid, SavedScript> unsaved, List<string> problems)
    {
        var id = entry.ScriptId ?? Guid.NewGuid();
        if (unsaved.TryGetValue(id, out var dirty))
            return CreateScriptTab(config, new SavedScript(id, config.Id, dirty.Text, entry.FilePath ?? dirty.FilePath));
        if (entry.ScriptId is not null && _scriptStore is { } store && store.Exists(id))
        {
            // A copy exists but could not be read: opening a clean tab with this id would delete it at the first autosave flush.
            var name = entry.FilePath is { } named ? Path.GetFileName(named) : id.ToString("N");
            problems.Add($"Script {name} could not be read; its saved copy is kept in {store.Folder}.");
            return null;
        }

        // Nothing unsaved: the file as it is now, or an empty new script when it is gone or cannot be read (never bound to it then).
        string? path = null;
        if (entry.FilePath is { } p && File.Exists(p))
        {
            try
            {
                using (new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                path = p;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add($"Could not read {p}: {e.Message} The script tab was opened empty and is not linked to the file.");
            }
        }
        return CreateScriptTab(config, new SavedScript(id, config.Id, null, path));
    }

    private SavedTabs SnapshotSession()
    {
        var tabs = new List<SavedTab>();
        var active = -1;
        foreach (var tab in Tabs.ToList())
        {
            SavedTab? entry = tab switch
            {
                TableTabViewModel t => new SavedTab(SavedTabKind.Table, t.Connection.Id, t.Table.Schema, t.Table.Name,
                    t.ColumnFilters.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value),
                    string.IsNullOrWhiteSpace(t.RawWhere) ? null : t.RawWhere,
                    t.Sort?.Column, t.Sort?.Descending ?? false),
                ScriptTabViewModel { IsDiscarded: false, IsUnmodifiedDefinition: false } s => new SavedTab(SavedTabKind.Script, s.Connection.Id, ScriptId: s.Id, FilePath: s.FilePath),
                _ => null,   // a placeholder, or a script the user discarded on exit
            };
            if (entry is null) continue;
            if (ReferenceEquals(tab, ActiveTab)) active = tabs.Count;
            tabs.Add(entry);
        }
        return new SavedTabs(active, tabs);
    }

    /// <summary>Writes the tab session after <see cref="SessionSaveDelay"/> (restarted by every change); the snapshot is taken now, the write runs on a pool thread.</summary>
    private void ScheduleSessionSave()
    {
        if (_sessionStore is not { } store || !_sessionRestored) return;
        var snapshot = SnapshotSession();
        int version;
        lock (_sessionGate) version = ++_sessionVersion;
        var timer = new CancellationTokenSource();
        Interlocked.Exchange(ref _sessionTimer, timer)?.Cancel();
        _ = Task.Delay(SessionSaveDelay, timer.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            lock (_sessionGate)
            {
                if (version == _sessionVersion) store.Save(snapshot);   // an older snapshot never overwrites a newer write
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Writes the tab session now (the window is closing). Idempotent; does nothing before the restore ran or without a store.</summary>
    public void PersistSession()
    {
        if (_sessionStore is not { } store || !_sessionRestored) return;
        Interlocked.Exchange(ref _sessionTimer, null)?.Cancel();
        var snapshot = SnapshotSession();
        lock (_sessionGate)
        {
            _sessionVersion++;   // a pending delayed write is now stale
            store.Save(snapshot);
        }
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
            ct => ResolveScriptSessionAsync(connection.Id, ct), _scriptFiles, _scriptStore, Status, _scriptDialogs, SwitchHooks, Tree.FindSession,
            _library, OnScriptFileUsed, _history, () => Tree.Config.RecordHistory, () => Tree.Config.Format,
            Guard, Assistant);
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
        // Another request (e.g. a second restored tab on the same connection) is connecting it already: wait for its outcome
        // instead of being told "still connecting".
        var waited = false;
        while (Tree.NodeOf(config.Id) is { State: ConnectionState.Connecting })
        {
            waited = true;
            await Task.Delay(40, ct);
        }
        if (waited && Tree.NodeOf(config.Id) is { State: ConnectionState.Failed } failed)
            throw new InvalidOperationException($"{config.Name} is not available: {failed.ErrorMessage}");
        return await Tree.EnsureConnectedAsync(config, ct)
            ?? throw new InvalidOperationException($"{config.Name} is not available (see the status bar).");
    }

    // ---- quick open (Ctrl+T, Ctrl+Shift+P, Ctrl+L) ----

    // The text last typed in the Ctrl+T box, offered again (selected) the next time it opens. Memory only: not saved to disk.
    private string _lastTableQuery = "";

    /// <summary>The overlay currently shown, or null. A fresh view model per opening: nothing is remembered.</summary>
    [ObservableProperty] private QuickOpenViewModel? _quickOpen;

    /// <summary>
    /// Ctrl+T: tables and views of the connection of the active tab (or, with no tab, the one selected in the tree); a connection
    /// that is not connected is connected first. With nothing to work on, a list of the connections to pick from.
    /// </summary>
    [RelayCommand]
    public void ShowTablePicker() => _ = ShowTablePickerAsync();

    /// <summary>The work of <see cref="ShowTablePicker"/>; completes synchronously when the connection is already open.</summary>
    public async Task ShowTablePickerAsync()
    {
        try
        {
            ConnectionConfig? target = ActiveTab?.Connection ?? Tree.SelectedConnection;
            if (target is null)
            {
                ShowConnectionPicker();
                return;
            }

            var session = Tree.FindSession(target.Id) ?? await Tree.EnsureConnectedAsync(target);
            if (session is null) return;   // the tree already said why
            ShowTablePicker(session);
        }
        catch (Exception e)
        {
            Status.Error($"Cannot open the table picker: {ErrorText.Describe(e)}");
        }
    }

    private void ShowTablePicker(Session session)
    {
        var picker = Filtered(TablesPicker(session, _lastTableQuery));
        picker.ScopeHint = "Search all connections";
        picker.OtherScope = () => BuildAllTablesPicker() is { } all ? Hinted(all, "Search this connection only") : null;
        picker.CloseRequested += () => _lastTableQuery = picker.LastQuery;   // kept on Esc too: the next Ctrl+T starts from it
        Show(picker);
    }

    private QuickOpenViewModel? BuildAllTablesPicker() =>
        _cache is { } cache ? QuickOpenViewModel.ForAllConnections(cache, Tree.Config.Connections.ToList(), Tree.EnsureConnectedAsync, OpenTable, Status, SearchObjects, Tree.Config.Groups.ToList()) : null;

    private static QuickOpenViewModel Hinted(QuickOpenViewModel vm, string hint)
    {
        vm.ScopeHint = hint;
        return vm;
    }

    /// <summary>The list of connections (Ctrl+T with nothing selected): choosing one connects it and goes on to its tables.</summary>
    private void ShowConnectionPicker()
    {
        if (Tree.Config.Connections.Count == 0)
        {
            Status.Info("No connection yet: add one with the + button of the navigator, then press Ctrl+T.");
            return;
        }
        var picker = QuickOpenViewModel.ForConnections(Tree.Config.Connections, c => Tree.FindSession(c.Id) is not null,
            Tree.EnsureConnectedAsync, session =>
            {
                ShowTablePicker(session);
                return Task.CompletedTask;
            }, Status, Tree.Config.Groups.ToList());
        Show(picker);
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
            var groups = Tree.Config.Groups.ToList();
            var vm = await Task.Run(() =>
                QuickOpenViewModel.ForAllConnections(cache, configs, Tree.EnsureConnectedAsync, OpenTable, Status, SearchObjects, groups));
            if ((ActiveTab?.Connection ?? Tree.SelectedConnection) is { } current && Tree.FindSession(current.Id) is { } session)
            {
                vm.ScopeHint = "Search this connection only";
                vm.OtherScope = () => Hinted(TablesPicker(session), "Search all connections");
            }
            Show(Filtered(vm));
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
            (tab as DiagramTabViewModel)?.Close();
            (tab as CompareTabViewModel)?.Close();
            (tab as TableTabViewModel)?.Close();
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

    // ---- tab context menu ----

    /// <summary>Closes every tab but <paramref name="keep"/>, one by one (a tab with unsaved work asks; Cancel stops the sequence).</summary>
    [RelayCommand]
    private async Task CloseOtherTabsAsync(TabViewModel? keep)
    {
        if (keep is null) return;
        foreach (var tab in Tabs.Where(t => !ReferenceEquals(t, keep)).ToList())
        {
            if (!Tabs.Contains(tab)) continue;
            await CloseTabAsync(tab);
            if (Tabs.Contains(tab)) return;   // the user chose Cancel
        }
    }

    /// <summary>Closes the tabs to the right of <paramref name="from"/>.</summary>
    [RelayCommand]
    private async Task CloseTabsToTheRightAsync(TabViewModel? from)
    {
        var index = from is null ? -1 : Tabs.IndexOf(from);
        if (index < 0) return;
        foreach (var tab in Tabs.Skip(index + 1).ToList())
        {
            await CloseTabAsync(tab);
            if (Tabs.Contains(tab)) return;
        }
    }

    /// <summary>Closes every tab.</summary>
    [RelayCommand]
    private async Task CloseAllTabsAsync()
    {
        foreach (var tab in Tabs.ToList())
        {
            await CloseTabAsync(tab);
            if (Tabs.Contains(tab)) return;
        }
    }

    // ---- reopen closed tabs (Ctrl+Shift+T) ----

    private abstract record ClosedTab(Guid ConnectionId);

    /// <summary>One line of the welcome page's recent tabs.</summary>
    public sealed record RecentTab(object Entry, string Title, string Icon);

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
        RefreshRecent();
    }

    /// <summary>How many closed tabs the welcome page lists.</summary>
    public const int MaxRecentShown = 5;

    /// <summary>The tabs closed last, newest first (welcome page "Recent"); choosing one reopens it.</summary>
    public ObservableCollection<RecentTab> RecentTabs { get; } = new();

    public bool HasRecentTabs => RecentTabs.Count > 0;

    private void RefreshRecent()
    {
        RecentTabs.Clear();
        foreach (var entry in Enumerable.Reverse(_closedTabs).Take(MaxRecentShown))
        {
            var name = Tree.Config.Connections.FirstOrDefault(c => c.Id == entry.ConnectionId)?.Name;
            if (name is null) continue;
            RecentTabs.Add(entry switch
            {
                ClosedTable t => new RecentTab(entry, $"{name} · {t.Table}", "IconTable"),
                ClosedScript s => new RecentTab(entry, $"{name} · {ScriptLabel(s)}", "IconScript"),
                _ => new RecentTab(entry, name, "IconTable"),
            });
        }
        OnPropertyChanged(nameof(HasRecentTabs));
    }

    private static string ScriptLabel(ClosedScript s)
    {
        if (s.FilePath is { } path) return Path.GetFileName(path);
        var line = s.Text.Split((char)10).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "script";
        return line.Length > 40 ? line[..40] + "…" : line;
    }

    /// <summary>Reopens one of <see cref="RecentTabs"/> (it becomes the most recently closed, then Ctrl+Shift+T logic runs).</summary>
    [RelayCommand]
    private async Task ReopenRecentAsync(RecentTab? recent)
    {
        if (recent?.Entry is not ClosedTab entry || !_closedTabs.Remove(entry)) return;
        _closedTabs.Add(entry);
        await ReopenClosedTabAsync();
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
            RefreshRecent();
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

