using System.Data.Common;
using DbExplorer.Core.Model;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Drivers;

public sealed class SqliteDriver : IDbDriver
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

        var columnsByTable = new Dictionary<string, List<(ColumnInfo Column, int PkPosition)>>();
        var rawForeignKeys = new Dictionary<string, List<RawForeignKey>>();
        var indexesByTable = new Dictionary<string, List<IndexInfo>>();

        foreach (var (name, _) in objects)
        {
            columnsByTable[name] = await LoadColumnsAsync(c, name, ct);
            rawForeignKeys[name] = await LoadForeignKeysAsync(c, name, ct);
            indexesByTable[name] = await LoadIndexesAsync(c, name, ct);
        }

        var primaryKeys = columnsByTable.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value.Where(x => x.PkPosition > 0)
                .OrderBy(x => x.PkPosition).Select(x => x.Column.Name).ToList());

        // SQLite identifiers are case-insensitive and REFERENCES keeps the name as written: use the canonical one.
        var canonicalNames = objects.ToDictionary(o => o.Name, o => o.Name, StringComparer.OrdinalIgnoreCase);

        var tables = new List<TableInfo>();
        foreach (var (name, isView) in objects)
        {
            var foreignKeys = rawForeignKeys[name]
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
                indexesByTable[name]));
        }
        return tables;
    }

    private sealed record RawForeignKey(
        long Id, IReadOnlyList<string> Columns, string RefTable, IReadOnlyList<string>? RefColumns);

    private static async Task<List<(ColumnInfo Column, int PkPosition)>> LoadColumnsAsync(
        DbConnection c, string table, CancellationToken ct)
    {
        var rows = await QueryAsync(c,
            "SELECT cid, name, type, \"notnull\", pk FROM pragma_table_info($t) ORDER BY cid",
            r => new
            {
                Ordinal = (int)r.GetInt64(0),
                Name = r.GetString(1),
                Type = r.IsDBNull(2) ? "" : r.GetString(2),
                NotNull = r.GetInt64(3) != 0,
                PkPosition = (int)r.GetInt64(4),
            }, ct, ("$t", table));

        return rows
            .Select(x => (new ColumnInfo(
                x.Name, x.Type, MapClrType(x.Type),
                Nullable: !x.NotNull && x.PkPosition == 0,
                IsPrimaryKey: x.PkPosition > 0,
                x.Ordinal), x.PkPosition))
            .ToList();
    }

    private static async Task<List<RawForeignKey>> LoadForeignKeysAsync(
        DbConnection c, string table, CancellationToken ct)
    {
        var rows = await QueryAsync(c,
            "SELECT id, seq, \"table\", \"from\", \"to\" FROM pragma_foreign_key_list($t) ORDER BY id, seq",
            r => (Id: r.GetInt64(0), RefTable: r.GetString(2), From: r.GetString(3),
                To: r.IsDBNull(4) ? null : r.GetString(4)), ct, ("$t", table));

        return rows.GroupBy(x => x.Id)
            .Select(g => new RawForeignKey(
                g.Key,
                g.Select(x => x.From).ToList(),
                g.First().RefTable,
                g.All(x => x.To is not null) ? g.Select(x => x.To!).ToList() : null))
            .ToList();
    }

    private static async Task<List<IndexInfo>> LoadIndexesAsync(DbConnection c, string table, CancellationToken ct)
    {
        // origin 'pk' is the implicit index behind the primary key: already reported on the columns.
        var indexes = await QueryAsync(c,
            "SELECT name, \"unique\" FROM pragma_index_list($t) WHERE origin <> 'pk'",
            r => (Name: r.GetString(0), Unique: r.GetInt64(1) != 0), ct, ("$t", table));

        var result = new List<IndexInfo>();
        foreach (var (name, unique) in indexes.OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            // Expression columns have a NULL name and are skipped.
            var columns = await QueryAsync(c,
                "SELECT name FROM pragma_index_info($i) WHERE name IS NOT NULL ORDER BY seqno",
                r => r.GetString(0), ct, ("$i", name));
            result.Add(new IndexInfo(name, unique, columns));
        }
        return result;
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
        DbConnection c, string sql, Func<DbDataReader, T> map, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        var rows = new List<T>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(map(reader));
        return rows;
    }
}
