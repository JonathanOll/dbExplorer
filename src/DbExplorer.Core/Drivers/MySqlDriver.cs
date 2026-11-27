using System.Data.Common;
using System.Text;
using DbExplorer.Core.Config;
using DbExplorer.Core.Model;
using MySqlConnector;

namespace DbExplorer.Core.Drivers;

/// <summary>MySQL and MariaDB (MySqlConnector). A connection is one database, so its tables carry no schema name.</summary>
/// <param name="secrets">Decrypts the client certificate passphrase; only connections that have one need it.</param>
public sealed partial class MySqlDriver(ISecretProtector? secrets = null) : IDbDriver
{
    public const int DefaultPort = 3306;

    public DbKind Kind => DbKind.MySql;

    public IDbDriver WithSecrets(ISecretProtector protector) => new MySqlDriver(protector);

    public string ParamPrefix => "@";

    public string Quote(string identifier) => "`" + identifier.Replace("`", "``") + "`";

    public string QuoteTable(TableRef t) => t.Schema is null ? Quote(t.Name) : Quote(t.Schema) + "." + Quote(t.Name);

    public string CastToText(string quotedColumn) => $"CAST({quotedColumn} AS CHAR)";

    // Not the backslash: how a literal backslash is read depends on the server's sql_mode (NO_BACKSLASH_ESCAPES).
    public string EscapeLike(string text) => text.Replace("|", "||").Replace("%", "|%").Replace("_", "|_");

    public string LikeEscapeClause => "ESCAPE '|'";

    public string InsertDefaultsSql(TableRef t) => $"INSERT INTO {QuoteTable(t)} () VALUES ()";

    public string PageSql(string selectSql, string orderBy, int offset, int limit)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset must be >= 0.");
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be >= 1.");
        var order = string.IsNullOrWhiteSpace(orderBy) ? "" : $" ORDER BY {orderBy}";
        return $"{selectSql}{order} LIMIT {limit} OFFSET {offset}";
    }

    public DbConnection CreateConnection(ConnectionConfig cfg, string? plainPassword) => Connect(cfg, Builder(cfg, plainPassword));

    public (string Host, int Port) NetworkTarget(ConnectionConfig cfg) =>
        (string.IsNullOrWhiteSpace(cfg.Host) ? "localhost" : cfg.Host, cfg.Port ?? DefaultPort);

    public DbConnection CreateTunneledConnection(ConnectionConfig cfg, string? plainPassword, int localPort)
    {
        var builder = Builder(cfg, plainPassword);
        builder.Server = "127.0.0.1";
        builder.Port = (uint)localPort;
        return Connect(cfg, builder, tunneledHost: NetworkTarget(cfg).Host);
    }

    private MySqlConnectionStringBuilder Builder(ConnectionConfig cfg, string? plainPassword)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = string.IsNullOrWhiteSpace(cfg.Host) ? "localhost" : cfg.Host,
            Port = (uint)(cfg.Port ?? DefaultPort),
            Database = cfg.Database,
            UserID = cfg.User ?? "",
            // Integrated: no password is sent, the server's GSSAPI request is answered with the Windows account.
            Password = cfg.Auth == AuthMode.Integrated ? "" : plainPassword ?? "",
            ConnectionTimeout = 10,
            // "@name" in a user script is a server variable, not a missing parameter.
            AllowUserVariables = true,
            // A script must never make the client send local files (LOAD DATA LOCAL).
            AllowLoadLocalInfile = false,
            // CHAR(36) holds any text; read as a Guid it would fail on the first value that is not one.
            GuidFormat = MySqlGuidFormat.None,
            // 0000-00-00 reads as 0001-01-01 instead of failing the whole page.
            ConvertZeroDateTime = true,
        };
        if (cfg.Tls is { } tls) ApplyTls(builder, tls);
        return builder;
    }

    /// <summary>Loads the whole schema of the connected database in four bulk queries (no per-table queries).</summary>
    public async Task<IReadOnlyList<TableInfo>> LoadSchemaAsync(DbConnection c, CancellationToken ct)
    {
        // MariaDB reports system-versioned tables with their own type; sequences are not tables.
        var relations = await DriverQueries.ReadAsync(c,
            "SELECT TABLE_NAME, TABLE_TYPE FROM information_schema.TABLES " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE IN ('BASE TABLE', 'VIEW', 'SYSTEM VERSIONED') ORDER BY TABLE_NAME",
            r => (Name: Text(r, 0)!, IsView: Text(r, 1) == "VIEW"), ct);

        var columns = await DriverQueries.ReadAsync(c,
            "SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, DATA_TYPE, IS_NULLABLE, ORDINAL_POSITION " +
            "FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME, ORDINAL_POSITION",
            r => (Table: Text(r, 0)!, Name: Text(r, 1)!, Type: Text(r, 2)!, Base: Text(r, 3)!,
                Nullable: Text(r, 4) == "YES", Ordinal: Convert.ToInt32(r.GetValue(5)) - 1), ct);

        // The referenced database is reported only when it is another one (cross-database foreign key).
        var fkRows = await DriverQueries.ReadAsync(c,
            "SELECT TABLE_NAME, CONSTRAINT_NAME, " +
            "CASE WHEN REFERENCED_TABLE_SCHEMA = DATABASE() THEN NULL ELSE REFERENCED_TABLE_SCHEMA END, " +
            "REFERENCED_TABLE_NAME, COLUMN_NAME, REFERENCED_COLUMN_NAME " +
            "FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA = DATABASE() AND REFERENCED_TABLE_NAME IS NOT NULL " +
            "ORDER BY TABLE_NAME, CONSTRAINT_NAME, ORDINAL_POSITION",
            r => (Table: Text(r, 0)!, Name: Text(r, 1)!, RefTable: new TableRef(Text(r, 2), Text(r, 3)!),
                Column: Text(r, 4)!, RefColumn: Text(r, 5)!), ct);

        // The primary key is read here too: COLUMN_KEY says PRI for a unique index when a table has no primary key.
        // COLUMN_NAME is null for a functional key part.
        var indexRows = await DriverQueries.ReadAsync(c,
            "SELECT TABLE_NAME, INDEX_NAME, NON_UNIQUE, COLUMN_NAME FROM information_schema.STATISTICS " +
            "WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX",
            r => (Table: Text(r, 0)!, Name: Text(r, 1)!, Unique: Convert.ToInt64(r.GetValue(2)) == 0, Column: Text(r, 3)), ct);

        var primary = indexRows.Where(i => i.Name == "PRIMARY" && i.Column is not null)
            .Select(i => (i.Table, i.Column!)).ToHashSet();
        var columnsByTable = columns.ToLookup(x => x.Table, x => new ColumnInfo(
            x.Name, x.Type, MapClrType(x.Base, x.Type), x.Nullable, primary.Contains((x.Table, x.Name)), x.Ordinal), StringComparer.Ordinal);

        var foreignKeysByTable = fkRows
            .GroupBy(x => (x.Table, x.Name))
            .Select(g => (g.Key.Table, Fk: new ForeignKeyInfo(
                g.Key.Name, g.Select(x => x.Column).ToList(), g.First().RefTable, g.Select(x => x.RefColumn).ToList())))
            .ToLookup(x => x.Table, x => x.Fk, StringComparer.Ordinal);

        var indexesByTable = indexRows
            .Where(i => i.Name != "PRIMARY")
            .GroupBy(x => (x.Table, x.Name))
            .Select(g => (g.Key.Table, Index: new IndexInfo(
                g.Key.Name, g.First().Unique, g.Where(x => x.Column is not null).Select(x => x.Column!).ToList())))
            .ToLookup(x => x.Table, x => x.Index, StringComparer.Ordinal);

        return relations
            .Select(rel => new TableInfo(
                new TableRef(null, rel.Name), rel.IsView, columnsByTable[rel.Name].ToList(),
                foreignKeysByTable[rel.Name].ToList(), indexesByTable[rel.Name].ToList()))
            .ToList();
    }

    // Some servers and collations hand information_schema text back as bytes.
    private static string? Text(DbDataReader r, int i) => r.IsDBNull(i)
        ? null
        : r.GetValue(i) is byte[] bytes ? Encoding.UTF8.GetString(bytes) : Convert.ToString(r.GetValue(i));

    /// <summary>Maps DATA_TYPE (with COLUMN_TYPE for the width and the sign) to the CLR type MySqlConnector returns.</summary>
    internal static Type MapClrType(string dataType, string columnType)
    {
        var type = columnType.ToLowerInvariant();
        var unsigned = type.Contains("unsigned", StringComparison.Ordinal);
        return dataType.ToLowerInvariant() switch
        {
            "tinyint" when type.StartsWith("tinyint(1)", StringComparison.Ordinal) && !unsigned => typeof(bool),
            "tinyint" => unsigned ? typeof(byte) : typeof(sbyte),
            "smallint" => unsigned ? typeof(ushort) : typeof(short),
            "mediumint" or "int" or "integer" => unsigned ? typeof(uint) : typeof(int),
            "bigint" => unsigned ? typeof(ulong) : typeof(long),
            "bit" => typeof(ulong),
            "year" => typeof(int),
            "float" => typeof(float),
            "double" or "real" => typeof(double),
            "decimal" or "numeric" => typeof(decimal),
            "date" or "datetime" or "timestamp" => typeof(DateTime),
            "time" => typeof(TimeSpan),
            "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" or "geometry" or "point"
                or "linestring" or "polygon" or "multipoint" or "multilinestring" or "multipolygon" or "geometrycollection"
                or "geomcollection" => typeof(byte[]),
            _ => typeof(string),
        };
    }

    /// <summary>MariaDB and MySQL differ where it matters (plans); the server version says which one answered.</summary>
    internal static bool IsMariaDb(DbConnection c)
    {
        try { return c.ServerVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase); }
        catch (InvalidOperationException) { return false; }   // not open
    }

    // Modes that change how the server reads quotes and backslashes (the composite ones switch several on at once).
    private static readonly HashSet<string> LexicalModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ANSI_QUOTES", "NO_BACKSLASH_ESCAPES", "ANSI", "MSSQL", "ORACLE", "POSTGRESQL", "DB2", "MAXDB",
    };

    /// <summary>
    /// Makes the session refuse writes whatever the statements do: every transaction (so every autocommit statement too,
    /// also after the implicit commit of a DDL statement) is READ ONLY. The quoting modes are set back to the ones the
    /// read-only guard reads by default, so the server does not run what the guard took for a string.
    /// </summary>
    internal static async Task MakeSessionReadOnlyAsync(DbConnection c, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT @@SESSION.sql_mode";
        var current = Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? "";
        var kept = string.Join(',', current.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(m => !LexicalModes.Contains(m)));
        // The names come from the server and hold only letters, digits and underscores.
        cmd.CommandText = $"SET SESSION sql_mode = '{kept.Replace("'", "")}'";
        await cmd.ExecuteNonQueryAsync(ct);
        cmd.CommandText = "SET SESSION TRANSACTION READ ONLY";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
