using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Config;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// Navigator tree: folder > connection > (schema >) table/view. Every mutation is saved to the config file
/// immediately. Connecting happens off the UI thread; failures end up in the node and the status bar, never as
/// exceptions.
/// </summary>
public partial class ConnectionTreeViewModel : ObservableObject
{
    private readonly ConfigStore _store;
    private readonly SessionManager _sessions;
    private readonly StatusModel _status;
    private readonly IDialogService _dialogs;
    private readonly string _dbeaverDir;
    private readonly bool _persistenceEnabled;
    private readonly Dictionary<Guid, ConnectionNodeViewModel> _connectionNodes = new();
    private readonly Dictionary<string, FolderNodeViewModel> _folderNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, CancellationTokenSource> _connecting = new();
    private readonly Dictionary<Guid, GroupNodeViewModel> _groupNodes = new();
    private GroupsSectionNodeViewModel? _groupsSection;
    private CancellationTokenSource? _schemaCheck;

    public ConnectionTreeViewModel(
        ConfigStore store, AppConfig config, SessionManager sessions, StatusModel status,
        IDialogService dialogs, string dbeaverDir, bool persistenceEnabled = true)
    {
        _persistenceEnabled = persistenceEnabled;
        _store = store;
        Config = config;
        _sessions = sessions;
        _status = status;
        _dialogs = dialogs;
        _dbeaverDir = dbeaverDir;
        Rebuild();
    }

    public AppConfig Config { get; private set; }

    public ObservableCollection<TreeNodeViewModel> Nodes { get; } = new();

    /// <summary>Called when a table node is activated; wired to <see cref="MainViewModel.OpenTable"/>.</summary>
    public Func<Session, TableRef, Task>? TableOpener { get; set; }

    /// <summary>Node currently selected in the navigator (two-way bound by the view); quick-open uses it as fallback.</summary>
    [ObservableProperty] private TreeNodeViewModel? _selectedNode;

    /// <summary>The open session of a connection, or null when it is not connected.</summary>
    public Session? FindSession(Guid connectionId) => _sessions.Find(connectionId);

    /// <summary>The connection that owns the selected node (connection, schema or table node), if any.</summary>
    public ConnectionConfig? SelectedConnection
    {
        get
        {
            if (SelectedNode is not { } selected) return null;
            if (selected is GroupMemberNodeViewModel member) return member.Target.Config;
            foreach (var node in _connectionNodes.Values)
                if (ReferenceEquals(node, selected) || Contains(node, selected)) return node.Config;
            return null;
        }
    }

    private static bool Contains(TreeNodeViewModel parent, TreeNodeViewModel target) =>
        parent.Children.Any(c => ReferenceEquals(c, target) || Contains(c, target));

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value) => OnPropertyChanged(nameof(SelectedConnection));

    /// <summary>The node of the connection that owns the selection (the selection itself, or its connection ancestor).</summary>
    private ConnectionNodeViewModel? SelectedConnectionNode =>
        SelectedConnection is { } config && _connectionNodes.TryGetValue(config.Id, out var node) ? node : null;

    /// <summary>The node of a connection (e.g. to select it after a group member was activated), or null.</summary>
    public ConnectionNodeViewModel? NodeOf(Guid connectionId) => _connectionNodes.GetValueOrDefault(connectionId);

    /// <summary>Navigator toolbar "Refresh": reloads the schema of the connection that owns the selected node.</summary>
    [RelayCommand]
    private async Task RefreshSelectedAsync()
    {
        if (SelectedConnectionNode is not { IsConnected: true } node)
        {
            _status.Info("Select a connected connection (or one of its tables) to refresh its schema.");
            return;
        }
        await RefreshAsync(node);
    }

    /// <summary>Navigator toolbar "Collapse all": folds every folder, connection and schema. Nothing is disconnected.</summary>
    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var node in Nodes) Collapse(node);
        static void Collapse(TreeNodeViewModel node)
        {
            node.IsExpanded = false;
            foreach (var child in node.Children) Collapse(child);
        }
    }

    /// <summary>
    /// Returns the session of <paramref name="config"/>, connecting first when needed (off the UI thread, with the
    /// spinner/cancel of <see cref="ConnectAsync"/>). Returns null, after telling the status bar why, when it cannot.
    /// </summary>
    public Task<Session?> EnsureConnectedAsync(ConnectionConfig config) => EnsureConnectedAsync(config, CancellationToken.None);

    /// <summary>As above; cancelling <paramref name="ct"/> abandons the connect attempt (null is returned).</summary>
    public async Task<Session?> EnsureConnectedAsync(ConnectionConfig config, CancellationToken ct)
    {
        if (!_connectionNodes.TryGetValue(config.Id, out var node))
        {
            _status.Error($"Connection '{config.Name}' no longer exists.");
            return null;
        }

        if (!node.IsConnected || _sessions.Find(config.Id) is null) await ConnectAsync(node, ct);
        var session = node.IsConnected ? _sessions.Find(config.Id) : null;
        if (session is null && node.State is ConnectionState.Connecting)
            _status.Info($"{config.Name} is still connecting, try again in a moment.");
        return session;
    }

    /// <summary>Warnings of the last DBeaver import (also summarised in the status bar).</summary>
    public IReadOnlyList<string> LastImportWarnings { get; private set; } = [];

    /// <summary>Connections of the last DBeaver import that were made read-only, with the reason.</summary>
    public IReadOnlyList<string> LastImportReadOnly { get; private set; } = [];

    // ---- tree building ----

    /// <summary>Rebuilds the node list from the config. Node objects are reused so state and expansion survive.</summary>
    public void Rebuild()
    {
        var folderNames = Config.Folders.Concat(Config.Connections.Select(c => c.Folder)
                .Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var liveIds = Config.Connections.Select(c => c.Id).ToHashSet();
        foreach (var gone in _connectionNodes.Keys.Where(id => !liveIds.Contains(id)).ToList())
            _connectionNodes.Remove(gone);

        foreach (var c in Config.Connections)
        {
            if (!_connectionNodes.TryGetValue(c.Id, out var node))
            {
                node = new ConnectionNodeViewModel(c) { Tree = this };
                _connectionNodes[c.Id] = node;
            }
            else
            {
                node.Config = c;
            }
        }

        Nodes.Clear();
        if (BuildGroupsSection() is { } groupsSection) Nodes.Add(groupsSection);
        var seenFolders = new Dictionary<string, FolderNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in folderNames)
        {
            if (!_folderNodes.TryGetValue(name, out var folder)) _folderNodes[name] = folder = new FolderNodeViewModel(name) { Tree = this };
            seenFolders[name] = folder;
            folder.Children = new ObservableCollection<TreeNodeViewModel>(
                Config.Connections
                    .Where(c => string.Equals(c.Folder, name, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(c => _connectionNodes[c.Id]));
            Nodes.Add(folder);
        }

        foreach (var gone in _folderNodes.Keys.Where(k => !seenFolders.ContainsKey(k)).ToList())
            _folderNodes.Remove(gone);

        foreach (var c in Config.Connections
                     .Where(c => string.IsNullOrWhiteSpace(c.Folder))
                     .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            Nodes.Add(_connectionNodes[c.Id]);
    }

    /// <summary>
    /// The "Groups" section (null when there are no groups). Section and group nodes are reused so their expansion survives;
    /// member nodes are cheap shortcuts to the real connection nodes and are recreated. Nothing here touches a database.
    /// </summary>
    private GroupsSectionNodeViewModel? BuildGroupsSection()
    {
        var liveGroups = Config.Groups.Select(g => g.Id).ToHashSet();
        foreach (var gone in _groupNodes.Keys.Where(id => !liveGroups.Contains(id)).ToList())
            _groupNodes.Remove(gone);
        if (Config.Groups.Count == 0) return null;

        var groupNodes = new List<TreeNodeViewModel>();
        foreach (var group in Config.Groups)
        {
            if (!_groupNodes.TryGetValue(group.Id, out var node))
                _groupNodes[group.Id] = node = new GroupNodeViewModel(group) { Tree = this };
            node.Group = group;

            var members = GroupRules.MembersOf(Config, group);
            node.Kind = members.Count > 0 ? members[0].Kind : null;
            node.Refresh();
            node.Children = new ObservableCollection<TreeNodeViewModel>(
                members.Select(m => (TreeNodeViewModel)new GroupMemberNodeViewModel(_connectionNodes[m.Id], group) { Tree = this }));
            groupNodes.Add(node);
        }

        _groupsSection ??= new GroupsSectionNodeViewModel { Tree = this, IsExpanded = true };
        _groupsSection.Children = new ObservableCollection<TreeNodeViewModel>(groupNodes);
        return _groupsSection;
    }

    /// <summary>Schemas with more tables than this are not expanded automatically.</summary>
    public const int AutoExpandLimit = 200;

    /// <summary>
    /// Fills a connected node: schema nodes for engines with schemas, tables directly for SQLite. Table nodes are
    /// created lazily, on first expansion of their parent; a parent with more than
    /// <see cref="AutoExpandLimit"/> tables is not expanded automatically.
    /// </summary>
    public static void ApplySchema(ConnectionNodeViewModel node, Session session)
    {
        var tables = session.Schema;
        if (session.Driver.Kind == DbKind.Sqlite)
        {
            node.SetLazyChildren(() => TableNodes(session, tables));
            node.IsExpanded = tables.Count <= AutoExpandLimit;
            return;
        }

        node.SetChildren(tables
            .GroupBy(t => t.Ref.Schema ?? "", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var infos = g.ToList();
                return (TreeNodeViewModel)new SchemaNodeViewModel(g.Key, infos.Count, () => TableNodes(session, infos));
            })
            .ToList());
        node.IsExpanded = true;
    }

    private static IReadOnlyList<TreeNodeViewModel> TableNodes(Session session, IEnumerable<TableInfo> tables) =>
        tables.Select(t => new TableNodeViewModel(session, t))
            .OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
            .ToList<TreeNodeViewModel>();
    // ---- folder operations ----

    public bool AddFolder(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return Fail("Folder name cannot be empty.");
        if (Config.Folders.Contains(name, StringComparer.OrdinalIgnoreCase)) return Fail($"Folder '{name}' already exists.");
        Config.Folders.Add(name);
        Persist();
        return true;
    }

    public bool RenameFolder(string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return Fail("Folder name cannot be empty.");
        if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)
            && Config.Folders.Contains(newName, StringComparer.OrdinalIgnoreCase))
            return Fail($"Folder '{newName}' already exists.");

        var index = Config.Folders.FindIndex(f => string.Equals(f, oldName, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) Config.Folders[index] = newName;
        else Config.Folders.Add(newName); // folder only known through its connections
        ReplaceConnections(c => string.Equals(c.Folder, oldName, StringComparison.OrdinalIgnoreCase) ? c with { Folder = newName } : c);
        Persist();
        return true;
    }

    /// <summary>Deletes the folder; its connections move to the root.</summary>
    public void DeleteFolder(string name)
    {
        Config.Folders.RemoveAll(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
        ReplaceConnections(c => string.Equals(c.Folder, name, StringComparison.OrdinalIgnoreCase) ? c with { Folder = null } : c);
        Persist();
    }

    /// <summary>Moves a connection into <paramref name="folder"/> (null = root). Also used by drag and drop.</summary>
    public void MoveToFolder(ConnectionConfig connection, string? folder)
    {
        if (folder is not null && !Config.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            Fail($"Folder '{folder}' does not exist.");
            return;
        }

        var current = Config.Connections.FirstOrDefault(c => c.Id == connection.Id);
        if (current is null) return;
        var canonical = folder is null ? null : Config.Folders.First(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        UpdateConnection(current with { Folder = canonical });
    }

    // ---- connection operations ----

    public void AddConnection(ConnectionConfig connection)
    {
        Config.Connections.Add(connection);
        if (!string.IsNullOrWhiteSpace(connection.Folder)
            && !Config.Folders.Contains(connection.Folder, StringComparer.OrdinalIgnoreCase))
            Config.Folders.Add(connection.Folder);
        Persist();
    }

    /// <summary>
    /// Replaces the stored connection with the same id. A live session survives cosmetic changes (name, folder,
    /// read-only flag) and is closed when anything that affects the connection itself changed.
    /// </summary>
    public void UpdateConnection(ConnectionConfig updated)
    {
        var index = Config.Connections.FindIndex(c => c.Id == updated.Id);
        if (index < 0) return;
        var old = Config.Connections[index];
        Config.Connections[index] = updated;

        // Name, folder and read-only flag do not affect the connection itself; anything else invalidates a live
        // session and a connect still in flight.
        if (old with { Name = updated.Name, Folder = updated.Folder, ReadOnly = updated.ReadOnly } == updated)
            _sessions.Find(updated.Id)?.UpdateConfig(updated);
        else
            Disconnect(updated.Id);

        Persist();
    }

    public void DeleteConnection(ConnectionConfig connection)
    {
        Config.Connections.RemoveAll(c => c.Id == connection.Id);
        Config.VirtualKeys.Remove(connection.Id);
        GroupRules.RemoveConnection(Config, connection.Id);   // a group only references ids: it must not dangle
        _connecting.GetValueOrDefault(connection.Id)?.Cancel();
        _sessions.Remove(connection.Id);
        Persist();
    }

    /// <summary>
    /// Stores the virtual key of a table (<c>"*"</c> = all columns) in the config and saves it. Returns false when it
    /// could not be saved (config file unreadable at startup, or a write error): the key then only lives in memory.
    /// </summary>
    public bool SetVirtualKey(Guid connectionId, TableRef table, string[] columns)
    {
        if (!Config.VirtualKeys.TryGetValue(connectionId, out var perTable))
            Config.VirtualKeys[connectionId] = perTable = new Dictionary<string, string[]>();
        perTable[table.ToString()] = columns;

        if (!_persistenceEnabled)
        {
            _status.Error("The config file could not be read at startup: this key is NOT saved.");
            return false;
        }
        try
        {
            _store.Save(Config);
            return true;
        }
        catch (Exception e)
        {
            _status.Error($"Cannot save config: {ErrorText.Describe(e)}");
            return false;
        }
    }

    [RelayCommand]
    public void ToggleReadOnly(ConnectionNodeViewModel? node)
    {
        if (node is not null) UpdateConnection(node.Config with { ReadOnly = !node.Config.ReadOnly });
    }

    // ---- group operations (a group only references connection ids; see GroupRules) ----

    /// <summary>Adds a new group after validating it. False (with a status message) when it is invalid.</summary>
    public bool AddGroup(ConnectionGroup group)
    {
        if (GroupRules.Validate(Config, group) is { } problem) return Fail(problem);
        Config.Groups.Add(group);
        Persist();
        return true;
    }

    /// <summary>Replaces the stored group with the same id after validating it. False when unknown or invalid.</summary>
    public bool ReplaceGroup(ConnectionGroup group)
    {
        var index = Config.Groups.FindIndex(g => g.Id == group.Id);
        if (index < 0) return false;
        if (GroupRules.Validate(Config, group) is { } problem) return Fail(problem);
        Config.Groups[index] = group;
        Persist();
        return true;
    }

    public bool RenameGroup(ConnectionGroup group, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return Fail("The group name cannot be empty.");
        return CurrentGroup(group) is { } current && ReplaceGroup(current with { Name = newName });
    }

    public void DeleteGroup(ConnectionGroup group)
    {
        Config.Groups.RemoveAll(g => g.Id == group.Id);
        Persist();
    }

    /// <summary>Appends the connection to the group (it must share the group's engine). False when refused.</summary>
    public bool AddToGroup(ConnectionConfig connection, ConnectionGroup group)
    {
        if (CurrentGroup(group) is not { } current) return false;
        if (current.ConnectionIds.Contains(connection.Id))
        {
            _status.Info($"'{connection.Name}' is already in group '{current.Name}'.");
            return false;
        }
        return ReplaceGroup(current with { ConnectionIds = [.. current.ConnectionIds, connection.Id] });
    }

    /// <summary>Takes the connection out of the group. The group is kept, even when it is left with fewer than two members.</summary>
    public void RemoveFromGroup(ConnectionConfig connection, ConnectionGroup group)
    {
        var index = Config.Groups.FindIndex(g => g.Id == group.Id);
        if (index < 0) return;
        var current = Config.Groups[index];
        Config.Groups[index] = current with { ConnectionIds = current.ConnectionIds.Where(id => id != connection.Id).ToList() };
        Persist();
    }

    private ConnectionGroup? CurrentGroup(ConnectionGroup group) => Config.Groups.FirstOrDefault(g => g.Id == group.Id);

    // ---- connecting ----

    /// <summary>Connects (off the UI thread) and fills the node with the schema. Never throws.</summary>
    public async Task ConnectAsync(ConnectionNodeViewModel node, CancellationToken ct = default)
    {
        if (node.State is ConnectionState.Connecting) return;
        if (node.State is ConnectionState.Connected)
        {
            node.IsExpanded = true;
            return;
        }

        var config = node.Config;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _connecting[config.Id] = cts;
        node.State = ConnectionState.Connecting;
        node.ErrorMessage = null;
        _status.Info($"Connecting to {config.Name}...");
        using var busy = _status.Busy($"Connecting to {config.Name}...");
        try
        {
            var session = await _sessions.GetOrOpenAsync(config, cts.Token);
            // Settings edited while the connect was running (read-only flag, name): the session was built from the config
            // captured above, the node holds the latest.
            if (session.Config != node.Config && SessionManager.SameTarget(session.Config, node.Config))
                session.UpdateConfig(node.Config);
            ApplySchema(node, session);
            node.State = ConnectionState.Connected;
            _status.Info($"Connected to {config.Name}: {session.Schema.Count} tables and views.");
        }
        catch (OperationCanceledException)
        {
            node.State = ConnectionState.Disconnected;
            _status.Info($"Connection to {config.Name} cancelled.");
        }
        catch (Exception e)
        {
            node.State = ConnectionState.Failed;
            node.ErrorMessage = ErrorText.Describe(e);
            _status.Error($"Cannot connect to {config.Name}: {node.ErrorMessage}");
        }
        finally
        {
            _connecting.Remove(config.Id);
        }
    }

    public void Disconnect(Guid connectionId)
    {
        _connecting.GetValueOrDefault(connectionId)?.Cancel();
        _sessions.Close(connectionId);
        if (!_connectionNodes.TryGetValue(connectionId, out var node)) return;
        node.SetChildren([]);
        node.State = ConnectionState.Disconnected;
        node.ErrorMessage = null;
        node.IsExpanded = false;
    }

    // ---- DBeaver import ----

    /// <summary>
    /// Imports the DBeaver workspace into a copy of the config; it replaces the live config only on success,
    /// so a malformed file or a locked credentials file cannot leave a half-merged state.
    /// </summary>
    [RelayCommand]
    public async Task ImportFromDBeaverAsync()
    {
        using var busy = _status.Busy("Importing DBeaver connections...");
        try
        {
            // A copy of EVERYTHING in the config (a positional `new AppConfig(...)` would silently drop the groups).
            var working = Config with
            {
                Folders = new(Config.Folders),
                Connections = new(Config.Connections),
                VirtualKeys = new(Config.VirtualKeys),
                Groups = Config.Groups.Select(g => g with { ConnectionIds = [.. g.ConnectionIds] }).ToList(),
            };
            var result = await Task.Run(() => DBeaverImporter.Import(_dbeaverDir, working, _store.Protector));
            LastImportWarnings = result.Warnings;
            LastImportReadOnly = result.ReadOnlyImported ?? [];
            if (result.Added > 0 || working.Folders.Count != Config.Folders.Count)
            {
                Config = working;
                Persist();
            }

            var summary = $"Imported {result.Added} connection(s), skipped {result.Skipped} already present.";
            if (LastImportReadOnly.Count > 0)
                summary += $" {LastImportReadOnly.Count} imported as READ-ONLY (production or read-only in DBeaver): " +
                           $"{string.Join("; ", LastImportReadOnly)}. Untick Read-only in the connection's menu to edit them.";
            if (result.Warnings.Count > 0)
                summary += $" {result.Warnings.Count} warning(s): {string.Join("; ", result.Warnings)}";
            _status.Info(summary);
        }
        catch (Exception e)
        {
            _status.Error($"DBeaver import failed: {ErrorText.Describe(e)}");
        }
    }

    // ---- commands (dialogs, then the operations above) ----

    [RelayCommand]
    private async Task AddConnectionAsync()
    {
        var vm = NewDialog(null);
        if (!await _dialogs.ShowConnectionDialogAsync(vm) || vm.Result is null) return;
        AddConnection(vm.Result);
    }

    [RelayCommand]
    private async Task EditConnectionAsync(ConnectionNodeViewModel? node)
    {
        if (node is null) return;
        var vm = NewDialog(node.Config);
        if (!await _dialogs.ShowConnectionDialogAsync(vm) || vm.Result is null) return;
        UpdateConnection(vm.Result);
    }

    [RelayCommand]
    private async Task DeleteConnectionAsync(ConnectionNodeViewModel? node)
    {
        if (node is null) return;
        if (!await _dialogs.ConfirmAsync("Delete connection", $"Delete connection '{node.Config.Name}'?")) return;
        DeleteConnection(node.Config);
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var name = await _dialogs.PromptTextAsync("New folder", "Folder name", "");
        if (name is not null) AddFolder(name);
    }

    [RelayCommand]
    private async Task RenameFolderAsync(FolderNodeViewModel? node)
    {
        if (node is null) return;
        var name = await _dialogs.PromptTextAsync("Rename folder", "Folder name", node.Name);
        if (name is not null) RenameFolder(node.Name, name);
    }

    [RelayCommand]
    private async Task DeleteFolderAsync(FolderNodeViewModel? node)
    {
        if (node is null) return;
        if (!await _dialogs.ConfirmAsync("Delete folder", $"Delete folder '{node.Name}'? Its connections move to the root.")) return;
        DeleteFolder(node.Name);
    }



    // ---- group commands ----

    [RelayCommand]
    private Task NewGroupAsync() => EditNewGroupAsync(null);

    private async Task EditNewGroupAsync(ConnectionConfig? firstMember)
    {
        var vm = new GroupEditorViewModel(Config, null);
        if (firstMember is not null) vm.Add(firstMember);
        if (!await _dialogs.ShowGroupEditorAsync(vm) || vm.Result is null) return;
        AddGroup(vm.Result);
    }

    [RelayCommand]
    private async Task EditGroupAsync(ConnectionGroup? group)
    {
        if (group is null || CurrentGroup(group) is not { } current) return;
        var vm = new GroupEditorViewModel(Config, current);
        if (!await _dialogs.ShowGroupEditorAsync(vm) || vm.Result is null) return;
        ReplaceGroup(vm.Result);
    }

    [RelayCommand]
    private async Task RenameGroupAsync(ConnectionGroup? group)
    {
        if (group is null || CurrentGroup(group) is not { } current) return;
        var name = await _dialogs.PromptTextAsync("Rename group", "Group name", current.Name);
        if (name is not null) RenameGroup(current, name);
    }

    [RelayCommand]
    private async Task DeleteGroupAsync(ConnectionGroup? group)
    {
        if (group is null || CurrentGroup(group) is not { } current) return;
        if (!await _dialogs.ConfirmAsync("Delete group", $"Delete group '{current.Name}'? Its connections are kept.")) return;
        DeleteGroup(current);
    }

    private const string NewGroupChoice = "New group…";

    /// <summary>
    /// "Add to group…" of a connection: pick one of the groups it could join (same engine, not already in it) or start a new
    /// group seeded with it. With no such group the editor opens directly.
    /// </summary>
    [RelayCommand]
    private async Task AddToGroupAsync(ConnectionNodeViewModel? node)
    {
        if (node is null) return;
        var connection = node.Config;
        var candidates = Config.Groups
            .Where(g => !g.ConnectionIds.Contains(connection.Id)
                        && GroupRules.MembersOf(Config, g).All(m => m.Kind == connection.Kind))
            .ToList();
        if (candidates.Count == 0)
        {
            await EditNewGroupAsync(connection);
            return;
        }

        var choice = await _dialogs.ChooseAsync("Add to group", $"Add '{connection.Name}' to:",
            [.. candidates.Select(g => g.Name), NewGroupChoice]);
        if (choice is not { } index) return;
        if (index >= 0 && index < candidates.Count) AddToGroup(connection, candidates[index]);
        else await EditNewGroupAsync(connection);
    }

    [RelayCommand]
    private void RemoveMember(GroupMemberNodeViewModel? member)
    {
        if (member is not null) RemoveFromGroup(member.Target.Config, member.Group);
    }

    /// <summary>Activating a group member connects the real connection (failures end up in its node and the status bar).</summary>
    [RelayCommand]
    public async Task OpenGroupMemberAsync(GroupMemberNodeViewModel? member)
    {
        if (member is not null) await ConnectAsync(member.Target);
    }

    private sealed record MemberLoad(ConnectionConfig Config, Session? Session, IReadOnlyList<TableInfo>? Schema, string? Error);

    /// <summary>Members loaded at the same time by <see cref="CheckSchemaDifferencesAsync"/> (as for a group run).</summary>
    private const int SchemaCheckParallelism = 4;

    /// <summary>
    /// "Check schema differences": connects to every member (in parallel, off the UI thread, with the status-bar spinner and
    /// its Cancel), reads their schemas and shows the report. Read-only and informational: an unreachable member is listed in
    /// the report, never fatal. The connections stay open and are shown as connected in the tree.
    /// </summary>
    [RelayCommand]
    public async Task CheckSchemaDifferencesAsync(ConnectionGroup? group, CancellationToken ct = default)
    {
        if (group is null || CurrentGroup(group) is not { } current) return;
        var members = GroupRules.MembersOf(Config, current);
        if (members.Count < 2)
        {
            _status.Info($"Group '{current.Name}' needs at least 2 databases to compare their schemas.");
            return;
        }
        if (_schemaCheck is not null)
        {
            _status.Info("A schema check is already running.");
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _schemaCheck = cts;
        SchemaDiffViewModel report;
        try
        {
            using (_status.Busy($"Checking schema differences of {current.Name}..."))
            {
                _status.Info($"Loading the schemas of the {members.Count} databases of '{current.Name}'...");
                using var gate = new SemaphoreSlim(SchemaCheckParallelism);
                var loads = await Task.WhenAll(members.Select(m => LoadMemberSchemaAsync(m, gate, cts.Token)));
                cts.Token.ThrowIfCancellationRequested();

                foreach (var load in loads.Where(l => l.Session is not null)) AdoptSession(load.Session!);
                var loaded = loads.Where(l => l.Schema is not null).ToList();
                var diffs = SchemaComparer.Compare(loaded.Select(l => new SchemaMember(l.Config.Name, l.Schema!)).ToList());
                var unreachable = loads.Where(l => l.Error is not null).Select(l => new UnreachableMember(l.Config.Name, l.Error!)).ToList();
                report = new SchemaDiffViewModel(current.Name, diffs, unreachable);
            }
        }
        catch (OperationCanceledException)
        {
            _status.Info("Schema check cancelled.");
            return;
        }
        catch (Exception e)
        {
            _status.Error($"Schema check failed: {ErrorText.Describe(e)}");
            return;
        }
        finally
        {
            _schemaCheck = null;
        }

        _status.Info(report.Unreachable.Count > 0
            ? $"{report.Summary} {report.Unreachable.Count} database(s) could not be reached."
            : report.Summary);
        await _dialogs.ShowSchemaDiffAsync(report);
    }

    /// <summary>
    /// Opens the member's session when needed (a new session carries a fresh schema). A session that was already open gets a
    /// throw-away connection to read the schema as it is now, without touching the session or the tree built from it.
    /// </summary>
    private async Task<MemberLoad> LoadMemberSchemaAsync(ConnectionConfig member, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var wasOpen = _sessions.Find(member.Id) is not null;
            var session = await _sessions.GetOrOpenAsync(member, ct);
            var schema = wasOpen
                ? await Task.Run(async () =>
                {
                    await using var connection = await session.OpenConnectionAsync(ct);
                    return await session.Driver.LoadSchemaAsync(connection, ct);
                }, ct)
                : session.Schema;
            return new MemberLoad(member, session, schema, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new MemberLoad(member, null, null, ErrorText.Describe(e));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Shows a session opened behind the tree's back (the schema check) as a connected node.</summary>
    private void AdoptSession(Session session)
    {
        if (!_connectionNodes.TryGetValue(session.Config.Id, out var node) || node.State is ConnectionState.Connected or ConnectionState.Connecting) return;
        if (_sessions.Find(session.Config.Id) != session) return;   // closed meanwhile
        ApplySchema(node, session);
        node.ErrorMessage = null;
        node.State = ConnectionState.Connected;
    }

    [RelayCommand]
    private Task Connect(ConnectionNodeViewModel? node) => node is null ? Task.CompletedTask : ConnectAsync(node);

    [RelayCommand]
    private void DisconnectNode(ConnectionNodeViewModel? node)
    {
        if (node is not null) Disconnect(node.Config.Id);
    }

    [RelayCommand]
    private async Task RefreshAsync(ConnectionNodeViewModel? node)
    {
        if (node is null || !node.IsConnected) return;
        using var busy = _status.Busy($"Refreshing {node.Config.Name}...");
        try
        {
            await _sessions.RefreshSchemaAsync(node.Config.Id, CancellationToken.None);
            var session = _sessions.Find(node.Config.Id);
            if (session is not null) ApplySchema(node, session);
            _status.Info($"Refreshed {node.Config.Name}.");
        }
        catch (Exception e)
        {
            _status.Error($"Cannot refresh {node.Config.Name}: {ErrorText.Describe(e)}");
        }
    }

    [RelayCommand]
    private void CancelConnect()
    {
        foreach (var cts in _connecting.Values.ToList()) cts.Cancel();
        _schemaCheck?.Cancel();
    }

    /// <summary>Opens the table through <see cref="TableOpener"/>; any failure is reported to the status bar.</summary>
    [RelayCommand]
    public async Task OpenTableAsync(TableNodeViewModel? node)
    {
        if (node is null || TableOpener is null) return;
        try
        {
            await TableOpener(node.Session, node.Table.Ref);
        }
        catch (Exception e)
        {
            _status.Error($"Cannot open {node.Table.Ref}: {ErrorText.Describe(e)}");
        }
    }

    // ---- helpers ----

    private ConnectionDialogViewModel NewDialog(ConnectionConfig? existing) =>
        new(_store.Protector, Config.Folders.ToList(),
            Config.Connections.Where(c => c.Id != existing?.Id).Select(c => c.Name).ToList(), existing);

    private void ReplaceConnections(Func<ConnectionConfig, ConnectionConfig> map)
    {
        for (var i = 0; i < Config.Connections.Count; i++)
            Config.Connections[i] = map(Config.Connections[i]);
    }

    private bool Fail(string message)
    {
        _status.Error(message);
        return false;
    }

    /// <summary>Saves the config; a no-op in read-only-config mode (the window shows a banner explaining why).</summary>
    private void Persist()
    {
        if (!_persistenceEnabled)
        {
            _status.Error("The config file could not be read at startup: this change is NOT saved.");
        }
        else
        {
            try
            {
                _store.Save(Config);
            }
            catch (Exception e)
            {
                _status.Error($"Cannot save config: {ErrorText.Describe(e)}");
            }
        }

        Rebuild();
    }
}







