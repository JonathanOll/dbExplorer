using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Security;
using DbExplorer.Core.Model;
using Npgsql;

namespace DbExplorer.Core.Drivers;

public sealed partial class PostgresDriver : IDbDriver
{
    // System schemas never shown: pg_catalog, pg_toast, pg_temp_N (all start with "pg_") and information_schema.
    private const string UserSchemaFilter = "n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'";

    public DbKind Kind => DbKind.Postgres;

    public string ParamPrefix => "@";

    public string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public string QuoteTable(TableRef t) => t.Schema is null ? Quote(t.Name) : Quote(t.Schema) + "." + Quote(t.Name);

    public string CastToText(string quotedColumn) => $"{quotedColumn}::text";

    public string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public string PageSql(string selectSql, string orderBy, int offset, int limit)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset must be >= 0.");
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be >= 1.");
        var order = string.IsNullOrWhiteSpace(orderBy) ? "" : $" ORDER BY {orderBy}";
        return $"{selectSql}{order} LIMIT {limit} OFFSET {offset}";
    }

    public DbConnection CreateConnection(ConnectionConfig cfg, string? plainPassword) =>
        new NpgsqlConnection(Builder(cfg, plainPassword).ConnectionString);

    public (string Host, int Port) NetworkTarget(ConnectionConfig cfg) =>
        (string.IsNullOrWhiteSpace(cfg.Host) ? "localhost" : cfg.Host, cfg.Port ?? 5432);

    public DbConnection CreateTunneledConnection(ConnectionConfig cfg, string? plainPassword, int localPort)
    {
        // Npgsql derives the Kerberos service name from the host it dials, which here is the local end of the tunnel.
        if (cfg.Auth == AuthMode.Integrated)
            throw new NotSupportedException("PostgreSQL Windows authentication does not work through an SSH tunnel.");
        var host = NetworkTarget(cfg).Host;
        var builder = Builder(cfg, plainPassword);
        builder.Host = "127.0.0.1";
        builder.Port = localPort;
        var connection = new NpgsqlConnection(builder.ConnectionString);
        // TLS (SNI and, with certificate verification, the name check) must see the real server, not 127.0.0.1.
        connection.SslClientAuthenticationOptionsCallback = TlsTargets.GetOrAdd(host, static name => options => options.TargetHost = name);
        return connection;
    }

    // One delegate per host: every connection of a pool must be configured the same way.
    private static readonly ConcurrentDictionary<string, Action<SslClientAuthenticationOptions>> TlsTargets = new();

    private static NpgsqlConnectionStringBuilder Builder(ConnectionConfig cfg, string? plainPassword) => new()
    {
        Host = cfg.Host ?? "localhost",
        Port = cfg.Port ?? 5432,
        Database = cfg.Database,
        Username = cfg.User,
        // Integrated: no password is sent, Npgsql answers the server's SSPI / GSS request with the Windows account.
        Password = cfg.Auth == AuthMode.Integrated ? null : plainPassword,
        Timeout = 10,
    };

    /// <summary>Loads the whole schema of the connected database in four bulk queries (no per-table queries).</summary>
    public async Task<IReadOnlyList<TableInfo>> LoadSchemaAsync(DbConnection c, CancellationToken ct)
    {
        if (c.State != System.Data.ConnectionState.Open) await c.OpenAsync(ct);

        // relkind: r table, p partitioned table, f foreign table, v view, m materialized view.
        var relations = await QueryAsync(c, $"""
            SELECT n.nspname, c.relname, c.relkind IN ('v', 'm')
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p', 'f', 'v', 'm') AND {UserSchemaFilter}
            ORDER BY n.nspname, c.relname
            """,
            r => (Ref: new TableRef(r.GetString(0), r.GetString(1)), IsView: r.GetBoolean(2)), ct);

        var columns = await QueryAsync(c, $"""
            SELECT n.nspname, c.relname, a.attname, format_type(a.atttypid, a.atttypmod),
                   COALESCE(bt.typname, t.typname), NOT a.attnotnull,
                   EXISTS (SELECT 1 FROM pg_index i
                           CROSS JOIN LATERAL generate_series(0, i.indnkeyatts - 1) AS k(pos)
                           WHERE i.indrelid = c.oid AND i.indisprimary AND i.indkey[k.pos] = a.attnum),
                   row_number() OVER (PARTITION BY a.attrelid ORDER BY a.attnum) - 1
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_type t ON t.oid = a.atttypid
            LEFT JOIN pg_type bt ON bt.oid = t.typbasetype AND t.typtype = 'd'
            WHERE c.relkind IN ('r', 'p', 'f', 'v', 'm') AND a.attnum > 0 AND NOT a.attisdropped
              AND {UserSchemaFilter}
            ORDER BY n.nspname, c.relname, a.attnum
            """,
            r => (
                Table: new TableRef(r.GetString(0), r.GetString(1)),
                Column: new ColumnInfo(
                    r.GetString(2), r.GetString(3), MapClrType(r.GetString(4)),
                    Nullable: r.GetBoolean(5), IsPrimaryKey: r.GetBoolean(6), (int)r.GetInt64(7))), ct);

        // One row per (constraint, column pair); ordinality keeps multi-column keys in declaration order.
        // conparentid = 0 keeps only user-declared constraints: PG 11+ clones a FK into extra pg_constraint rows
        // (same conname, confrelid = each partition) when the referenced table is partitioned. Requires PG 11+.
        var fkRows = await QueryAsync(c, $"""
            SELECT n.nspname, cl.relname, con.conname, rn.nspname, rcl.relname, a.attname, ra.attname
            FROM pg_constraint con
            JOIN pg_class cl ON cl.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = cl.relnamespace
            JOIN pg_class rcl ON rcl.oid = con.confrelid
            JOIN pg_namespace rn ON rn.oid = rcl.relnamespace
            CROSS JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY AS k(attnum, refattnum, ord)
            JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum
            JOIN pg_attribute ra ON ra.attrelid = con.confrelid AND ra.attnum = k.refattnum
            WHERE con.contype = 'f' AND con.conparentid = 0 AND {UserSchemaFilter}
            ORDER BY n.nspname, cl.relname, con.conname, k.ord
            """,
            r => (
                Table: new TableRef(r.GetString(0), r.GetString(1)),
                Name: r.GetString(2),
                RefTable: new TableRef(r.GetString(3), r.GetString(4)),
                Column: r.GetString(5),
                RefColumn: r.GetString(6)), ct);

        // The primary key's index is skipped: it is already reported on the columns. indkey is 0-based;
        // attnum 0 marks an expression column (no attribute row, so attname is NULL and the column is skipped).
        var indexRows = await QueryAsync(c, $"""
            SELECT n.nspname, t.relname, ic.relname, i.indisunique, a.attname
            FROM pg_index i
            JOIN pg_class ic ON ic.oid = i.indexrelid
            JOIN pg_class t ON t.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            CROSS JOIN LATERAL generate_series(0, i.indnkeyatts - 1) AS k(pos)
            LEFT JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = i.indkey[k.pos]
            WHERE NOT i.indisprimary AND {UserSchemaFilter}
            ORDER BY n.nspname, t.relname, ic.relname, k.pos
            """,
            r => (
                Table: new TableRef(r.GetString(0), r.GetString(1)),
                Name: r.GetString(2),
                Unique: r.GetBoolean(3),
                Column: r.IsDBNull(4) ? null : r.GetString(4)), ct);

        var columnsByTable = columns.ToLookup(x => x.Table, x => x.Column);

        var foreignKeysByTable = fkRows
            .GroupBy(x => (x.Table, x.Name))
            .Select(g => (g.Key.Table, Fk: new ForeignKeyInfo(
                g.Key.Name,
                g.Select(x => x.Column).ToList(),
                g.First().RefTable,
                g.Select(x => x.RefColumn).ToList())))
            .ToLookup(x => x.Table, x => x.Fk);

        var indexesByTable = indexRows
            .GroupBy(x => (x.Table, x.Name))
            .Select(g => (g.Key.Table, Index: new IndexInfo(
                g.Key.Name,
                g.First().Unique,
                g.Where(x => x.Column is not null).Select(x => x.Column!).ToList())))
            .ToLookup(x => x.Table, x => x.Index);

        return relations
            .Select(rel => new TableInfo(
                rel.Ref,
                rel.IsView,
                columnsByTable[rel.Ref].ToList(),
                foreignKeysByTable[rel.Ref].ToList(),
                indexesByTable[rel.Ref].ToList()))
            .ToList();
    }

    /// <summary>
    /// Binds a DateTime with its column's exact type: <c>timestamptz</c> as UTC (Npgsql reads such values as UTC; an unspecified
    /// kind would be reinterpreted in the session time zone), <c>timestamp</c> as an unspecified kind, <c>date</c> as a date.
    /// </summary>
    public static void ConfigureParameter(DbParameter parameter, ColumnInfo column)
    {
        var p = (NpgsqlParameter)parameter;
        if (p.Value is not DateTime value) return;

        var type = column.DataType.Trim().ToLowerInvariant();
        if (type.StartsWith("timestamp", StringComparison.Ordinal))
        {
            if (type.Contains("with time zone", StringComparison.Ordinal))
            {
                p.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz;
                p.Value = value.Kind switch
                {
                    DateTimeKind.Utc => value,
                    DateTimeKind.Local => value.ToUniversalTime(),
                    _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
                };
            }
            else
            {
                p.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Timestamp;
                p.Value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
            }
        }
        else if (type == "date")
        {
            p.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date;
        }
    }

    /// <summary>Maps a PostgreSQL base type name (pg_type.typname) to the CLR type Npgsql returns for it.</summary>
    internal static Type MapClrType(string typname) => typname switch
    {
        "bool" => typeof(bool),
        "int2" => typeof(short),
        "int4" or "oid" => typeof(int),
        "int8" => typeof(long),
        "float4" => typeof(float),
        "float8" => typeof(double),
        "numeric" or "money" => typeof(decimal),
        "date" or "timestamp" or "timestamptz" => typeof(DateTime),
        "time" or "interval" => typeof(TimeSpan),
        "timetz" => typeof(DateTimeOffset),
        "uuid" => typeof(Guid),
        "bytea" => typeof(byte[]),
        _ when typname.StartsWith('_') => typeof(Array),
        _ => typeof(string),
    };

    private static async Task<List<T>> QueryAsync<T>(
        DbConnection c, string sql, Func<DbDataReader, T> map, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;   // schema load on a big or busy server: no automatic timeout

        var rows = new List<T>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(map(reader));
        return rows;
    }
}
