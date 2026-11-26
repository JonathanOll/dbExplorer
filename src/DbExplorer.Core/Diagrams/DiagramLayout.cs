namespace DbExplorer.Core.Diagrams;

/// <summary>Where every table of a diagram goes. <see cref="Tables"/> has the order of the model's tables.</summary>
public sealed record DiagramPlacement(IReadOnlyList<DiagramRect> Tables, double Width, double Height);

/// <summary>
/// A layered layout: referenced tables on the left, the tables that reference them to their right. Each group of linked
/// tables is laid out on its own (cycles are broken, self references ignored, tall layers wrapped into several columns,
/// tables ordered to shorten the lines); the groups are then packed in shelves, unlinked tables last in a compact grid.
/// Deterministic and cancellable.
/// </summary>
public static class DiagramLayout
{
    public const double Margin = 40;
    private const double ColumnGap = 90;
    private const double SubColumnGap = 40;
    private const double RowGap = 24;
    private const double GroupGap = 70;
    private const double MinLayerHeight = 900;
    private const int OrderSweeps = 6;

    public static DiagramPlacement Arrange(
        DiagramModel model, DiagramColumnMode mode, bool showSchema = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var count = model.Tables.Count;
        var sizes = model.Tables.Select(t => DiagramMetrics.Size(t, mode, showSchema)).ToArray();
        var rects = new DiagramRect[count];
        if (count == 0) return new DiagramPlacement(rects, 0, 0);

        // One edge per linked pair, parent to child.
        var edges = model.Relations.Where(r => !r.IsSelf).Select(r => (From: r.Parent, To: r.Child)).Distinct().ToList();
        var neighbours = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        var outgoing = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        foreach (var (from, to) in edges)
        {
            neighbours[from].Add(to);
            neighbours[to].Add(from);
            outgoing[from].Add(to);
        }

        var blocks = new List<Block>();
        var lonely = new List<int>();
        var seen = new bool[count];
        for (var i = 0; i < count; i++)
        {
            if (seen[i]) continue;
            seen[i] = true;
            if (neighbours[i].Count == 0)
            {
                lonely.Add(i);
                continue;
            }
            var group = new List<int>();
            var queue = new Queue<int>([i]);
            while (queue.Count > 0)
            {
                var n = queue.Dequeue();
                group.Add(n);
                foreach (var m in neighbours[n])
                    if (!seen[m]) { seen[m] = true; queue.Enqueue(m); }
            }
            group.Sort();
            ct.ThrowIfCancellationRequested();
            blocks.Add(Layered(group, outgoing, sizes, ct));
        }

        blocks = blocks.OrderByDescending(b => b.Places.Count).ThenByDescending(b => b.Width * b.Height).ToList();
        var area = blocks.Sum(b => (b.Width + GroupGap) * (b.Height + GroupGap))
                   + lonely.Sum(i => (sizes[i].Width + SubColumnGap) * (sizes[i].Height + RowGap));
        var shelfWidth = Math.Max(blocks.Count > 0 ? blocks.Max(b => b.Width) : 0, Math.Sqrt(area * 1.7));
        if (lonely.Count > 0) blocks.Add(Grid(lonely, sizes, shelfWidth));

        double x = Margin, y = Margin, shelf = 0, width = 0;
        foreach (var block in blocks)
        {
            if (x > Margin && x + block.Width > Margin + shelfWidth)
            {
                x = Margin;
                y += shelf + GroupGap;
                shelf = 0;
            }
            foreach (var (node, left, top) in block.Places)
                rects[node] = new DiagramRect(Math.Round(x + left), Math.Round(y + top), sizes[node].Width, sizes[node].Height);
            width = Math.Max(width, x + block.Width);
            x += block.Width + GroupGap;
            shelf = Math.Max(shelf, block.Height);
        }
        return new DiagramPlacement(rects, Math.Ceiling(width + Margin), Math.Ceiling(y + shelf + Margin));
    }

    private sealed record Block(List<(int Node, double X, double Y)> Places, double Width, double Height);

    // The unlinked tables: rows of cards, left to right, in the model's order (by name).
    private static Block Grid(List<int> nodes, (double Width, double Height)[] sizes, double width)
    {
        var places = new List<(int, double, double)>();
        double x = 0, y = 0, row = 0, used = 0;
        foreach (var n in nodes)
        {
            if (x > 0 && x + sizes[n].Width > width)
            {
                x = 0;
                y += row + RowGap;
                row = 0;
            }
            places.Add((n, x, y));
            used = Math.Max(used, x + sizes[n].Width);
            x += sizes[n].Width + SubColumnGap;
            row = Math.Max(row, sizes[n].Height);
        }
        return new Block(places, used, y + row);
    }

    private static Block Layered(List<int> group, List<int>[] outgoing, (double Width, double Height)[] sizes, CancellationToken ct)
    {
        var acyclic = BreakCycles(group, outgoing);
        var layer = Layers(group, acyclic);

        var layers = group.GroupBy(n => layer[n]).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
        var near = group.ToDictionary(n => n, _ => new List<int>());
        foreach (var n in group)
            foreach (var m in acyclic[n])
            {
                near[n].Add(m);
                near[m].Add(n);
            }
        Order(layers, near, layer, ct);

        // A layer much taller than the block would be wide becomes several columns side by side.
        var total = group.Sum(n => sizes[n].Height + RowGap);
        var averageWidth = group.Average(n => sizes[n].Width) + SubColumnGap;
        var limit = Math.Max(MinLayerHeight, Math.Sqrt(total * averageWidth / 1.6));

        var places = new Dictionary<int, (double X, double Y)>();
        var columns = new List<List<int>>();
        double x = 0;
        foreach (var whole in layers)
        {
            ct.ThrowIfCancellationRequested();
            var parts = new List<List<int>> { new() };
            double height = 0;
            foreach (var n in whole)
            {
                if (parts[^1].Count > 0 && height + sizes[n].Height > limit)
                {
                    parts.Add([]);
                    height = 0;
                }
                parts[^1].Add(n);
                height += sizes[n].Height + RowGap;
            }
            for (var p = 0; p < parts.Count; p++)
            {
                double y = 0;
                foreach (var n in parts[p])
                {
                    places[n] = (x, y);
                    y += sizes[n].Height + RowGap;
                }
                columns.Add(parts[p]);
                x += parts[p].Max(n => sizes[n].Width) + (p == parts.Count - 1 ? ColumnGap : SubColumnGap);
            }
        }

        Align(columns, near, places, sizes, ct);

        var top = places.Values.Min(p => p.Y);
        var list = group.Select(n => (Node: n, places[n].X, Y: places[n].Y - top)).ToList();
        return new Block(list, list.Max(p => p.X + sizes[p.Node].Width), list.Max(p => p.Y + sizes[p.Node].Height));
    }

    // Depth-first walk; an edge back to a table still on the stack is reversed. Iterative: schemas can be long chains.
    private static Dictionary<int, HashSet<int>> BreakCycles(List<int> group, List<int>[] outgoing)
    {
        var result = group.ToDictionary(n => n, _ => new HashSet<int>());
        var state = group.ToDictionary(n => n, _ => 0);   // 0 new, 1 on the stack, 2 done
        var referencing = group.SelectMany(n => outgoing[n]).ToHashSet();
        // Tables that reference nothing first: they are the natural left edge.
        foreach (var root in group.OrderBy(n => referencing.Contains(n)))
        {
            if (state[root] != 0) continue;
            var stack = new Stack<(int Node, int Next)>();
            stack.Push((root, 0));
            state[root] = 1;
            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                if (next >= outgoing[node].Count)
                {
                    state[node] = 2;
                    continue;
                }
                stack.Push((node, next + 1));
                var to = outgoing[node][next];
                if (state[to] == 1)
                {
                    result[to].Add(node);
                    continue;
                }
                result[node].Add(to);
                if (state[to] == 0)
                {
                    state[to] = 1;
                    stack.Push((to, 0));
                }
            }
        }
        return result;
    }

    // Longest path from the sources; then each source moves next to its first child, so its line stays short.
    private static Dictionary<int, int> Layers(List<int> group, Dictionary<int, HashSet<int>> acyclic)
    {
        var layer = group.ToDictionary(n => n, _ => 0);
        var waiting = group.ToDictionary(n => n, _ => 0);
        foreach (var to in acyclic.Values.SelectMany(v => v)) waiting[to]++;
        var sources = group.Where(n => waiting[n] == 0).ToList();
        var queue = new Queue<int>(sources);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            foreach (var m in acyclic[n])
            {
                layer[m] = Math.Max(layer[m], layer[n] + 1);
                if (--waiting[m] == 0) queue.Enqueue(m);
            }
        }
        foreach (var n in sources)
            if (acyclic[n].Count > 0) layer[n] = acyclic[n].Min(m => layer[m]) - 1;
        return layer;
    }

    // Barycentre sweeps: each table moves to the average position of the tables it links to in the layers already ordered.
    private static void Order(List<List<int>> layers, Dictionary<int, List<int>> near, Dictionary<int, int> layer, CancellationToken ct)
    {
        var position = new Dictionary<int, double>();
        void Number(List<int> l)
        {
            for (var i = 0; i < l.Count; i++) position[l[i]] = (i + 0.5) / l.Count;
        }
        foreach (var l in layers) Number(l);
        for (var sweep = 0; sweep < OrderSweeps; sweep++)
        {
            ct.ThrowIfCancellationRequested();
            var down = sweep % 2 == 0;
            for (var step = 1; step < layers.Count; step++)
            {
                var current = layers[down ? step : layers.Count - 1 - step];
                var keys = current.ToDictionary(n => n, n =>
                {
                    var side = near[n].Where(m => down ? layer[m] < layer[n] : layer[m] > layer[n]).ToList();
                    return side.Count == 0 ? position[n] : side.Average(m => position[m]);
                });
                var sorted = current.OrderBy(n => keys[n]).ThenBy(n => n).ToList();
                current.Clear();
                current.AddRange(sorted);
                Number(current);
            }
        }
    }

    // Slides each table towards the tables it links to, keeping the order of its column and the gap between cards.
    private static void Align(
        List<List<int>> columns, Dictionary<int, List<int>> near, Dictionary<int, (double X, double Y)> places,
        (double Width, double Height)[] sizes, CancellationToken ct)
    {
        double Centre(int n) => places[n].Y + sizes[n].Height / 2;
        for (var pass = 0; pass < 4; pass++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var column in pass % 2 == 0 ? columns : Enumerable.Reverse(columns))
            {
                var wanted = column
                    .Select(n => near[n].Count == 0 ? places[n].Y : near[n].Average(Centre) - sizes[n].Height / 2).ToArray();
                // Pushed down where they would overlap, then the whole column is shifted back by the average push.
                var y = new double[column.Count];
                for (var i = 0; i < column.Count; i++)
                    y[i] = i == 0 ? wanted[0] : Math.Max(wanted[i], y[i - 1] + sizes[column[i - 1]].Height + RowGap);
                var shift = y.Zip(wanted, (a, b) => a - b).Average();
                for (var i = 0; i < column.Count; i++) places[column[i]] = (places[column[i]].X, y[i] - shift);
            }
        }
    }
}
