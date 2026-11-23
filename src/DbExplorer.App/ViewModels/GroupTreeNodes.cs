using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>The "Groups" section of the navigator: one child per group. Absent from the tree while there are no groups.</summary>
public sealed class GroupsSectionNodeViewModel : TreeNodeViewModel
{
    public override string Title => "Groups";

    /// <summary>Owner whose commands the context menu binds to.</summary>
    public ConnectionTreeViewModel? Tree { get; set; }
}

/// <summary>A group: expands to its members, in the order of the group.</summary>
public sealed partial class GroupNodeViewModel(ConnectionGroup group) : TreeNodeViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(MemberCount), nameof(IsUnderPopulated), nameof(Summary))]
    private ConnectionGroup _group = group;

    /// <summary>Engine shared by the members (null for an empty group); refreshed by the tree on every rebuild.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private DbKind? _kind;

    public override string Title => Group.Name;

    /// <summary>Members that resolve to a connection (the child nodes); an id of a deleted connection is not counted.</summary>
    public int MemberCount => Children.Count;

    /// <summary>A group of fewer than two databases is allowed, but there is nothing to switch to or compare.</summary>
    public bool IsUnderPopulated => MemberCount < 2;

    /// <summary>Dim text after the name, e.g. "SQLite · 3".</summary>
    public string Summary => Kind is { } kind ? $"{EngineName(kind)} · {MemberCount}" : $"{MemberCount}";

    public ConnectionTreeViewModel? Tree { get; set; }

    /// <summary>The group's member list is edited in place by some operations (e.g. deleting a connection): re-reads it.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(MemberCount));
        OnPropertyChanged(nameof(IsUnderPopulated));
        OnPropertyChanged(nameof(Summary));
    }

    private static string EngineName(DbKind kind) => kind switch
    {
        DbKind.Postgres => "PostgreSQL",
        DbKind.SqlServer => "SQL Server",
        DbKind.Sqlite => "SQLite",
        _ => kind.ToString(),
    };
}

/// <summary>
/// A member of a group: a shortcut to the real connection node (same object, so its connected state, colour and read-only
/// flag are always the connection's own). Activating it connects that connection.
/// </summary>
public sealed class GroupMemberNodeViewModel(ConnectionNodeViewModel target, ConnectionGroup group) : TreeNodeViewModel
{
    public ConnectionNodeViewModel Target { get; } = target;
    public ConnectionGroup Group { get; } = group;
    public override string Title => Target.Title;
    public ConnectionTreeViewModel? Tree { get; set; }
}
