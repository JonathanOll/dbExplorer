using DbExplorer.Core.Model;

namespace DbExplorer.Core.Querying;

public static class KeyResolver
{
    /// <summary>
    /// Columns identifying a row of <paramref name="t"/> for UPDATE/DELETE: the primary key, else the virtual key
    /// saved for the table (<c>["*"]</c> = all columns), else <c>null</c>, meaning the table is read-only.
    /// Views and read-only connections always return <c>null</c>.
    /// </summary>
    public static IReadOnlyList<string>? Resolve(TableInfo t, ConnectionConfig c, AppConfig cfg)
    {
        if (c.ReadOnly || t.IsView) return null;

        var pk = t.Columns.Where(col => col.IsPrimaryKey).OrderBy(col => col.Ordinal).Select(col => col.Name).ToList();
        if (pk.Count > 0) return pk;

        if (!cfg.VirtualKeys.TryGetValue(c.Id, out var perTable)
            || !perTable.TryGetValue(t.Ref.ToString(), out var configured)
            || configured.Length == 0)
            return null;

        if (configured.Contains("*"))
        {
            // Only columns the engine can compare with "=": an imported (DBeaver) "*" key must not make every save fail on
            // a SQL Server text/ntext/image/xml column. Nothing usable left = read-only.
            var usable = t.Columns.OrderBy(col => col.Ordinal).Where(col => IsKeyable(col, c.Kind)).Select(col => col.Name).ToList();
            return usable.Count == 0 ? null : usable;
        }

        // Canonical column names; a saved column that no longer exists makes the table read-only rather than
        // producing a WHERE on a different set of columns than the user chose.
        var resolved = new List<string>();
        foreach (var name in configured)
        {
            var column = t.Columns.FirstOrDefault(col => col.Name == name)
                ?? t.Columns.FirstOrDefault(col => string.Equals(col.Name, name, StringComparison.OrdinalIgnoreCase));
            if (column is null) return null;
            if (!resolved.Contains(column.Name)) resolved.Add(column.Name);
        }
        return resolved;
    }

    /// <summary>Types the engine has no usable equality for: they would make every UPDATE/DELETE by key fail.</summary>
    public static bool IsKeyable(ColumnInfo column, DbKind kind)
    {
        var type = column.DataType;
        var paren = type.IndexOf('(');
        if (paren >= 0) type = type[..paren];
        type = type.Trim().ToLowerInvariant();
        if (kind == DbKind.MySql) type = type.Split(' ')[0];   // "float unsigned"
        return kind switch
        {
            DbKind.SqlServer => type is not ("text" or "ntext" or "image" or "xml" or "geography" or "geometry"),
            DbKind.Postgres => type is not ("json" or "xml" or "point" or "line" or "lseg" or "box" or "path" or "polygon" or "circle"),
            // JSON and spatial values do not compare with a bound parameter, and "=" almost never matches a FLOAT.
            DbKind.MySql => type is not ("float" or "json" or "geometry" or "point" or "linestring" or "polygon" or "multipoint"
                or "multilinestring" or "multipolygon" or "geometrycollection" or "geomcollection"),
            _ => true,
        };
    }
}
