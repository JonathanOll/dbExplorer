using System.Text.RegularExpressions;

namespace DbExplorer.Core.Upgrade;

/// <summary>One <c>*.sql</c> file of a version folder. <see cref="ProductionTag"/> is e.g. <c>Production2</c>.</summary>
public sealed record DbUpdateScript(string FileName, string FullPath, string FolderName, bool IsProduction, string? ProductionTag);

/// <summary>
/// A <c>vNNNN-...</c> folder of the dbupdate tree. <see cref="Code"/> is the leading <c>v0860</c> part of the name,
/// <see cref="Sprint"/> is null when the name carries no sprint number (hotfix folders).
/// <see cref="Ignored"/> holds the names of the entries that are not top-level <c>.sql</c> files.
/// </summary>
public sealed record DbUpdateFolder(
    string Name, string FullPath, string Code, int Version, int? Sprint,
    IReadOnlyList<DbUpdateScript> Scripts, IReadOnlyList<string> Ignored);

/// <summary>
/// The scanned dbupdate folder of the n2f repo: its version folders (top level only) and their scripts.
/// Pure disk listing: file contents are never read.
/// </summary>
public sealed class DbUpdateCatalog
{
    private static readonly Regex VersionFolder = new(@"^[vV]\d+", RegexOptions.Compiled);
    private static readonly Regex SprintNumber = new(@"sprint\s*[- ]?\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ProductionSuffix = new(@"-\s*(Production\d+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Root { get; }
    public IReadOnlyList<DbUpdateFolder> Folders { get; }
    /// <summary>Distinct sprint numbers present, ascending.</summary>
    public IReadOnlyList<int> Sprints { get; }

    private DbUpdateCatalog(string root, IReadOnlyList<DbUpdateFolder> folders)
    {
        Root = root;
        Folders = folders;
        Sprints = folders.Where(f => f.Sprint.HasValue).Select(f => f.Sprint!.Value).Distinct().Order().ToList();
    }

    /// <summary>The folders of one sprint, in run order (empty for an unknown sprint).</summary>
    public IReadOnlyList<DbUpdateFolder> FoldersOfSprint(int sprint) =>
        Folders.Where(f => f.Sprint == sprint).ToList();

    /// <summary>
    /// The <paramref name="perSide"/> sprints closest to <paramref name="sprint"/> (ties go to the lower one),
    /// ascending. Used to suggest alternatives for an unknown sprint number.
    /// </summary>
    public IReadOnlyList<int> NearestSprints(int sprint, int perSide = 2) =>
        Sprints.OrderBy(s => Math.Abs((long)s - sprint)).ThenBy(s => s).Take(Math.Max(perSide, 0)).Order().ToList();

    /// <summary>Null when <paramref name="path"/> is a usable dbupdate folder, otherwise a user-facing message. Never throws.</summary>
    public static string? Validate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Enter the path of the dbupdate folder.";
        var p = Normalize(path);
        try
        {
            if (File.Exists(p)) return $"'{p}' is a file, not a folder.";
            if (!Directory.Exists(p)) return $"Folder not found: {p}";
            if (!Directory.EnumerateDirectories(p).Any(d => VersionFolder.IsMatch(Path.GetFileName(d))))
                return $"No version folder (v0001-..., v0860-sprint 143...) found in {p}. Is this the dbupdate folder?";
            return null;
        }
        catch (Exception ex)
        {
            return $"Cannot read {p}: {ex.Message}";
        }
    }

    /// <summary>
    /// Lists the version folders and their scripts. Throws the usual I/O exceptions when the root folder is unreadable; a version
    /// folder that cannot be read is listed without scripts and with one ignored entry saying so.
    /// </summary>
    public static DbUpdateCatalog Scan(string path) => Scan(path, Directory.EnumerateFileSystemEntries);

    /// <summary>Test seam: <paramref name="listEntries"/> lists the entries of a version folder.</summary>
    internal static DbUpdateCatalog Scan(string path, Func<string, IEnumerable<string>> listEntries)
    {
        var root = Normalize(path);
        var folders = new List<DbUpdateFolder>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            var m = VersionFolder.Match(name);
            if (!m.Success) continue;
            folders.Add(ScanFolder(dir, name, m.Value, listEntries));
        }
        folders.Sort((a, b) =>
        {
            var c = a.Version.CompareTo(b.Version);
            return c != 0 ? c : NaturalComparer.Instance.Compare(a.Name, b.Name);
        });
        return new DbUpdateCatalog(root, folders);
    }

    private static DbUpdateFolder ScanFolder(string dir, string name, string code, Func<string, IEnumerable<string>> listEntries)
    {
        var digits = code.AsSpan(1);
        var version = int.TryParse(digits, out var v) ? v : int.MaxValue;

        int? sprint = null;
        var sm = SprintNumber.Match(name);
        if (sm.Success && int.TryParse(sm.Groups[1].Value, out var s)) sprint = s;

        var scripts = new List<DbUpdateScript>();
        var ignored = new List<string>();
        List<string> entries;
        try
        {
            entries = listEntries(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // One unreadable folder must not hide the others: it is kept, without scripts, and says why in its ignored entries.
            return new DbUpdateFolder(name, dir, code, version, sprint, [], [$"(folder cannot be read: {ex.Message})"]);
        }

        foreach (var entry in entries)
        {
            var file = Path.GetFileName(entry);
            // EnumerateFiles' "*.sql" pattern also matches longer extensions on Windows, so test the extension itself.
            if (!Directory.Exists(entry) && string.Equals(Path.GetExtension(file), ".sql", StringComparison.OrdinalIgnoreCase))
            {
                var pm = ProductionSuffix.Match(Path.GetFileNameWithoutExtension(file));
                scripts.Add(new DbUpdateScript(file, entry, name, pm.Success, pm.Success ? pm.Groups[1].Value : null));
            }
            else ignored.Add(file);
        }
        scripts.Sort((a, b) => NaturalComparer.Instance.Compare(a.FileName, b.FileName));
        ignored.Sort(NaturalComparer.Instance);
        return new DbUpdateFolder(name, dir, code, version, sprint, scripts, ignored);
    }

    private static string Normalize(string path)
    {
        var p = path.Trim();
        var trimmed = p.TrimEnd('\\', '/');
        // Keep a bare root such as "C:\" intact.
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? p : trimmed;
    }
}
