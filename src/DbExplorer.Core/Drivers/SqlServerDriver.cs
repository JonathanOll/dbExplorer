using System.Data.Common;
using DbExplorer.Core.Model;
using Microsoft.Data.SqlClient;

namespace DbExplorer.Core.Drivers;

public sealed partial class SqlServerDriver : IDbDriver
{
    private const int ConnectTimeoutSeconds = 10;

    // User tables and views (never system objects), shared by the bulk schema queries.
    private const string UserObjects =
        "(SELECT object_id, schema_id, name, CAST(0 AS bit) AS is_view FROM sys.tables WHERE is_ms_shipped = 0 " +
        "UNION ALL " +
        "SELECT object_id, schema_id, name, CAST(1 AS bit) AS is_view FROM sys.views WHERE is_ms_shipped = 0)";

    public DbKind Kind => DbKind.SqlServer;

    public string ParamPrefix => "@";

    public string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    public string QuoteTable(TableRef t) => t.Schema is null ? Quote(t.Name) : Quote(t.Schema) + "." + Quote(t.Name);

    public string CastToText(string quotedColumn) => $"CAST({quotedColumn} AS NVARCHAR(MAX))";

    // "[" opens a character class in T-SQL LIKE, so it is escaped along with the usual wildcards.
    public string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    public string PageSql(string selectSql, string orderBy, int offset, int limit)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset must be >= 0.");
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be >= 1.");
        // OFFSET/FETCH is only valid after ORDER BY; "(SELECT NULL)" keeps it valid when no order is requested.
        var order = string.IsNullOrWhiteSpace(orderBy) ? "(SELECT NULL)" : orderBy;
        return $"{selectSql} ORDER BY {order} OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY";
    }

    public DbConnection CreateConnection(ConnectionConfig cfg, string? plainPassword)
    {
        var host = string.IsNullOrWhiteSpace(cfg.Host) ? "localhost" : cfg.Host;
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = cfg.Port is { } port ? $"{host},{port}" : host,
            InitialCatalog = cfg.Database,
            Encrypt = cfg.Encrypt,
            TrustServerCertificate = cfg.TrustServerCertificate,
            ConnectTimeout = ConnectTimeoutSeconds,
            ApplicationName = "dbExplorer",
        };
        if (!string.IsNullOrEmpty(cfg.User)) builder.UserID = cfg.User;
        if (plainPassword is not null) builder.Password = plainPassword;
        return new SqlConnection(builder.ToString());
    }

    public async Task<IReadOnlyList<TableInfo>> LoadSchemaAsync(DbConnection c, CancellationToken ct)
    {
        if (c.State != System.Data.ConnectionState.Open) await c.OpenAsync(ct);

        var objects = await QueryAsync(c,
            $"SELECT s.name, o.name, o.is_view FROM {UserObjects} o " +
            "JOIN sys.schemas s ON s.schema_id = o.schema_id ORDER BY s.name, o.name",
            r => (Ref: new TableRef(r.GetString(0), r.GetString(1)), IsView: r.GetBoolean(2)), ct);

        var columnRows = await QueryAsync(c,
            $"SELECT s.name, o.name, c.name, t.name, COALESCE(bt.name, t.name), c.max_length, c.precision, c.scale, c.is_nullable " +
            $"FROM {UserObjects} o " +
            "JOIN sys.schemas s ON s.schema_id = o.schema_id " +
            "JOIN sys.columns c ON c.object_id = o.object_id " +
            "JOIN sys.types t ON t.user_type_id = c.user_type_id " +
            // For alias types (e.g. sysname) t.system_type_id points at the underlying system type.
            "LEFT JOIN sys.types bt ON bt.user_type_id = t.system_type_id " +
            "ORDER BY s.name, o.name, c.column_id",
            r => (Table: new TableRef(r.GetString(0), r.GetString(1)),
                Name: r.GetString(2),
                TypeName: r.GetString(3),
                BaseTypeName: r.GetString(4),
                MaxLength: (int)r.GetInt16(5),
                Precision: (int)r.GetByte(6),
                Scale: (int)r.GetByte(7),
                Nullable: r.GetBoolean(8)), ct);

        var indexRows = await QueryAsync(c,
            "SELECT s.name, o.name, i.index_id, i.name, i.is_unique, i.is_primary_key, c.name " +
            $"FROM {UserObjects} o " +
            "JOIN sys.schemas s ON s.schema_id = o.schema_id " +
            "JOIN sys.indexes i ON i.object_id = o.object_id AND i.type > 0 AND i.is_hypothetical = 0 " +
            "JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
            "AND ic.is_included_column = 0 AND ic.key_ordinal > 0 " +
            "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
            "ORDER BY s.name, o.name, i.index_id, ic.key_ordinal",
            r => (Table: new TableRef(r.GetString(0), r.GetString(1)),
                IndexId: r.GetInt32(2),
                IndexName: r.GetString(3),
                Unique: r.GetBoolean(4),
                IsPrimaryKey: r.GetBoolean(5),
                Column: r.GetString(6)), ct);

        var foreignKeyRows = await QueryAsync(c,
            "SELECT fk.object_id, fk.name, ps.name, po.name, pc.name, rs.name, ro.name, rc.name " +
            "FROM sys.foreign_keys fk " +
            "JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id " +
            "JOIN sys.tables po ON po.object_id = fk.parent_object_id " +
            "JOIN sys.schemas ps ON ps.schema_id = po.schema_id " +
            "JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id " +
            "JOIN sys.tables ro ON ro.object_id = fk.referenced_object_id " +
            "JOIN sys.schemas rs ON rs.schema_id = ro.schema_id " +
            "JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id " +
            "WHERE po.is_ms_shipped = 0 " +
            "ORDER BY ps.name, po.name, fk.name, fkc.constraint_column_id",
            r => (FkId: r.GetInt32(0),
                Name: r.GetString(1),
                Table: new TableRef(r.GetString(2), r.GetString(3)),
                Column: r.GetString(4),
                RefTable: new TableRef(r.GetString(5), r.GetString(6)),
                RefColumn: r.GetString(7)), ct);

        var primaryKeyColumns = indexRows
            .Where(x => x.IsPrimaryKey)
            .GroupBy(x => x.Table)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Column).ToHashSet(StringComparer.Ordinal));

        var columnsByTable = columnRows.GroupBy(x => x.Table).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<ColumnInfo>)g.Select((x, ordinal) => new ColumnInfo(
                x.Name,
                FormatDataType(x.TypeName, x.MaxLength, x.Precision, x.Scale),
                MapClrType(x.BaseTypeName),
                x.Nullable,
                primaryKeyColumns.TryGetValue(g.Key, out var pk) && pk.Contains(x.Name),
                ordinal)).ToList());

        // The primary-key index is already reported on the columns, so only secondary indexes are listed.
        var indexesByTable = indexRows
            .Where(x => !x.IsPrimaryKey)
            .GroupBy(x => x.Table)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<IndexInfo>)g.GroupBy(x => x.IndexId)
                    .Select(ix => new IndexInfo(ix.First().IndexName, ix.First().Unique, ix.Select(x => x.Column).ToList()))
                    .ToList());

        var foreignKeysByTable = foreignKeyRows
            .GroupBy(x => x.Table)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ForeignKeyInfo>)g.GroupBy(x => x.FkId)
                    .Select(fk => new ForeignKeyInfo(
                        fk.First().Name,
                        fk.Select(x => x.Column).ToList(),
                        fk.First().RefTable,
                        fk.Select(x => x.RefColumn).ToList()))
                    .ToList());

        return objects.Select(o => new TableInfo(
            o.Ref,
            o.IsView,
            columnsByTable.GetValueOrDefault(o.Ref) ?? Array.Empty<ColumnInfo>(),
            foreignKeysByTable.GetValueOrDefault(o.Ref) ?? Array.Empty<ForeignKeyInfo>(),
            indexesByTable.GetValueOrDefault(o.Ref) ?? Array.Empty<IndexInfo>())).ToList();
    }

    /// <summary>
    /// Binds a date/time value with its column's exact type and scale (<c>datetime2(3)</c> -> DateTime2 with Scale 3...),
    /// instead of the legacy <c>datetime</c> SqlClient infers for any DateTime. Values of other types, and other column
    /// types, are left to the provider.
    /// </summary>
    public static void ConfigureParameter(DbParameter parameter, ColumnInfo column)
    {
        var p = (SqlParameter)parameter;
        var type = column.DataType;
        var paren = type.IndexOf('(');
        var scale = paren >= 0 && int.TryParse(type[(paren + 1)..].TrimEnd(')'), out var s) ? s : 7;
        if (paren >= 0) type = type[..paren];

        var value = p.Value;
        switch (type.Trim().ToLowerInvariant())
        {
            case "datetime2" when value is DateTime:
                p.SqlDbType = System.Data.SqlDbType.DateTime2;
                p.Scale = (byte)scale;
                break;
            case "datetime" when value is DateTime:
                p.SqlDbType = System.Data.SqlDbType.DateTime;
                break;
            case "smalldatetime" when value is DateTime:
                p.SqlDbType = System.Data.SqlDbType.SmallDateTime;
                break;
            case "date" when value is DateTime:
                p.SqlDbType = System.Data.SqlDbType.Date;
                break;
            case "time" when value is TimeSpan:
                p.SqlDbType = System.Data.SqlDbType.Time;
                p.Scale = (byte)scale;
                break;
            case "datetimeoffset" when value is DateTimeOffset:
                p.SqlDbType = System.Data.SqlDbType.DateTimeOffset;
                p.Scale = (byte)scale;
                break;
        }
    }

    /// <summary>Maps a SQL Server system type name (as in sys.types) to a CLR type.</summary>
    public static Type MapClrType(string sqlTypeName) => sqlTypeName.ToLowerInvariant() switch
    {
        "uniqueidentifier" => typeof(Guid),
        "bigint" => typeof(long),
        "int" => typeof(int),
        "smallint" => typeof(short),
        "tinyint" => typeof(byte),
        "bit" => typeof(bool),
        "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal),
        "float" => typeof(double),
        "real" => typeof(float),
        "date" or "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime),
        "datetimeoffset" => typeof(DateTimeOffset),
        "time" => typeof(TimeSpan),
        "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => typeof(byte[]),
        "char" or "varchar" or "text" or "nchar" or "nvarchar" or "ntext" or "xml" or "sysname" => typeof(string),
        _ => typeof(object),
    };

    /// <summary>Renders a type name with its length, precision or scale, as written in a column definition.</summary>
    public static string FormatDataType(string typeName, int maxLength, int precision, int scale) => typeName switch
    {
        "char" or "varchar" or "binary" or "varbinary" => $"{typeName}({(maxLength == -1 ? "max" : maxLength.ToString())})",
        // sys.columns.max_length counts bytes: two per character for the national types.
        "nchar" or "nvarchar" => $"{typeName}({(maxLength == -1 ? "max" : (maxLength / 2).ToString())})",
        "decimal" or "numeric" => $"{typeName}({precision},{scale})",
        "datetime2" or "time" or "datetimeoffset" => $"{typeName}({scale})",
        _ => typeName,
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
