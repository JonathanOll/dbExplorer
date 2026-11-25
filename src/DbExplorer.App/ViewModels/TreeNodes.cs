using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.ViewModels;

public abstract partial class TreeNodeViewModel : ObservableObject
{
    private Func<IReadOnlyList<TreeNodeViewModel>>? _lazyChildren;

    [ObservableProperty] private bool _isExpanded;

    /// <summary>False while the navigator filter hides the node (the container is collapsed, the node stays in the tree).</summary>
    [ObservableProperty] private bool _isShown = true;

    public abstract string Title { get; }

    [ObservableProperty] private ObservableCollection<TreeNodeViewModel> _children = new();

    /// <summary>True while the real children have not been created yet (a placeholder stands in for them).</summary>
    public bool HasUnrealizedChildren => _lazyChildren is not null;

    /// <summary>
    /// Shows a placeholder child (so the expander appears) and builds the real children only when the node is
    /// first expanded; thousands of tables must not become thousands of view models up front.
    /// </summary>
    public void SetLazyChildren(Func<IReadOnlyList<TreeNodeViewModel>> factory)
    {
        _lazyChildren = factory;
        Children = new ObservableCollection<TreeNodeViewModel> { new PlaceholderNodeViewModel() };
        if (IsExpanded) RealizeChildren();
    }

    /// <summary>Replaces the children in one assignment so a large list raises a single reset.</summary>
    public void SetChildren(IEnumerable<TreeNodeViewModel> nodes)
    {
        _lazyChildren = null;
        Children = new ObservableCollection<TreeNodeViewModel>(nodes);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) RealizeChildren();
    }

    private void RealizeChildren()
    {
        if (_lazyChildren is not { } factory) return;
        _lazyChildren = null;
        Children = new ObservableCollection<TreeNodeViewModel>(factory());
    }
}

/// <summary>Stand-in child of a node whose children are created on first expansion.</summary>
public sealed class PlaceholderNodeViewModel : TreeNodeViewModel
{
    public override string Title => "Loading…";
}

public sealed class FolderNodeViewModel : TreeNodeViewModel
{
    private List<ConnectionNodeViewModel> _watched = [];

    public FolderNodeViewModel(string name)
    {
        Name = name;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsExpanded)) OnPropertyChanged(nameof(CountText));
        };
    }

    public string Name { get; }
    public override string Title => Name;

    /// <summary>How many connections of the folder are connected.</summary>
    public int ConnectedCount => Children.OfType<ConnectionNodeViewModel>().Count(c => c.IsConnected);

    /// <summary>"(3)", or "(3 · 1 connected)" while the folder is collapsed and something in it is connected.</summary>
    public string CountText => !IsExpanded && ConnectedCount > 0 ? $"({Children.Count} · {ConnectedCount} connected)" : $"({Children.Count})";

    /// <summary>Follows the connection state of the current children (call after they were replaced).</summary>
    public void WatchChildren()
    {
        foreach (var old in _watched) old.PropertyChanged -= OnChildChanged;
        _watched = Children.OfType<ConnectionNodeViewModel>().ToList();
        foreach (var child in _watched) child.PropertyChanged += OnChildChanged;
        OnPropertyChanged(nameof(ConnectedCount));
        OnPropertyChanged(nameof(CountText));
    }

    private void OnChildChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConnectionNodeViewModel.IsConnected)) return;
        OnPropertyChanged(nameof(ConnectedCount));
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>Owner whose commands the context menu binds to.</summary>
    public ConnectionTreeViewModel? Tree { get; set; }
}

public enum ConnectionState { Disconnected, Connecting, Connected, Failed }

public sealed partial class ConnectionNodeViewModel(ConnectionConfig config) : TreeNodeViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(IsReadOnly), nameof(Tooltip))]
    private ConnectionConfig _config = config;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnecting), nameof(IsConnected), nameof(HasError), nameof(IsDisconnected), nameof(StateText), nameof(Tooltip))]
    private ConnectionState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip), nameof(StateText))]
    private string? _errorMessage;

    public override string Title => Config.Name;

    /// <summary>Owner whose commands the context menu binds to.</summary>
    public ConnectionTreeViewModel? Tree { get; set; }
    public bool IsReadOnly => Config.ReadOnly;
    public bool IsConnecting => State == ConnectionState.Connecting;
    public bool IsConnected => State == ConnectionState.Connected;
    public bool HasError => State == ConnectionState.Failed;
    public bool IsDisconnected => State == ConnectionState.Disconnected;

    /// <summary>The state in words (tooltips, screen readers): "Connected", "Connecting…", "Failed: reason" or "Not connected".</summary>
    public string StateText => State switch
    {
        ConnectionState.Connected => "Connected",
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Failed => string.IsNullOrWhiteSpace(ErrorMessage) ? "Failed" : $"Failed: {ErrorMessage}",
        _ => "Not connected",
    };

    /// <summary>Row tooltip: name, engine and target, state, read-only. Never the password.</summary>
    public string Tooltip
    {
        get
        {
            var lines = new List<string> { Config.Name, $"{EngineName(Config.Kind)} · {Target(Config)}", StateText };
            if (Config.ReadOnly) lines.Add("Read-only: no editing, scripts refuse writes");
            if (HasError) lines.Add("Double-click to retry");
            return string.Join(Environment.NewLine, lines);
        }
    }

    private static string EngineName(DbKind kind) => kind switch
    {
        DbKind.Postgres => "PostgreSQL",
        DbKind.SqlServer => "SQL Server",
        DbKind.Sqlite => "SQLite",
        _ => kind.ToString(),
    };

    private static string Target(ConnectionConfig c)
    {
        if (c.Kind == DbKind.Sqlite) return c.Database;
        var host = string.IsNullOrWhiteSpace(c.Host) ? "localhost" : c.Host;
        var port = c.Port is { } p ? $":{p}" : "";
        var user = string.IsNullOrWhiteSpace(c.User) ? "" : $" · {c.User}";
        return $"{host}{port}/{c.Database}{user}";
    }

    public string ChipColor => ConnectionPalette.ColorFor(Config.Id);
}

public sealed class SchemaNodeViewModel : TreeNodeViewModel
{
    private readonly string _name;

    /// <summary>Table nodes are created when the schema node is first expanded.</summary>
    public SchemaNodeViewModel(string name, int tableCount, Func<IReadOnlyList<TreeNodeViewModel>> tables)
    {
        _name = name;
        TableCount = tableCount;
        SetLazyChildren(tables);
    }

    public int TableCount { get; }

    public override string Title => _name;
}

public sealed class TableNodeViewModel(Session session, TableInfo table) : TreeNodeViewModel
{
    public Session Session { get; } = session;
    public TableInfo Table { get; } = table;
    public override string Title => Table.Ref.Name;
    public bool IsView => Table.IsView;
    public string Badge => Table.IsView ? "V" : "T";
}



