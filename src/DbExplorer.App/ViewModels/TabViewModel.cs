using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.ViewModels;

/// <summary>What a tab needs to switch between the members of its connection's group(s).</summary>
/// <param name="GetConfig">The live application config (groups and connections).</param>
/// <param name="ResolveSession">The live session of a connection by id (connecting it through the tree when needed), or null
/// when it no longer exists; throws when it cannot connect. Also what Reload / Reconnect use, so they follow a switch.</param>
public sealed record GroupSwitchHooks(
    Func<AppConfig> GetConfig,
    Func<Guid, CancellationToken, Task<Session?>> ResolveSession);

/// <summary>
/// Base class of everything shown in the main tab area (table views, script editors). Subclasses provide the
/// title; the connection drives the colour chip on the tab header. A tab whose connection belongs to a group can be
/// switched to another member of that group (<see cref="SwitchConnectionAsync"/>); the subclass decides what that means.
/// </summary>
public abstract class TabViewModel : ObservableObject
{
    private ConnectionConfig _connection;
    private Guid? _activeGroupId;
    private bool _isSwitching;

    protected TabViewModel(ConnectionConfig connection) => _connection = connection;

    /// <summary>The connection the tab is on. Changes only through a group switch.</summary>
    public ConnectionConfig Connection
    {
        get => _connection;
        protected set
        {
            if (!SetProperty(ref _connection, value)) return;
            OnPropertyChanged(nameof(ChipColor));
            OnPropertyChanged(nameof(Title));
            NotifyGroupsChanged();
        }
    }

    /// <summary>Hooks for group switching; null (the default) means the tab cannot switch.</summary>
    protected virtual GroupSwitchHooks? GroupHooks => null;

    // ---- linked groups: switching between members ----

    /// <summary>Every group the tab's connection belongs to, in config order (empty when none).</summary>
    public IReadOnlyList<ConnectionGroup> Groups =>
        GroupHooks is { } hooks ? GroupRules.GroupsOf(hooks.GetConfig(), Connection.Id) : [];

    /// <summary>
    /// The group whose members the tab can switch between: the one chosen with the setter, else the first group of the
    /// connection; null when the connection is in no group. Setting a group the connection is not in is ignored.
    /// </summary>
    public ConnectionGroup? ActiveGroup
    {
        get
        {
            var groups = Groups;
            return groups.FirstOrDefault(g => g.Id == _activeGroupId) ?? groups.FirstOrDefault();
        }
        set
        {
            if (value is null || Groups.All(g => g.Id != value.Id) || value.Id == ActiveGroup?.Id) return;
            _activeGroupId = value.Id;
            NotifyGroupsChanged();
        }
    }

    /// <summary>The members of <see cref="ActiveGroup"/> in group order (this tab's connection included); empty when it is in no group.</summary>
    public IReadOnlyList<ConnectionConfig> GroupMembers =>
        GroupHooks is { } hooks && ActiveGroup is { } group ? GroupRules.MembersOf(hooks.GetConfig(), group) : [];

    /// <summary>The entry of <see cref="GroupMembers"/> that is the tab's connection (the selector's selected item), or null.</summary>
    public ConnectionConfig? SelectedMember => GroupMembers.FirstOrDefault(m => m.Id == Connection.Id);

    /// <summary>The database selector is shown: there is another member to switch to.</summary>
    public bool HasGroupSwitch => GroupMembers.Count > 1;

    /// <summary>The group picker is shown too: the connection is in several groups.</summary>
    public bool HasGroupPicker => HasGroupSwitch && Groups.Count > 1;

    /// <summary>True while a switch is in progress (connecting to the target): the selector is disabled, runs are refused.</summary>
    public bool IsSwitching
    {
        get => _isSwitching;
        private set => SetProperty(ref _isSwitching, value);
    }

    /// <summary>Repaints the selector after the groups or connections of the config changed.</summary>
    public void NotifyGroupsChanged()
    {
        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(ActiveGroup));
        OnPropertyChanged(nameof(GroupMembers));
        OnPropertyChanged(nameof(SelectedMember));
        OnPropertyChanged(nameof(HasGroupSwitch));
        OnPropertyChanged(nameof(HasGroupPicker));
    }

    /// <summary>
    /// Moves the tab to another member of its group. False when refused or failed: the tab is then exactly as it was
    /// (the reason is shown by the tab). A target outside the active group, a switch already in progress and a tab that
    /// cannot switch are refused; switching to the connection the tab is already on does nothing (true). Never throws.
    /// </summary>
    public async Task<bool> SwitchConnectionAsync(ConnectionConfig target)
    {
        if (IsSwitching) return false;
        var member = GroupMembers.FirstOrDefault(m => m.Id == target.Id);
        if (member is null)
        {
            ReportSwitchProblem($"{target.Name} is not in the group of this tab.");
            return false;
        }
        if (member.Id == Connection.Id) return true;

        IsSwitching = true;
        try
        {
            return await SwitchCoreAsync(member);
        }
        catch (Exception e)
        {
            ReportSwitchProblem($"Cannot switch to {member.Name}: {ErrorText.Describe(e)}");
            return false;
        }
        finally
        {
            IsSwitching = false;
        }
    }

    /// <summary>The next (<paramref name="delta"/> 1) or previous (-1) member of the group, wrapping around.</summary>
    public Task<bool> SwitchRelativeAsync(int delta)
    {
        var members = GroupMembers;
        if (members.Count < 2) return Task.FromResult(false);
        var index = members.ToList().FindIndex(m => m.Id == Connection.Id);
        var next = index < 0 ? (delta > 0 ? 0 : members.Count - 1) : ((index + delta) % members.Count + members.Count) % members.Count;
        return SwitchConnectionAsync(members[next]);
    }

    /// <summary>Does the tab-specific part of a switch to <paramref name="target"/> (a member of the group, not the current connection).</summary>
    protected virtual Task<bool> SwitchCoreAsync(ConnectionConfig target) => Task.FromResult(false);

    /// <summary>Shows why a switch was refused (error strip / status bar).</summary>
    protected virtual void ReportSwitchProblem(string message) { }

    public abstract string Title { get; }

    /// <summary>
    /// Asked before the tab is closed; false vetoes the close. Tabs with unsaved work override it to prompt.
    /// </summary>
    public virtual Task<bool> ConfirmCloseAsync() => Task.FromResult(true);

    /// <summary>
    /// True when <see cref="ConfirmCloseAsync"/> is going to ask the user something. The shell activates such a tab
    /// before asking, so the prompt is about the tab on screen. Tabs with nothing to ask are left alone.
    /// </summary>
    public virtual bool NeedsCloseConfirmation => false;

    /// <summary>
    /// <see cref="ConfirmCloseAsync"/> for the window being closed (not a single tab): a tab whose unsaved work is kept
    /// by other means (a script's autosave) can answer yes without asking. Defaults to the tab-close prompt.
    /// </summary>
    public virtual Task<bool> ConfirmExitAsync() => ConfirmCloseAsync();

    /// <summary>True when <see cref="ConfirmExitAsync"/> is going to ask (the shell shows the tab first).</summary>
    public virtual bool NeedsExitConfirmation => NeedsCloseConfirmation;

    /// <summary>
    /// Called on a tab whose <see cref="ConfirmCloseAsync"/> said yes when the close it was part of was abandoned
    /// afterwards (another tab answered Cancel): the tab stays open and must go back to normal.
    /// </summary>
    public virtual void CloseAborted() { }

    /// <summary>Short status shown next to the title (e.g. "read-only"), or null.</summary>
    public virtual string? Badge => null;

    public string ChipColor => ConnectionPalette.ColorFor(Connection.Id);
    /// <summary>Resource key of the vector icon (Styles/Icons.axaml) drawn on the tab header.</summary>
    public virtual string IconKey => "IconTable";

    /// <summary>True while the tab holds changes that are not saved yet: its header shows a dirty marker.</summary>
    public virtual bool HasUnsavedWork => false;
}
