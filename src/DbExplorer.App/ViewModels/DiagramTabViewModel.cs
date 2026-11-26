using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Diagrams;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.ViewModels;

/// <summary>What an ER diagram is opened on: the whole database, one schema of it, or one table and its neighbours.</summary>
public sealed record DiagramRequest(string? Schema = null, TableRef? Table = null);

/// <summary>What a diagram tab needs from the shell.</summary>
/// <param name="ResolveSession">The live session of the tab's connection (connecting it when needed), or null when the
/// connection no longer exists; throws when it cannot connect.</param>
/// <param name="OpenTable">Opens the data of a table in a tab.</param>
public sealed record DiagramHooks(
    Func<CancellationToken, Task<Session?>> ResolveSession,
    Func<Session, TableRef, Task> OpenTable);

/// <summary>One schema of the filter.</summary>
public sealed partial class DiagramSchemaChoice(string name, bool isShown) : ObservableObject
{
    public string Name { get; } = name;
    public string Label => Name.Length == 0 ? "(no schema)" : Name;
    [ObservableProperty] private bool _isShown = isShown;
}

/// <summary>
/// An ER diagram of a connection: which tables are shown (schemas, or the neighbourhood of one table), how their cards
/// look, the zoom, the search and the selection. Reading and layout run off the UI thread; a newer request cancels the one
/// in flight. Only catalog queries are ever sent.
/// </summary>
public sealed partial class DiagramTabViewModel : TabViewModel
{
    public const double MinZoom = 0.05;
    public const double MaxZoom = 2;
    public const int MaxDepth = 9;

    private readonly DiagramRequest _request;
    private readonly DiagramHooks _hooks;
    private IReadOnlyList<TableInfo>? _tables;
    private CancellationTokenSource? _work;
    private string _searched = "";
    private int _searchIndex;
    private bool _settingUp;

    public DiagramTabViewModel(ConnectionConfig connection, DiagramRequest request, DiagramHooks hooks) : base(connection)
    {
        _request = request;
        _hooks = hooks;
        _focus = request.Table;
    }

    public DiagramRequest Request => _request;

    public override string Title => $"ER · {_request.Table?.Name ?? _request.Schema ?? Connection.Name}";

    public override string IconKey => "IconDiagram";

    /// <summary>The load or layout in progress; completed when the diagram is up to date.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>The diagram to draw; null until the first layout.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(IsEmpty), nameof(CanExport), nameof(ExportTip))]
    private DiagramScene? _scene;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(CanReload), nameof(ReloadTip))]
    private bool _isBusy;

    /// <summary>Why the diagram could not be read or laid out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsEmpty))]
    private string? _error;

    public bool HasError => Error is not null;

    /// <summary>Nothing to draw (and not because it is still coming or failed).</summary>
    public bool IsEmpty => !IsBusy && Error is null && Scene is { Model.Tables.Count: 0 };

    public string EmptyText => Focus is { } table ? $"{table} is not a table of this database any more. Reload, or show all tables."
        : SchemaChoices.Count > 0 && SchemaChoices.All(s => !s.IsShown) ? "No schema is selected."
        : "There is no table to draw.";

    public string Summary => Scene is not { } scene ? ""
        : $"{Count(scene.Model.Tables.Count, "table")} · {Count(scene.Model.Relations.Count, "relation")}";

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n:N0} {what}s";

    public bool CanExport => Scene is { Model.Tables.Count: > 0 };

    public string ExportTip => CanExport ? "Save the whole diagram as a PNG picture" : "There is nothing to export yet.";

    public bool CanReload => !IsBusy;

    public string ReloadTip => CanReload ? "Read the tables and keys again from the database" : "The diagram is being read.";

    // ---- loading ----

    /// <summary>Draws the diagram from the schema the session already has. Never throws.</summary>
    public Task LoadAsync() => Pending = RunAsync(read: false);

    /// <summary>Reads the metadata again from the database, then draws. Never throws.</summary>
    [RelayCommand]
    public Task ReloadAsync() => Pending = RunAsync(read: true);

    /// <summary>Lays the tables out afresh (drops the positions set by dragging).</summary>
    [RelayCommand]
    public Task ArrangeAsync() => Pending = RunAsync(read: false);

    [RelayCommand]
    private void Cancel() => _work?.Cancel();

    /// <summary>Stops the work in progress: the tab is closing.</summary>
    public void Close() => _work?.Cancel();

    private async Task RunAsync(bool read)
    {
        _work?.Cancel();
        var work = _work = new CancellationTokenSource();
        var ct = work.Token;
        IsBusy = true;
        Error = null;
        try
        {
            if (_tables is null || read)
            {
                var session = await _hooks.ResolveSession(ct)
                    ?? throw new InvalidOperationException("The connection of this diagram no longer exists.");
                var tables = read ? await session.ReadSchemaAsync(ct) : session.Schema;
                ct.ThrowIfCancellationRequested();
                _tables = tables;
                SetUpSchemas(DiagramBuilder.Schemas(tables));
            }

            // Captured on this thread: the layout runs on another one.
            var source = _tables;
            var scope = Scope();
            var mode = ColumnMode;
            var scene = await Task.Run(() =>
            {
                var model = DiagramBuilder.Build(source, scope, ct);
                var several = model.Tables.Select(t => t.Ref.Schema).Distinct().Count() > 1;
                return DiagramScene.Create(model, mode, several, ct);
            }, ct);

            if (!ReferenceEquals(work, _work)) return;
            SelectedTable = Focus is { } focus ? IndexOf(scene, focus) : -1;
            _searched = "";
            SearchStatus = "";
            Scene = scene;
            OnPropertyChanged(nameof(EmptyText));
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(work, _work) && Scene is null) Error = "Reading the diagram was cancelled.";
        }
        catch (Exception e)
        {
            if (ReferenceEquals(work, _work)) Error = $"Cannot draw the diagram: {ErrorText.Describe(e)}";
        }
        finally
        {
            if (ReferenceEquals(work, _work)) IsBusy = false;
        }
    }

    private static int IndexOf(DiagramScene scene, TableRef table)
    {
        for (var i = 0; i < scene.Model.Tables.Count; i++)
            if (string.Equals(scene.Model.Tables[i].Ref.ToString(), table.ToString(), StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private DiagramScope Scope() => Focus is { } focus
        ? new DiagramScope(Focus: focus, Depth: Depth)
        : new DiagramScope(SchemaChoices.Count == 0 ? null : SchemaChoices.Where(s => s.IsShown).Select(s => s.Name).ToHashSet(StringComparer.Ordinal));

    private void Redraw()
    {
        // Before the first load there is nothing to redraw: the load in flight reads the new value itself.
        if (_tables is not null && !_settingUp) _ = ArrangeAsync();
    }

    // ---- columns ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllColumns), nameof(IsKeyColumns), nameof(IsNamesOnly))]
    private DiagramColumnMode _columnMode = DiagramColumnMode.All;

    partial void OnColumnModeChanged(DiagramColumnMode value) => Redraw();

    public bool IsAllColumns
    {
        get => ColumnMode == DiagramColumnMode.All;
        set { if (value) ColumnMode = DiagramColumnMode.All; }
    }

    public bool IsKeyColumns
    {
        get => ColumnMode == DiagramColumnMode.Keys;
        set { if (value) ColumnMode = DiagramColumnMode.Keys; }
    }

    public bool IsNamesOnly
    {
        get => ColumnMode == DiagramColumnMode.Names;
        set { if (value) ColumnMode = DiagramColumnMode.Names; }
    }

    // ---- schemas ----

    public ObservableCollection<DiagramSchemaChoice> SchemaChoices { get; } = [];

    /// <summary>The schema filter is offered when there are several schemas and the whole diagram is shown.</summary>
    public bool HasSchemaFilter => SchemaChoices.Count > 1 && !IsFocused;

    public string SchemaFilterText
    {
        get
        {
            var shown = SchemaChoices.Where(s => s.IsShown).ToList();
            return shown.Count == SchemaChoices.Count ? "All schemas"
                : shown.Count == 1 ? shown[0].Label
                : $"{shown.Count} of {SchemaChoices.Count} schemas";
        }
    }

    [RelayCommand]
    private void ShowAllSchemas()
    {
        _settingUp = true;
        foreach (var choice in SchemaChoices) choice.IsShown = true;
        _settingUp = false;
        Redraw();
    }

    // Keeps the user's choice across a reload; at first the requested schema, or all of them.
    private void SetUpSchemas(IReadOnlyList<string> names)
    {
        var before = SchemaChoices.ToDictionary(s => s.Name, s => s.IsShown, StringComparer.Ordinal);
        var first = before.Count == 0;
        var wanted = first && _request.Schema is { } s && names.Contains(s) ? s : null;
        _settingUp = true;
        SchemaChoices.Clear();
        foreach (var name in names)
        {
            var choice = new DiagramSchemaChoice(name,
                first ? wanted is null || name == wanted : before.GetValueOrDefault(name, true));
            choice.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(SchemaFilterText));
                Redraw();
            };
            SchemaChoices.Add(choice);
        }
        _settingUp = false;
        OnPropertyChanged(nameof(HasSchemaFilter));
        OnPropertyChanged(nameof(SchemaFilterText));
    }

    // ---- neighbourhood ----

    /// <summary>The table whose neighbourhood is shown; null for the whole diagram.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFocused), nameof(FocusText), nameof(HasSchemaFilter))]
    private TableRef? _focus;

    public bool IsFocused => Focus is not null;

    public string FocusText => Focus is { } table ? $"Around {table}" : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoDeeper), nameof(CanGoShallower), nameof(DeeperTip), nameof(ShallowerTip))]
    private int _depth = 1;

    partial void OnDepthChanged(int value)
    {
        if (IsFocused) Redraw();
    }

    public bool CanGoDeeper => Depth < MaxDepth;
    public bool CanGoShallower => Depth > 1;
    public string DeeperTip => CanGoDeeper ? "Follow one more foreign key from the table" : $"{MaxDepth} is the largest depth.";
    public string ShallowerTip => CanGoShallower ? "Follow one foreign key less" : "1 is the smallest depth: the direct neighbours.";

    [RelayCommand]
    private void Deeper() => Depth = Math.Min(MaxDepth, Depth + 1);

    [RelayCommand]
    private void Shallower() => Depth = Math.Max(1, Depth - 1);

    /// <summary>Shows only a table and its neighbours.</summary>
    public void FocusOn(TableRef table)
    {
        Focus = table;
        Redraw();
    }

    [RelayCommand]
    public void ShowAll()
    {
        if (Focus is null) return;
        Focus = null;
        Redraw();
    }

    // ---- selection, search, opening ----

    /// <summary>The highlighted table (index in the scene), or -1.</summary>
    [ObservableProperty] private int _selectedTable = -1;

    [ObservableProperty] private string _searchText = "";

    /// <summary>"2 of 5", "No table", or empty before a search.</summary>
    [ObservableProperty] private string _searchStatus = "";

    /// <summary>Asks the view to put a table in the middle of the canvas.</summary>
    public event Action<int>? CentreRequested;

    partial void OnSearchTextChanged(string value) => Search(0);

    /// <summary>Selects and centres a match of the search box: the next one (1), the previous one (-1) or the first (0).</summary>
    public void Search(int step)
    {
        var matches = Scene?.Find(SearchText) ?? [];
        if (matches.Count == 0)
        {
            SearchStatus = string.IsNullOrWhiteSpace(SearchText) ? "" : "No table";
            _searched = "";
            return;
        }
        _searchIndex = step == 0 || _searched != SearchText ? 0 : ((_searchIndex + step) % matches.Count + matches.Count) % matches.Count;
        _searched = SearchText;
        SearchStatus = $"{_searchIndex + 1} of {matches.Count}";
        SelectedTable = matches[_searchIndex];
        CentreRequested?.Invoke(SelectedTable);
    }

    /// <summary>Opens the data of a table of the diagram. Never throws; failures show in the tab.</summary>
    public async Task OpenTableAsync(int table)
    {
        if (Scene is not { } scene || table < 0 || table >= scene.Model.Tables.Count) return;
        var name = scene.Model.Tables[table].Ref;
        try
        {
            if (await _hooks.ResolveSession(CancellationToken.None) is { } session) await _hooks.OpenTable(session, name);
            else Error = "The connection of this diagram no longer exists.";
        }
        catch (Exception e)
        {
            Error = $"Cannot open {name}: {ErrorText.Describe(e)}";
        }
    }

    // ---- zoom ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    private double _zoom = 1;

    public string ZoomText => $"{Math.Round(Zoom * 100)}%";

    public void SetZoom(double zoom) => Zoom = Math.Clamp(Math.Round(zoom, 3), MinZoom, MaxZoom);

    /// <summary>
    /// One zoom step (a wheel notch is 0.1, as on an execution plan). Below 50% the step shrinks with the zoom, so a very
    /// large diagram stays controllable.
    /// </summary>
    public void ZoomBy(double step) => SetZoom(Zoom + step * Math.Min(1, Zoom * 2));

    /// <summary>The zoom that fits the whole diagram in a viewport, never above 100% nor below <paramref name="floor"/>.</summary>
    public double FitZoom(double viewWidth, double viewHeight, double floor = MinZoom) =>
        Scene is not { Width: > 0, Height: > 0 } s ? 1
            : Math.Clamp(Math.Min(Math.Min((viewWidth - 4) / s.Width, (viewHeight - 4) / s.Height), 1), floor, 1);
}
