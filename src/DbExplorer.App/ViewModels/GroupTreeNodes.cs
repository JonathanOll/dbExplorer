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
        DbKind.MySql => "MySQL / MariaDB",
        _ => kind.ToString(),
    };
}

/// <summary>
/// A member of a group: a shortcut to the real connection node (same object, so its connected state, colour and read-only
/// flag are always the connection's own). Activating it connects that connection.
/// </summary>
public sealed class GroupMemberNodeViewModel : TreeNodeViewModel
{
    public GroupMemberNodeViewModel(ConnectionNodeViewModel target, ConnectionGroup group)
    {
        Target = target;
        Group = group;
        MirrorChildren();
        target.PropertyChanged += OnTargetChanged;
        PropertyChanged += OnSelfChanged;
    }

    public ConnectionNodeViewModel Target { get; }
    public ConnectionGroup Group { get; }
    public override string Title => Target.Title;
    public ConnectionTreeViewModel? Tree { get; set; }

    /// <summary>
    /// The member shows the schema of the real connection: the very same child nodes (schemas, tables), so opening a table from
    /// here or from the connection is one thing. Until the connection is open a "Loading…" child stands in, so the expander
    /// is there: expanding the member connects it.
    /// </summary>
    private void MirrorChildren()
    {
        if (Target.Children.Count > 0) Children = Target.Children;
        else SetChildren([new PlaceholderNodeViewModel()]);
    }

    private void OnTargetChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Children)) MirrorChildren();
    }

    private void OnSelfChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsExpanded) || !IsExpanded || Target.IsConnecting) return;
        if (Target.IsConnected) Target.IsExpanded = true;   // realizes tables a big schema has not created yet; the children are shared
        else if (Tree is { } tree) _ = tree.OpenGroupMemberAsync(this);
    }

    /// <summary>Stops following the connection: the tree calls it when it replaces this node (the connection outlives it).</summary>
    public void Detach()
    {
        Target.PropertyChanged -= OnTargetChanged;
        PropertyChanged -= OnSelfChanged;
    }
}
