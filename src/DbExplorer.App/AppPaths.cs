namespace DbExplorer.App;

/// <summary>
/// Where the app keeps its files. Defaults to %APPDATA%\dbExplorer; overridable for tests and verification:
/// <c>--data-dir &lt;path&gt;</c> or env <c>DBEXPLORER_DATA_DIR</c>. The DBeaver workspace folder is overridable with
/// <c>--dbeaver-dir &lt;path&gt;</c> or env <c>DBEXPLORER_DBEAVER_DIR</c>; when only the data dir is overridden the
/// DBeaver folder defaults to <c>&lt;data dir&gt;\dbeaver</c>, never the real workspace. A flag without a value throws.
/// </summary>
public sealed record AppPaths(string DataDir, string DBeaverDir)
{
    public string ConfigFile => Path.Combine(DataDir, "config.json");
    public string CacheDir => Path.Combine(DataDir, "cache");

    /// <summary>Size, position and maximised state of the main window (a tiny JSON file read at start).</summary>
    public string WindowStateFile => Path.Combine(DataDir, "window.json");

    /// <summary>The tabs that were open when the app closed (order, active tab, filters...), reopened at the next start.</summary>
    public string SessionFile => Path.Combine(DataDir, "session.json");

    /// <summary>Unsaved script tabs, restored at the next start.</summary>
    public string ScriptsDir => Path.Combine(DataDir, "scripts");

    /// <summary>The script library: .sql files, with real sub-folders to organise them.</summary>
    public string LibraryDir => Path.Combine(DataDir, "library");

    /// <summary>The scripts opened or saved lately (JSON, up to 20).</summary>
    public string RecentScriptsFile => Path.Combine(DataDir, "recent-scripts.json");

    /// <summary>The statements run from script tabs (JSON lines, at most 5000, read when the history opens).</summary>
    public string HistoryFile => Path.Combine(DataDir, "history.jsonl");

    /// <summary>What Claude Code asked through the MCP server (JSON lines).</summary>
    public string ClaudeActivityFile => Path.Combine(DataDir, "claude-activity.jsonl");

    /// <summary>The MCP configuration written for "Open in Claude Code".</summary>
    public string ClaudeMcpConfigFile => Path.Combine(DataDir, "claude", "mcp.json");

    public const string Usage = "Usage: dbExplorer [--data-dir <path>] [--dbeaver-dir <path>] [--no-update-check]";

    /// <summary>
    /// Checks the command line only (cheap, no disk access): the message to show the user when a flag has no value, or null when
    /// <see cref="Resolve"/> will not throw. Run before anything else so a bad flag is reported instead of crashing the startup.
    /// </summary>
    public static string? CheckArguments(string[] args)
    {
        foreach (var flag in new[] { "--data-dir", "--dbeaver-dir" })
        {
            try { Option(args, flag); }
            catch (ArgumentException e) { return e.Message + " " + Usage; }
        }
        return null;
    }

    /// <summary>
    /// True when the automatic update check is switched off for this run: <c>--no-update-check</c> or env
    /// <c>DBEXPLORER_NO_UPDATE_CHECK</c> set to 1 / true. The flag takes no value, so it never makes <see cref="CheckArguments"/> fail.
    /// </summary>
    public static bool UpdateCheckDisabled(string[] args, Func<string, string?>? env = null)
    {
        if (Array.IndexOf(args, "--no-update-check") >= 0) return true;
        var value = (env ?? Environment.GetEnvironmentVariable)("DBEXPLORER_NO_UPDATE_CHECK")?.Trim();
        return value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public static AppPaths Resolve(string[] args, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var given = Option(args, "--data-dir") ?? env("DBEXPLORER_DATA_DIR");
        var overridden = given is not null;
        var data = given
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dbExplorer");
        // With an overridden data dir the real DBeaver workspace is never used implicitly.
        var dbeaver = Option(args, "--dbeaver-dir") ?? env("DBEXPLORER_DBEAVER_DIR")
            ?? (overridden ? Path.Combine(data, "dbeaver") : Core.Config.DBeaverImporter.DefaultDirectory);
        return new AppPaths(data, dbeaver);
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        if (i < 0) return null;
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--")) throw new ArgumentException($"{name} requires a value.");
        return args[i + 1];
    }
}


