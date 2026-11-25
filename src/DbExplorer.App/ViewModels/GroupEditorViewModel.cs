using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// Edits one group: a name and an ordered list of connections of ONE engine. Choosing the first member restricts
/// <see cref="Candidates"/> to that engine. <see cref="Result"/> is set by <see cref="TryConfirm"/> only when the group is
/// valid (<see cref="GroupRules.Validate"/>); otherwise <see cref="Error"/> says why.
/// </summary>
public sealed partial class GroupEditorViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly ConnectionGroup? _existing;
    private bool _showEmptyName;

    public GroupEditorViewModel(AppConfig cfg, ConnectionGroup? existing)
    {
        _config = cfg;
        _existing = existing;
        if (existing is not null)
        {
            _name = existing.Name;
            foreach (var member in GroupRules.MembersOf(cfg, existing)) Selected.Add(member);
        }

        RefreshCandidates();
    }

    public bool IsEdit => _existing is not null;
    public string Title => IsEdit ? "Edit group" : "New group";

    /// <summary>The primary button: a verb rather than "OK".</summary>
    public string OkText => IsEdit ? "Save group" : "Create group";

    [ObservableProperty]
    private string _name = "";

    /// <summary>Connections that can still be added: the engine of the first member (every connection while the group is empty).</summary>
    public ObservableCollection<ConnectionConfig> Candidates { get; } = new();

    /// <summary>The members, in group order.</summary>
    public ObservableCollection<ConnectionConfig> Selected { get; } = new();

    /// <summary>Narrows <see cref="Candidates"/> to the connections whose name, folder, host or database contain this text.</summary>
    [ObservableProperty]
    private string _candidateFilter = "";

    /// <summary>
    /// Why the group cannot be saved (set by <see cref="TryConfirm"/>, cleared by the next change); null when fine. A problem with the
    /// name also shows under the name box (<see cref="NameError"/>); the banner is for the rest.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowErrorBand))]
    private string? _error;

    /// <summary>The name problem shown under the name box: a clash shows as it is typed, an empty name after the first OK.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNameError), nameof(ShowErrorBand))]
    private string? _nameError;

    public bool HasError => Error is not null;
    public bool HasNameError => NameError is not null;

    /// <summary>The error is about the members, not the name: the banner below the lists shows it.</summary>
    public bool ShowErrorBand => Error is not null && NameError is null;

    /// <summary>Non-blocking hint shown while the group has fewer than two members.</summary>
    public string? Warning => Selected.Count < 2
        ? "A group needs at least 2 databases to switch between them or compare them. You can still save it."
        : null;

    /// <summary>The hint under the member list while it is short of members; null once there are two or more.</summary>
    public string? MembersHint => Selected.Count switch
    {
        0 => "Add two or more connections. The first one sets the engine.",
        1 => "Add at least one more to switch between databases and compare them. You can still save.",
        _ => null,
    };

    public bool IsMembersEmpty => Selected.Count == 0;
    public bool NeedsMoreMembers => Selected.Count == 1;
    public string MembersTitle => $"MEMBERS ({Selected.Count})";

    /// <summary>The engine members must share (that of the first member); null while the group is empty.</summary>
    public DbKind? Kind => Selected.Count > 0 ? Selected[0].Kind : null;

    /// <summary>The engine as text for the member header chip ("PostgreSQL only"); null while the group is empty.</summary>
    public string? EngineOnlyText => Kind switch
    {
        DbKind.Postgres => "PostgreSQL only",
        DbKind.SqlServer => "SQL Server only",
        DbKind.Sqlite => "SQLite only",
        _ => null,
    };

    /// <summary>Connections of another engine that are not offered (and why), once the first member fixed the engine.</summary>
    public int HiddenCount { get; private set; }

    public string? HiddenText => HiddenCount == 0 ? null : $"{HiddenCount} {(HiddenCount == 1 ? "connection" : "connections")} of another engine not shown.";

    /// <summary>The valid group, set by <see cref="TryConfirm"/>.</summary>
    public ConnectionGroup? Result { get; private set; }

    partial void OnNameChanged(string value)
    {
        Error = null;
        NameError = NameProblem(_showEmptyName);
    }

    partial void OnCandidateFilterChanged(string value) => RefreshCandidates();

    private string? NameProblem(bool includeEmpty)
    {
        var name = Name.Trim();
        if (name.Length == 0) return includeEmpty ? "The group name cannot be empty." : null;
        var id = _existing?.Id;
        return _config.Groups.Any(o => o.Id != id && string.Equals(o.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase))
            ? $"A group named \"{name}\" already exists."
            : null;
    }

    /// <summary>Appends a connection to the members. Nothing happens when it is already one.</summary>
    [RelayCommand]
    public void Add(ConnectionConfig? connection)
    {
        if (connection is null || Selected.Any(c => c.Id == connection.Id)) return;
        Selected.Add(connection);
        SelectionChanged();
    }

    /// <summary>Appends several connections (the multi-selection of the candidate list), keeping their order.</summary>
    public void AddRange(IEnumerable<ConnectionConfig> connections)
    {
        var added = false;
        foreach (var c in connections)
        {
            if (Selected.Any(s => s.Id == c.Id) || (Kind is { } kind && c.Kind != kind)) continue;
            Selected.Add(c);
            added = true;
        }
        if (added) SelectionChanged();
    }

    [RelayCommand]
    public void Remove(ConnectionConfig? connection)
    {
        if (connection is null) return;
        var index = IndexOf(connection);
        if (index < 0) return;
        Selected.RemoveAt(index);
        SelectionChanged();
    }

    /// <summary>Removes several members (the multi-selection of the member list).</summary>
    public void RemoveRange(IEnumerable<ConnectionConfig> connections)
    {
        var removed = false;
        foreach (var c in connections.ToList())
        {
            var index = IndexOf(c);
            if (index < 0) continue;
            Selected.RemoveAt(index);
            removed = true;
        }
        if (removed) SelectionChanged();
    }

    [RelayCommand]
    public void MoveUp(ConnectionConfig? connection) => Move(connection, -1);

    [RelayCommand]
    public void MoveDown(ConnectionConfig? connection) => Move(connection, +1);

    /// <summary>Moves a member to a position of the list (drag and drop); the target is clamped to the list.</summary>
    public void MoveTo(ConnectionConfig connection, int targetIndex)
    {
        var index = IndexOf(connection);
        if (index < 0) return;
        targetIndex = Math.Clamp(targetIndex, 0, Selected.Count - 1);
        if (targetIndex == index) return;
        Selected.Move(index, targetIndex);
        SelectionChanged();
    }

    /// <summary>Validates; on success builds <see cref="Result"/> (the same id as the edited group) and returns true.</summary>
    public bool TryConfirm()
    {
        Result = null;
        _showEmptyName = true;
        var group = new ConnectionGroup(_existing?.Id ?? Guid.NewGuid(), Name.Trim(), Selected.Select(c => c.Id).ToList());
        NameError = NameProblem(includeEmpty: true);
        Error = GroupRules.Validate(_config, group);
        if (Error is not null) return false;
        Result = group;
        return true;
    }

    private void Move(ConnectionConfig? connection, int delta)
    {
        if (connection is null) return;
        var index = IndexOf(connection);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Selected.Count) return;
        Selected.Move(index, target);
        SelectionChanged();
    }

    private int IndexOf(ConnectionConfig connection)
    {
        for (var i = 0; i < Selected.Count; i++)
            if (Selected[i].Id == connection.Id) return i;
        return -1;
    }

    private void SelectionChanged()
    {
        Error = null;
        RefreshCandidates();
        OnPropertyChanged(nameof(Warning));
        OnPropertyChanged(nameof(MembersHint));
        OnPropertyChanged(nameof(IsMembersEmpty));
        OnPropertyChanged(nameof(NeedsMoreMembers));
        OnPropertyChanged(nameof(MembersTitle));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(EngineOnlyText));
    }

    private void RefreshCandidates()
    {
        var kind = Kind;
        var taken = Selected.Select(c => c.Id).ToHashSet();
        var filter = CandidateFilter.Trim();
        var free = _config.Connections.Where(c => !taken.Contains(c.Id)).ToList();
        HiddenCount = kind is null ? 0 : free.Count(c => c.Kind != kind);
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HiddenText));
        Candidates.Clear();
        foreach (var c in free
                     .Where(c => (kind is null || c.Kind == kind) && Matches(c, filter))
                     .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            Candidates.Add(c);
    }

    private static bool Matches(ConnectionConfig c, string filter) =>
        filter.Length == 0
        || Contains(c.Name, filter) || Contains(c.Folder, filter) || Contains(c.Host, filter) || Contains(c.Database, filter);

    private static bool Contains(string? text, string filter) => text?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
}
