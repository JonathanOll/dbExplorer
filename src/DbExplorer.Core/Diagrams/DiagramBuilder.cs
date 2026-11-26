using DbExplorer.Core.Model;

namespace DbExplorer.Core.Diagrams;

/// <summary>What a diagram shows.</summary>
/// <param name="Schemas">The schemas shown; null for all of them.</param>
/// <param name="Focus">When set, only this table and its neighbours.</param>
/// <param name="Depth">How many foreign keys away from <paramref name="Focus"/>, followed in both directions.</param>
public sealed record DiagramScope(IReadOnlySet<string>? Schemas = null, TableRef? Focus = null, int Depth = 1);

/// <summary>Turns loaded table metadata into a diagram. Views are left out: they declare no key.</summary>
public static class DiagramBuilder
{
    /// <summary>The schema names that hold at least one table ("" for engines without schemas).</summary>
    public static IReadOnlyList<string> Schemas(IEnumerable<TableInfo> tables) =>
        tables.Where(t => !t.IsView).Select(t => t.Ref.Schema ?? "").Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

    public static DiagramModel Build(IReadOnlyList<TableInfo> schema, DiagramScope scope, CancellationToken ct = default)
    {
        var all = schema.Where(t => !t.IsView).ToList();
        var find = Finder(all);

        List<TableInfo> shown;
        if (scope.Focus is { } focus)
        {
            // The neighbourhood ignores the schema filter: a neighbour in another schema is still a neighbour.
            shown = find(focus) is { } start ? Neighbourhood(all, find, start, scope.Depth, ct) : [];
        }
        else
        {
            shown = scope.Schemas is { } wanted ? all.Where(t => wanted.Contains(t.Ref.Schema ?? "")).ToList() : all;
        }

        var index = new Dictionary<TableRef, int>();
        for (var i = 0; i < shown.Count; i++) index[shown[i].Ref] = i;

        var tables = new List<DiagramTable>(shown.Count);
        var relations = new List<DiagramRelation>();
        for (var i = 0; i < shown.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = shown[i];
            var keyColumns = new HashSet<string>(t.ForeignKeys.SelectMany(f => f.Columns), StringComparer.OrdinalIgnoreCase);
            tables.Add(new DiagramTable(t.Ref, t.Columns
                .Select(c => new DiagramColumn(c.Name, c.DataType, c.Nullable, c.IsPrimaryKey, keyColumns.Contains(c.Name)))
                .ToList()));

            foreach (var fk in t.ForeignKeys)
            {
                if (find(fk.RefTable) is not { } parent || !index.TryGetValue(parent.Ref, out var p)) continue;
                var columns = new HashSet<string>(fk.Columns, StringComparer.OrdinalIgnoreCase);
                var optional = t.Columns.Any(c => c.Nullable && columns.Contains(c.Name));
                var primaryKey = t.Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
                var unique = (primaryKey.Count > 0 && columns.SetEquals(primaryKey))
                             || t.Indexes.Any(x => x.Unique && x.Columns.Count > 0 && columns.SetEquals(x.Columns));
                relations.Add(new DiagramRelation(fk.Name, i, p, fk.Columns, fk.RefColumns, optional, unique));
            }
        }
        return new DiagramModel(tables, relations);
    }

    // Exact name first; then ignoring case when that points at one table only (SQLite names are case-insensitive).
    private static Func<TableRef, TableInfo?> Finder(IReadOnlyList<TableInfo> tables)
    {
        var exact = new Dictionary<TableRef, TableInfo>();
        foreach (var t in tables) exact[t.Ref] = t;
        var loose = tables.ToLookup(t => t.Ref.ToString(), StringComparer.OrdinalIgnoreCase);
        return name =>
        {
            if (exact.TryGetValue(name, out var hit)) return hit;
            var near = loose[name.ToString()].Take(2).ToList();
            return near.Count == 1 ? near[0] : null;
        };
    }

    private static List<TableInfo> Neighbourhood(
        IReadOnlyList<TableInfo> all, Func<TableRef, TableInfo?> find, TableInfo start, int depth, CancellationToken ct)
    {
        var links = new Dictionary<TableRef, List<TableRef>>();
        foreach (var t in all) links[t.Ref] = [];
        foreach (var t in all)
            foreach (var fk in t.ForeignKeys)
                if (find(fk.RefTable) is { } parent)
                {
                    links[t.Ref].Add(parent.Ref);
                    links[parent.Ref].Add(t.Ref);
                }

        var seen = new HashSet<TableRef> { start.Ref };
        var ring = new List<TableRef> { start.Ref };
        for (var d = 0; d < depth && ring.Count > 0; d++)
        {
            ct.ThrowIfCancellationRequested();
            ring = ring.SelectMany(r => links[r]).Where(seen.Add).ToList();
        }
        return all.Where(t => seen.Contains(t.Ref)).ToList();
    }
}
