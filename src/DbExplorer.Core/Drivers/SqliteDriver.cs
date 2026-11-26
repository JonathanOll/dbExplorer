using System.Data.Common;
using DbExplorer.Core.Model;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Drivers;

public sealed partial class SqliteDriver : IDbDriver
{
    public DbKind Kind => DbKind.Sqlite;

    public string ParamPrefix => "@";

    public string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public string QuoteTable(TableRef t) => t.Schema is null ? Quote(t.Name) : Quote(t.Schema) + "." + Quote(t.Name);

    public string CastToText(string quotedColumn) => $"CAST({quotedColumn} AS TEXT)";

    public string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public string PageSql(string selectSql, string orderBy, int offset, int limit)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset must be >= 0.");
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be >= 1.");
        var order = string.IsNullOrWhiteSpace(orderBy) ? "" : $" ORDER BY {orderBy}";
        return $"{selectSql}{order} LIMIT {limit} OFFSET {offset}";
    }

    public DbConnection CreateConnection(ConnectionConfig cfg, string? plainPassword)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = cfg.Database,
            // ReadWrite (not ReadWriteCreate): a mistyped path must fail rather than create an empty database. A read-only
            // connection is opened read-only: SQLite itself then refuses every write (not pooled, so a toggle of the flag
            // takes effect on the next connection that is opened).
            Mode = cfg.ReadOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            // No pooling: a closed connection must release the file handle.
            Pooling = false,
        };
        return new SqliteConnection(builder.ToString());
    }

    public async Task<IReadOnlyList<TableInfo>> LoadSchemaAsync(DbConnection c, CancellationToken ct)
    {
        if (c.State != System.Data.ConnectionState.Open) await c.OpenAsync(ct);

        var objects = await QueryAsync(c,
            "SELECT name, type FROM sqlite_master WHERE type IN ('table', 'view') " +
            "AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY name",
            r => (Name: r.GetString(0), IsView: r.GetString(1) == "view"), ct);

        // One query per kind of metadata for the whole file (table-valued pragmas), not three per table.
        var columnsByTable = await LoadColumnsAsync(c, ct);
        var rawForeignKeys = await LoadForeignKeysAsync(c, ct);
        var indexesByTable = await LoadIndexesAsync(c, ct);

        var primaryKeys = objects.ToDictionary(
            o => o.Name,
            o => (IReadOnlyList<string>)columnsByTable[o.Name].Where(x => x.PkPosition > 0)
                .OrderBy(x => x.PkPosition).Select(x => x.Column.Name).ToList());

        // SQLite identifiers are case-insensitive and REFERENCES keeps the name as written: use the canonical one.
        var canonicalNames = objects.ToDictionary(o => o.Name, o => o.Name, StringComparer.OrdinalIgnoreCase);

        var tables = new List<TableInfo>();
        foreach (var (name, isView) in objects)
        {
            var foreignKeys = rawForeignKeys.GetValueOrDefault(name, [])
                .Select(fk =>
                {
                    var refName = canonicalNames.GetValueOrDefault(fk.RefTable, fk.RefTable);
                    return new ForeignKeyInfo(
                        $"fk_{name}_{fk.Id}",
                        fk.Columns,
                        new TableRef(null, refName),
                        // `to` is NULL when the FK targets the parent's primary key implicitly.
                        fk.RefColumns ?? (primaryKeys.TryGetValue(refName, out var pk) ? pk : Array.Empty<string>()));
                })
                .ToList();

            tables.Add(new TableInfo(
                new TableRef(null, name),
                isView,
                columnsByTable[name].Select(x => x.Column).ToList(),
                foreignKeys,
                indexesByTable.GetValueOrDefault(name, [])));
        }
        return tables;
    }

    private sealed record RawForeignKey(
        long Id, IReadOnlyList<string> Columns, string RefTable, IReadOnlyList<string>? RefColumns);

    // Every user table and view, joined to a table-valued pragma.
    private static string PerObject(string select, string joins, string and, string orderBy) =>
        $"SELECT m.name, {select} FROM sqlite_master m {joins} WHERE m.type IN ('table', 'view') " +
        $"AND m.name NOT LIKE 'sqlite\\_%' ESCAPE '\\' {and} ORDER BY m.name, {orderBy}";

    private static async Task<ILookup<string, (ColumnInfo Column, int PkPosition)>> LoadColumnsAsync(
        DbConnection c, CancellationToken ct)
    {
        var rows = await QueryAsync(c,
            PerObject("p.cid, p.name, p.type, p.\"notnull\", p.pk", "JOIN pragma_table_info(m.name) p", "", "p.cid"),
            r => new
            {
                Table = r.GetString(0),
                Ordinal = (int)r.GetInt64(1),
                Name = r.GetString(2),
                Type = r.IsDBNull(3) ? "" : r.GetString(3),
                NotNull = r.GetInt64(4) != 0,
                PkPosition = (int)r.GetInt64(5),
            }, ct);

        return rows.ToLookup(x => x.Table, x => (Column: new ColumnInfo(
            x.Name, x.Type, MapClrType(x.Type),
            Nullable: !x.NotNull && x.PkPosition == 0,
            IsPrimaryKey: x.PkPosition > 0,
            x.Ordinal), x.PkPosition));
    }

    private static async Task<Dictionary<string, List<RawForeignKey>>> LoadForeignKeysAsync(
        DbConnection c, CancellationToken ct)
    {
        var rows = await QueryAsync(c,
            PerObject("f.id, f.\"table\", f.\"from\", f.\"to\"", "JOIN pragma_foreign_key_list(m.name) f",
                "AND m.type = 'table'", "f.id, f.seq"),
            r => (Table: r.GetString(0), Id: r.GetInt64(1), RefTable: r.GetString(2), From: r.GetString(3),
                To: r.IsDBNull(4) ? null : r.GetString(4)), ct);

        return rows.GroupBy(x => x.Table).ToDictionary(
            t => t.Key,
            t => t.GroupBy(x => x.Id)
                .Select(g => new RawForeignKey(
                    g.Key,
                    g.Select(x => x.From).ToList(),
                    g.First().RefTable,
                    g.All(x => x.To is not null) ? g.Select(x => x.To!).ToList() : null))
                .ToList());
    }

    private static async Task<Dictionary<string, List<IndexInfo>>> LoadIndexesAsync(DbConnection c, CancellationToken ct)
    {
        // origin 'pk' is the implicit index behind the primary key: already reported on the columns.
        var rows = await QueryAsync(c,
            PerObject("l.name, l.\"unique\", i.name",
                "JOIN pragma_index_list(m.name) l LEFT JOIN pragma_index_info(l.name) i",
                "AND l.origin <> 'pk'", "l.name, i.seqno"),
            r => (Table: r.GetString(0), Name: r.GetString(1), Unique: r.GetInt64(2) != 0,
                Column: r.IsDBNull(3) ? null : r.GetString(3)), ct);

        return rows.GroupBy(x => x.Table).ToDictionary(
            t => t.Key,
            // Expression columns have a NULL name and are skipped.
            t => t.GroupBy(x => x.Name).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new IndexInfo(g.Key, g.First().Unique,
                    g.Where(x => x.Column is not null).Select(x => x.Column!).ToList()))
                .ToList());
    }

    /// <summary>Maps a declared SQLite type to a CLR type, following SQLite's type-affinity name rules.</summary>
    internal static Type MapClrType(string declaredType)
    {
        var t = declaredType.ToUpperInvariant();
        if (t.Contains("BOOL")) return typeof(bool);
        if (t.Contains("INT")) return typeof(long);
        if (t.Contains("DATE") || t.Contains("TIME")) return typeof(DateTime);
        if (t.Contains("CHAR") || t.Contains("CLOB") || t.Contains("TEXT")) return typeof(string);
        if (t.Contains("BLOB")) return typeof(byte[]);
        if (t.Contains("REAL") || t.Contains("FLOA") || t.Contains("DOUB")) return typeof(double);
        if (t.Contains("DEC") || t.Contains("NUM")) return typeof(decimal);
        return typeof(string);
    }

    private static async Task<List<T>> QueryAsync<T>(
        DbConnection c, string sql, Func<DbDataReader, T> map, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;   // schema load of a big file: no automatic timeout

        var rows = new List<T>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();   // SQLite runs synchronously
            rows.Add(map(reader));
        }
        return rows;
    }
}
