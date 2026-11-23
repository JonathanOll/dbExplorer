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

    /// <summary>Unsaved script tabs, restored at the next start.</summary>
    public string ScriptsDir => Path.Combine(DataDir, "scripts");

    public const string Usage = "Usage: dbExplorer [--data-dir <path>] [--dbeaver-dir <path>]";

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


