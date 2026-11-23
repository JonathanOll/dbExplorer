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

    [ObservableProperty]
    private string _name = "";

    /// <summary>Connections that can still be added: the engine of the first member (every connection while the group is empty).</summary>
    public ObservableCollection<ConnectionConfig> Candidates { get; } = new();

    /// <summary>The members, in group order.</summary>
    public ObservableCollection<ConnectionConfig> Selected { get; } = new();

    /// <summary>Why the group cannot be saved (set by <see cref="TryConfirm"/>, cleared by the next change); null when fine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => Error is not null;

    /// <summary>Non-blocking hint shown while the group has fewer than two members.</summary>
    public string? Warning => Selected.Count < 2
        ? "A group needs at least 2 databases to switch between them or compare them. You can still save it."
        : null;

    /// <summary>The engine members must share (that of the first member); null while the group is empty.</summary>
    public DbKind? Kind => Selected.Count > 0 ? Selected[0].Kind : null;

    /// <summary>The valid group, set by <see cref="TryConfirm"/>.</summary>
    public ConnectionGroup? Result { get; private set; }

    partial void OnNameChanged(string value) => Error = null;

    /// <summary>Appends a connection to the members. Nothing happens when it is already one.</summary>
    [RelayCommand]
    public void Add(ConnectionConfig? connection)
    {
        if (connection is null || Selected.Any(c => c.Id == connection.Id)) return;
        Selected.Add(connection);
        SelectionChanged();
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

    [RelayCommand]
    public void MoveUp(ConnectionConfig? connection) => Move(connection, -1);

    [RelayCommand]
    public void MoveDown(ConnectionConfig? connection) => Move(connection, +1);

    /// <summary>Validates; on success builds <see cref="Result"/> (the same id as the edited group) and returns true.</summary>
    public bool TryConfirm()
    {
        Result = null;
        var group = new ConnectionGroup(_existing?.Id ?? Guid.NewGuid(), Name.Trim(), Selected.Select(c => c.Id).ToList());
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
        OnPropertyChanged(nameof(Kind));
    }

    private void RefreshCandidates()
    {
        var kind = Kind;
        var taken = Selected.Select(c => c.Id).ToHashSet();
        Candidates.Clear();
        foreach (var c in _config.Connections
                     .Where(c => !taken.Contains(c.Id) && (kind is null || c.Kind == kind))
                     .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            Candidates.Add(c);
    }
}
