using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// Where a quick-open row lives, shared by all rows of one connection: what a word of the query can name besides the object
/// itself (connection, database, folder, linked groups, environment).
/// </summary>
public sealed class QuickOpenScope
{
    private QuickOpenScope(string connection, string database, string[] others, bool isActive)
    {
        Connection = connection;
        Database = database;
        Others = others;
        IsActive = isActive;
        Badge = database.Length == 0 || database.Equals(connection, StringComparison.OrdinalIgnoreCase)
            ? connection
            : $"{connection} / {database}";
    }

    public string Connection { get; }

    /// <summary>The database name; for SQLite the file name without folder and extension (a path would match anything).</summary>
    public string Database { get; }

    /// <summary>Weaker names: folder, groups of the connection, environment ("Production", "PROD").</summary>
    public string[] Others { get; }

    /// <summary>The connection Ctrl+T was opened on: its rows come first among equal matches.</summary>
    public bool IsActive { get; }

    /// <summary>"connection / database" (the connection alone when both are the same): the chip of a row.</summary>
    public string Badge { get; }

    public static QuickOpenScope For(ConnectionConfig config, IEnumerable<ConnectionGroup>? groups = null, bool isActive = false)
    {
        var others = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.Folder)) others.Add(config.Folder);
        if (groups is not null) others.AddRange(groups.Where(g => g.ConnectionIds.Contains(config.Id)).Select(g => g.Name));
        if (config.Environment != DbEnvironment.None)
        {
            others.Add(config.Environment.Label());
            others.Add(config.Environment.ShortLabel());
        }

        var database = config.Kind == DbKind.Sqlite ? Path.GetFileNameWithoutExtension(config.Database) : config.Database;
        return new QuickOpenScope(config.Name, database ?? "", [.. others], isActive);
    }
}
