using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.ViewModels;

public enum ObjectGroupKind { Views, Procedures, Functions }

/// <summary>
/// "Views", "Procedures" or "Functions" under a schema (under the connection for SQLite). The navigator filter looks at
/// what is inside, never at this fixed title (typing "proc" must not show every schema).
/// </summary>
public abstract partial class ObjectGroupNodeViewModel(ObjectGroupKind kind) : TreeNodeViewModel
{
    public ObjectGroupKind Kind { get; } = kind;
    public override string Title => Kind.ToString();

    /// <summary>Resource key of the group's icon (Styles/Icons.axaml).</summary>
    public string IconKey => Kind switch
    {
        ObjectGroupKind.Views => "IconView",
        ObjectGroupKind.Procedures => "IconProcedure",
        _ => "IconFunction",
    };

    /// <summary>"(3)"; empty while the content is not known.</summary>
    [ObservableProperty] private string _countText = "";
}

/// <summary>The views of a schema: they come with the tables, their nodes are created on first expansion.</summary>
public sealed class ViewGroupNodeViewModel : ObjectGroupNodeViewModel
{
    public ViewGroupNodeViewModel(int count, Func<IReadOnlyList<TreeNodeViewModel>> views) : base(ObjectGroupKind.Views)
    {
        CountText = $"({count})";
        SetLazyChildren(views);
    }
}

/// <summary>
/// The procedures or functions of a schema. Nothing is read until the node is first expanded; one read serves every
/// group of the session. A failed or cancelled read says so in a child row, and expanding again retries.
/// </summary>
public sealed class RoutineGroupNodeViewModel : ObjectGroupNodeViewModel
{
    private readonly Session _session;
    private readonly string _schema;
    private readonly StatusModel? _status;
    private bool _loaded;
    private Task? _loading;

    public RoutineGroupNodeViewModel(Session session, ObjectGroupKind kind, string schema, StatusModel? status = null) : base(kind)
    {
        _session = session;
        _schema = schema;
        _status = status;
        SetChildren([new PlaceholderNodeViewModel()]);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsExpanded) && IsExpanded) _ = LoadAsync();
        };
    }

    /// <summary>Raised once the routines are listed (the navigator applies its filter to them).</summary>
    public event Action? Loaded;

    /// <summary>Reads and lists the routines (once). Never throws.</summary>
    public Task LoadAsync()
    {
        if (_loaded) return Task.CompletedTask;
        return _loading is { IsCompleted: false } running ? running : _loading = LoadCoreAsync();
    }

    private async Task LoadCoreAsync()
    {
        var what = Title.ToLowerInvariant();
        SetChildren([new PlaceholderNodeViewModel()]);
        using var cts = new CancellationTokenSource();
        using var busy = _status?.Busy($"Reading the {what} of {_session.Config.Name}…", () => cts.Cancel());
        try
        {
            var all = await _session.LoadRoutinesAsync(cts.Token);
            var kind = Kind == ObjectGroupKind.Procedures ? RoutineKind.Procedure : RoutineKind.Function;
            var mine = all
                .Where(r => r.Kind == kind && string.Equals(r.Ref.Schema ?? "", _schema, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Ref.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Arguments, StringComparer.OrdinalIgnoreCase)
                .Select(r => (TreeNodeViewModel)new RoutineNodeViewModel(_session, r))
                .ToList();
            _loaded = true;
            CountText = $"({mine.Count})";
            SetChildren(mine.Count > 0 ? mine : [new MessageNodeViewModel($"No {what}")]);
            Loaded?.Invoke();
        }
        catch (OperationCanceledException)
        {
            SetChildren([new MessageNodeViewModel("Cancelled: collapse and expand to retry")]);
        }
        catch (Exception e)
        {
            var reason = ErrorText.Describe(e);
            SetChildren([new MessageNodeViewModel($"Cannot read: {reason}")]);
            _status?.Error($"Cannot read the {what} of {_session.Config.Name}: {reason?.TrimEnd('.')}. Collapse and expand to retry.");
        }
    }
}

/// <summary>A stored procedure or function. Activating it opens its source text.</summary>
public sealed class RoutineNodeViewModel(Session session, RoutineInfo routine) : TreeNodeViewModel
{
    public Session Session { get; } = session;
    public RoutineInfo Routine { get; } = routine;
    public override string Title => Routine.Ref.Name;

    /// <summary>"(integer, text)" where the engine has overloads, else null.</summary>
    public string? Signature => Routine.Arguments is null ? null : $"({Routine.Arguments})";

    public string IconKey => Routine.Kind == RoutineKind.Procedure ? "IconProcedure" : "IconFunction";
}

/// <summary>A line of text standing where a list has nothing to show (empty or unreadable).</summary>
public sealed class MessageNodeViewModel(string text) : TreeNodeViewModel
{
    public override string Title => text;
}
