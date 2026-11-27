using DbExplorer.Core.Model;

namespace DbExplorer.Core.Scripting;

/// <summary>Small keyword and function lists per engine for <see cref="SqlCompletion"/>: the common ones, not the whole grammar.</summary>
internal static class SqlCompletionVocabulary
{
    private static readonly string[] CommonStatements =
        ["SELECT", "INSERT INTO", "UPDATE", "DELETE FROM", "WITH", "CREATE", "ALTER", "DROP", "EXPLAIN", "TRUNCATE TABLE", "BEGIN", "COMMIT", "ROLLBACK"];

    private static readonly string[] CommonKeywords =
    [
        "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN",
        "CROSS JOIN", "ON", "AS", "AND", "OR", "NOT", "IN", "IS NULL", "IS NOT NULL", "LIKE", "BETWEEN", "EXISTS", "CASE", "WHEN",
        "THEN", "ELSE", "END", "DISTINCT", "UNION", "UNION ALL", "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "ASC", "DESC",
        "NULL", "CREATE TABLE", "ALTER TABLE", "DROP TABLE", "CREATE INDEX", "CREATE VIEW", "PRIMARY KEY", "FOREIGN KEY", "REFERENCES",
        "DEFAULT", "CONSTRAINT", "ANY", "ALL", "INTERSECT", "EXCEPT", "OVER", "PARTITION BY", "CURRENT_DATE", "CURRENT_TIMESTAMP",
    ];

    private static readonly string[] PostgresKeywords =
    [
        "LIMIT", "OFFSET", "RETURNING", "ILIKE", "TRUE", "FALSE", "ON CONFLICT", "DO NOTHING", "DO UPDATE SET", "LATERAL",
        "FETCH FIRST", "FILTER", "WITHIN GROUP", "USING", "NULLS FIRST", "NULLS LAST", "WITH RECURSIVE",
    ];

    private static readonly string[] SqlServerKeywords =
    [
        "TOP", "OUTPUT", "CROSS APPLY", "OUTER APPLY", "OFFSET", "FETCH NEXT", "ROWS ONLY", "WITH (NOLOCK)", "NOLOCK", "IDENTITY",
        "NVARCHAR", "DECLARE", "EXEC", "GO", "TRY", "CATCH", "PRINT", "IF", "WHILE", "RETURN", "SET NOCOUNT ON",
    ];

    private static readonly string[] SqliteKeywords =
    [
        "LIMIT", "OFFSET", "RETURNING", "GLOB", "TRUE", "FALSE", "ON CONFLICT", "DO NOTHING", "DO UPDATE SET", "INDEXED BY",
        "AUTOINCREMENT", "WITHOUT ROWID", "COLLATE NOCASE", "USING", "WITH RECURSIVE",
    ];

    private static readonly string[] PostgresStatements = ["VACUUM", "ANALYZE", "COPY", "SET", "SHOW", "GRANT", "REVOKE"];
    private static readonly string[] SqlServerStatements = ["DECLARE", "EXEC", "USE", "SET", "IF", "PRINT", "GO", "GRANT", "REVOKE"];
    private static readonly string[] SqliteStatements = ["PRAGMA", "VACUUM", "REPLACE INTO", "ATTACH DATABASE", "ANALYZE"];

    private static readonly string[] MySqlKeywords =
    [
        "LIMIT", "OFFSET", "TRUE", "FALSE", "ON DUPLICATE KEY UPDATE", "INSERT IGNORE INTO", "REPLACE INTO", "STRAIGHT_JOIN",
        "REGEXP", "AUTO_INCREMENT", "ENGINE=InnoDB", "FOR UPDATE", "WITH ROLLUP", "USING", "WITH RECURSIVE", "SHOW TABLES",
        "SHOW CREATE TABLE", "DELIMITER", "IF", "BEGIN", "DECLARE",
    ];

    private static readonly string[] MySqlStatements =
        ["SHOW", "DESCRIBE", "USE", "SET", "REPLACE INTO", "CALL", "ANALYZE TABLE", "OPTIMIZE TABLE", "GRANT", "REVOKE"];

    private static readonly string[] MySqlFunctions =
    [
        "NOW", "CURDATE", "DATE_FORMAT", "DATE_ADD", "DATE_SUB", "DATEDIFF", "TIMESTAMPDIFF", "STR_TO_DATE", "UNIX_TIMESTAMP",
        "FROM_UNIXTIME", "IFNULL", "IF", "GROUP_CONCAT", "CONCAT_WS", "SUBSTRING_INDEX", "LENGTH", "CHAR_LENGTH", "LOCATE",
        "LEFT", "RIGHT", "FLOOR", "CEIL", "RAND", "UUID", "LAST_INSERT_ID", "ROW_COUNT", "JSON_EXTRACT", "JSON_OBJECT",
        "JSON_ARRAYAGG", "REGEXP_REPLACE", "CONVERT", "DATABASE",
    ];

    /// <summary>Words MySQL or MariaDB reserve beyond the common ones: a name equal to one needs backticks.</summary>
    internal static readonly HashSet<string> MySqlReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACCESSIBLE", "ANALYZE", "BEFORE", "BIGINT", "BINARY", "BLOB", "BOTH", "CALL", "CASCADE", "CHANGE", "CHAR", "CHARACTER",
        "CONDITION", "CONTINUE", "CONVERT", "CUBE", "CUME_DIST", "CURSOR", "DATABASE", "DATABASES", "DECIMAL", "DECLARE", "DELAYED",
        "DESCRIBE", "DIV", "DOUBLE", "DUAL", "EACH", "ELSEIF", "EMPTY", "ENCLOSED", "ESCAPED", "EXIT", "EXPLAIN", "FLOAT", "FORCE",
        "FULLTEXT", "FUNCTION", "GENERATED", "GROUPING", "GROUPS", "HIGH_PRIORITY", "IF", "IGNORE", "INFILE", "INOUT", "INT",
        "INTEGER", "INTERVAL", "ITERATE", "KEYS", "KILL", "LAG", "LEAD", "LEADING", "LEAVE", "LINEAR", "LINES", "LOAD", "LOCK",
        "LONG", "LOOP", "MATCH", "MAXVALUE", "MOD", "MODIFIES", "OF", "OPTIMIZE", "OPTION", "OPTIONALLY", "OUT", "OUTFILE",
        "PRECISION", "PROCEDURE", "PURGE", "RANGE", "RANK", "READ", "READS", "REAL", "RECURSIVE", "REGEXP", "RELEASE", "RENAME",
        "REPEAT", "REPLACE", "REQUIRE", "RESTRICT", "RETURN", "REVOKE", "RLIKE", "ROW", "ROWS", "ROW_NUMBER", "SCHEMA", "SCHEMAS",
        "SENSITIVE", "SEPARATOR", "SHOW", "SIGNAL", "SMALLINT", "SPATIAL", "SQL", "SSL", "STARTING", "STORED", "SYSTEM",
        "TERMINATED", "THEN", "TINYINT", "TRAILING", "TRIGGER", "UNDO", "UNLOCK", "UNSIGNED", "USAGE", "USE", "VARBINARY",
        "VARCHAR", "VARYING", "VIRTUAL", "WHILE", "WRITE", "XOR", "ZEROFILL",
    };

    private static readonly string[] CommonFunctions =
    [
        "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "NULLIF", "CAST", "UPPER", "LOWER", "SUBSTRING", "TRIM", "REPLACE", "ROUND",
        "ABS", "ROW_NUMBER", "RANK", "DENSE_RANK", "LAG", "LEAD", "CONCAT",
    ];

    private static readonly string[] PostgresFunctions =
    [
        "NOW", "DATE_TRUNC", "EXTRACT", "TO_CHAR", "TO_DATE", "TO_TIMESTAMP", "STRING_AGG", "ARRAY_AGG", "GENERATE_SERIES", "LENGTH",
        "POSITION", "AGE", "GREATEST", "LEAST", "UNNEST", "REGEXP_REPLACE", "MD5", "GEN_RANDOM_UUID", "JSONB_BUILD_OBJECT",
        "JSONB_AGG", "SPLIT_PART", "LEFT", "RIGHT", "FLOOR", "CEIL", "RANDOM", "FORMAT", "INITCAP", "BOOL_OR", "BOOL_AND",
    ];

    private static readonly string[] SqlServerFunctions =
    [
        "GETDATE", "GETUTCDATE", "SYSDATETIME", "DATEADD", "DATEDIFF", "DATEPART", "DATENAME", "EOMONTH", "FORMAT", "ISNULL", "IIF",
        "LEN", "CHARINDEX", "STRING_AGG", "STRING_SPLIT", "CONVERT", "TRY_CAST", "TRY_CONVERT", "NEWID", "SCOPE_IDENTITY",
        "OBJECT_ID", "STUFF", "LEFT", "RIGHT", "FLOOR", "CEILING", "ISNUMERIC", "ISDATE", "JSON_VALUE", "OPENJSON", "NEWSEQUENTIALID",
    ];

    private static readonly string[] SqliteFunctions =
    [
        "DATE", "TIME", "DATETIME", "STRFTIME", "JULIANDAY", "IFNULL", "SUBSTR", "INSTR", "LENGTH", "TYPEOF", "RANDOM", "GROUP_CONCAT",
        "JSON_EXTRACT", "TOTAL", "IIF", "PRINTF", "LIKE", "GLOB", "HEX", "QUOTE", "LAST_INSERT_ROWID", "CHANGES", "MAX", "MIN",
    ];

    /// <summary>Words that cannot be used as a table alias and that always need quoting when used as a name.</summary>
    internal static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "ORDER", "BY", "HAVING", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "NATURAL",
        "ON", "AS", "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "BETWEEN", "EXISTS", "CASE", "WHEN", "THEN", "ELSE", "END", "DISTINCT",
        "UNION", "INTERSECT", "EXCEPT", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "CREATE", "ALTER", "DROP", "TABLE", "INDEX",
        "VIEW", "LIMIT", "OFFSET", "USING", "RETURNING", "WITH", "ADD", "COLUMN", "PRIMARY", "FOREIGN", "KEY", "REFERENCES", "DEFAULT",
        "CHECK", "UNIQUE", "CONSTRAINT", "ASC", "DESC", "TOP", "OUTPUT", "OVER", "PARTITION", "WINDOW", "FETCH", "FOR", "ALL", "ANY",
        "USER", "CURRENT_USER", "CURRENT_DATE", "CURRENT_TIMESTAMP", "TRUE", "FALSE", "TO", "GRANT", "TRUNCATE", "APPLY", "GO", "LATERAL",
    };

    public static IReadOnlyList<string> StatementKeywords(DbKind kind) => [.. CommonStatements, .. kind switch
    {
        DbKind.Postgres => PostgresStatements,
        DbKind.SqlServer => SqlServerStatements,
        DbKind.MySql => MySqlStatements,
        _ => SqliteStatements,
    }];

    public static IReadOnlyList<string> Keywords(DbKind kind) => [.. CommonKeywords, .. kind switch
    {
        DbKind.Postgres => PostgresKeywords,
        DbKind.SqlServer => SqlServerKeywords,
        DbKind.MySql => MySqlKeywords,
        _ => SqliteKeywords,
    }];

    public static IReadOnlyList<string> Functions(DbKind kind) => [.. CommonFunctions, .. kind switch
    {
        DbKind.Postgres => PostgresFunctions,
        DbKind.SqlServer => SqlServerFunctions,
        DbKind.MySql => MySqlFunctions,
        _ => SqliteFunctions,
    }];
}
