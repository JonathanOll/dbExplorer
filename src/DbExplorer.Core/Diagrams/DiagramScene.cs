namespace DbExplorer.Core.Diagrams;

/// <summary>
/// A laid out diagram as it is drawn: the model, a rectangle per table (which dragging changes) and what the drawing and
/// the pointer need to know about them. Not thread safe: built off the UI thread, then owned by it.
/// </summary>
public sealed class DiagramScene
{
    private readonly DiagramRect[] _rects;
    private readonly IReadOnlyList<DiagramColumn>[] _columns;
    private readonly HashSet<int>[] _neighbours;
    private readonly List<int> _order;

    public DiagramScene(DiagramModel model, DiagramPlacement placement, DiagramColumnMode mode, bool showSchema)
    {
        Model = model;
        Mode = mode;
        ShowSchema = showSchema;
        _rects = [.. placement.Tables];
        _columns = model.Tables.Select(t => DiagramMetrics.VisibleColumns(t, mode)).ToArray();
        _neighbours = model.Tables.Select(_ => new HashSet<int>()).ToArray();
        foreach (var r in model.Relations)
        {
            _neighbours[r.Child].Add(r.Parent);
            _neighbours[r.Parent].Add(r.Child);
        }
        _order = Enumerable.Range(0, model.Tables.Count).ToList();
        Width = placement.Width;
        Height = placement.Height;
    }

    /// <summary>Builds and lays out a diagram. Safe to call off the UI thread.</summary>
    public static DiagramScene Create(DiagramModel model, DiagramColumnMode mode, bool showSchema = false, CancellationToken ct = default) =>
        new(model, DiagramLayout.Arrange(model, mode, showSchema, ct), mode, showSchema);

    public DiagramModel Model { get; }
    public DiagramColumnMode Mode { get; }

    /// <summary>The card titles carry the schema (the diagram spans several).</summary>
    public bool ShowSchema { get; }

    public double Width { get; private set; }
    public double Height { get; private set; }

    /// <summary>A table moved (the canvas may have grown or shrunk).</summary>
    public event Action? Changed;

    public DiagramRect Rect(int table) => _rects[table];

    /// <summary>Table indexes from the bottom of the pile to the top (the last one dragged is drawn last).</summary>
    public IReadOnlyList<int> DrawOrder => _order;

    /// <summary>The columns listed on a card; none in the names-only mode.</summary>
    public IReadOnlyList<DiagramColumn> Columns(int table) => _columns[table];

    /// <summary>True when a foreign key links the two tables.</summary>
    public bool AreLinked(int a, int b) => _neighbours[a].Contains(b);

    public DiagramRoute Route(DiagramRelation relation) => DiagramRoute.Between(
        _rects[relation.Child], Anchor(relation.Child, relation.Columns),
        _rects[relation.Parent], Anchor(relation.Parent, relation.RefColumns),
        relation.IsSelf);

    private double Anchor(int table, IReadOnlyList<string> columns) =>
        DiagramMetrics.AnchorY(Columns(table), columns.Count > 0 ? columns[0] : null);

    /// <summary>The topmost table under a point, or -1.</summary>
    public int HitTest(double x, double y)
    {
        for (var i = _order.Count - 1; i >= 0; i--)
            if (_rects[_order[i]].Contains(x, y)) return _order[i];
        return -1;
    }

    /// <summary>Moves a table (kept on the canvas: never left of or above its origin) and puts it on top.</summary>
    public void Move(int table, double x, double y)
    {
        _rects[table] = _rects[table] with { X = Math.Max(0, x), Y = Math.Max(0, y) };
        if (_order[^1] != table)
        {
            _order.Remove(table);
            _order.Add(table);
        }
        Width = _rects.Max(r => r.Right) + DiagramLayout.Margin;
        Height = _rects.Max(r => r.Bottom) + DiagramLayout.Margin;
        Changed?.Invoke();
    }

    /// <summary>
    /// The tables whose name contains <paramref name="text"/>: exact names first, then names that start with it, then the
    /// others, each in name order.
    /// </summary>
    public IReadOnlyList<int> Find(string text)
    {
        var wanted = text.Trim();
        if (wanted.Length == 0) return [];
        return Enumerable.Range(0, Model.Tables.Count)
            .Select(i => (Index: i, Rank: Rank(Model.Tables[i].Ref, wanted)))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => Model.Tables[x.Index].Ref.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Index)
            .ToList();
    }

    private static int Rank(Model.TableRef table, string wanted)
    {
        const StringComparison ignore = StringComparison.OrdinalIgnoreCase;
        if (table.Name.Equals(wanted, ignore) || table.ToString().Equals(wanted, ignore)) return 0;
        if (table.Name.StartsWith(wanted, ignore)) return 1;
        return table.ToString().Contains(wanted, ignore) ? 2 : -1;
    }
}
