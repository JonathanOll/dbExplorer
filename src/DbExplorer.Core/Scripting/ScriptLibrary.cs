using DbExplorer.Core.Upgrade;

namespace DbExplorer.Core.Scripting;

/// <summary>A refused library operation (bad name, outside the root, would overwrite...). The message is meant for the user.</summary>
public sealed class ScriptLibraryException(string message) : Exception(message);

public enum LibraryEntryKind { Folder, Script }

/// <summary>One folder or script of the library. <see cref="Name"/> is the file or directory name (scripts keep their .sql).</summary>
public sealed record LibraryEntry(string Path, string Name, LibraryEntryKind Kind)
{
    public bool IsFolder => Kind == LibraryEntryKind.Folder;

    /// <summary>The name shown to the user: scripts without the .sql extension.</summary>
    public string DisplayName => IsFolder ? Name : System.IO.Path.GetFileNameWithoutExtension(Name);
}

/// <summary>A folder of the library for pickers: its path, the name relative to the root and its depth (0 = directly under the root).</summary>
public sealed record LibraryFolder(string Path, string RelativeName, int Depth);

/// <summary>
/// The script library: a root directory where scripts are plain .sql files and folders are real sub-directories. Nothing is
/// stored twice, so a script tab simply points at a file inside it. Every path handed in is resolved and must stay inside the
/// root (no <c>..</c> tricks); every refusal is a <see cref="ScriptLibraryException"/> with a message for the user, never a
/// silent failure. Nothing here overwrites an existing file or folder. IO errors (locked file, access denied) propagate as
/// their own exceptions: callers report them.
/// </summary>
public sealed class ScriptLibrary
{
    public const string Extension = ".sql";
    public const int MaxNameLength = 120;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public ScriptLibrary(string root) => Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>Full path of the library folder (may not exist yet: it is created by the first write).</summary>
    public string Root { get; }

    /// <summary>Creates the root folder when missing.</summary>
    public void EnsureRoot() => Directory.CreateDirectory(Root);

    // ---- reading ----

    /// <summary>The folders (first) then the scripts directly inside <paramref name="folder"/> (the root when null), in natural name order.</summary>
    public IReadOnlyList<LibraryEntry> List(string? folder = null)
    {
        var dir = folder is null ? Root : Inside(folder);
        if (!Directory.Exists(dir)) return [];
        var folders = new List<LibraryEntry>();
        var scripts = new List<LibraryEntry>();
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.Hidden) || entry.Name.StartsWith('.')) continue;
            if (entry is DirectoryInfo) folders.Add(new LibraryEntry(entry.FullName, entry.Name, LibraryEntryKind.Folder));
            else if (entry.Extension.Equals(Extension, StringComparison.OrdinalIgnoreCase))
                scripts.Add(new LibraryEntry(entry.FullName, entry.Name, LibraryEntryKind.Script));
        }
        folders.Sort((a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name));
        // By the name the user sees: "q" before "q (2)", not after it because of the dot of ".sql".
        scripts.Sort((a, b) => NaturalComparer.Instance.Compare(a.DisplayName, b.DisplayName));
        return [.. folders, .. scripts];
    }

    /// <summary>How many scripts a folder holds, sub-folders included.</summary>
    public int CountScripts(string folder)
    {
        var dir = Inside(folder);
        if (!Directory.Exists(dir)) return 0;
        return Directory.EnumerateFiles(dir, "*" + Extension, SearchOption.AllDirectories).Count();
    }

    /// <summary>Every folder of the library, depth first and name-sorted (for the "Move to" and "Save script" pickers).</summary>
    public IReadOnlyList<LibraryFolder> AllFolders()
    {
        var result = new List<LibraryFolder>();
        Walk(Root, "", 0);
        return result;

        void Walk(string dir, string prefix, int depth)
        {
            foreach (var entry in List(dir).Where(e => e.IsFolder))
            {
                var relative = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
                result.Add(new LibraryFolder(entry.Path, relative, depth));
                Walk(entry.Path, relative, depth + 1);
            }
        }
    }

    /// <summary>True when <paramref name="path"/> resolves to the root or somewhere below it.</summary>
    public bool Contains(string path)
    {
        try { return IsInside(Path.GetFullPath(path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    // ---- names ----

    /// <summary>Why <paramref name="name"/> cannot be a file or folder name, or null when it can. A script's ".sql" is optional.</summary>
    public static string? ValidateName(string? name)
    {
        if (name is null || name.Trim().Length == 0) return "The name cannot be empty.";
        if (name != name.Trim()) return "The name cannot start or end with a space.";
        if (name.Length > MaxNameLength) return $"The name is too long (at most {MaxNameLength} characters).";
        if (name.Contains("..")) return "The name cannot contain \"..\".";
        if (name.IndexOfAny(['/', '\\']) >= 0) return "The name cannot contain / or \\.";
        var bad = name.IndexOfAny(Path.GetInvalidFileNameChars());
        if (bad >= 0) return $"The name cannot contain the character \"{name[bad]}\".";
        if (name.EndsWith('.')) return "The name cannot end with a dot.";
        var stem = name.Contains('.') ? name[..name.IndexOf('.')] : name;
        if (Reserved.Contains(stem)) return $"\"{stem}\" is a reserved name on Windows.";
        return null;
    }

    private static string ScriptFileName(string name) =>
        name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? name : name + Extension;

    private static void Check(string? name)
    {
        if (ValidateName(name) is { } problem) throw new ScriptLibraryException(problem);
    }

    // ---- writing ----

    /// <summary>Creates <paramref name="name"/> inside <paramref name="parent"/> (the root when null) and returns its path. Refuses an existing name.</summary>
    public string CreateFolder(string? parent, string name)
    {
        Check(name);
        var dir = ParentDir(parent);
        var path = Inside(Path.Combine(dir, name));
        if (Exists(path)) throw new ScriptLibraryException($"\"{name}\" already exists here.");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Renames a script (its .sql is kept) or a folder inside its parent. Refuses to overwrite; a case-only change is allowed.</summary>
    public string Rename(string path, string newName)
    {
        var source = Inside(path);
        if (SamePath(source, Root)) throw new ScriptLibraryException("The library folder itself cannot be renamed.");
        var isFolder = Directory.Exists(source);
        if (!isFolder && !File.Exists(source)) throw new ScriptLibraryException("It no longer exists.");
        Check(newName);
        var fileName = isFolder ? newName : ScriptFileName(newName);
        var target = Inside(Path.Combine(Path.GetDirectoryName(source)!, fileName));
        if (SamePath(source, target) && source == target) return source;
        if (!SamePath(source, target) && Exists(target)) throw new ScriptLibraryException($"\"{fileName}\" already exists here.");
        if (isFolder) Directory.Move(source, target); else File.Move(source, target);
        return target;
    }

    /// <summary>Moves a script or folder into <paramref name="targetFolder"/> (the root when null). Refuses to overwrite or to move a folder into itself or a descendant.</summary>
    public string Move(string path, string? targetFolder)
    {
        var source = Inside(path);
        if (SamePath(source, Root)) throw new ScriptLibraryException("The library folder itself cannot be moved.");
        var isFolder = Directory.Exists(source);
        if (!isFolder && !File.Exists(source)) throw new ScriptLibraryException("It no longer exists.");
        var targetDir = ParentDir(targetFolder);
        if (isFolder && (SamePath(targetDir, source) || IsBelow(targetDir, source)))
            throw new ScriptLibraryException("A folder cannot be moved into itself or into one of its own folders.");
        var target = Inside(Path.Combine(targetDir, Path.GetFileName(source)));
        if (SamePath(source, target)) return source;
        if (Exists(target)) throw new ScriptLibraryException($"\"{Path.GetFileName(source)}\" already exists in the target folder.");
        if (isFolder) Directory.Move(source, target); else File.Move(source, target);
        return target;
    }

    /// <summary>Deletes a script, or a folder when <paramref name="recursive"/>: a folder that still holds anything is refused otherwise.</summary>
    public void Delete(string path, bool recursive = false)
    {
        var target = Inside(path);
        if (SamePath(target, Root)) throw new ScriptLibraryException("The library folder itself cannot be deleted.");
        if (Directory.Exists(target))
        {
            if (!recursive && Directory.EnumerateFileSystemEntries(target).Any())
                throw new ScriptLibraryException("The folder is not empty.");
            Directory.Delete(target, recursive);
        }
        else if (File.Exists(target)) File.Delete(target);
        else throw new ScriptLibraryException("It no longer exists.");
    }

    /// <summary>
    /// Takes a free file name for a new script (<c>name.sql</c>, else <c>name (2).sql</c>...) by creating the empty file, so two
    /// saves can never pick the same name, and returns its path. The caller writes the content.
    /// </summary>
    public string ReserveNew(string? folder, string name)
    {
        Check(name);
        var dir = ParentDir(folder);
        Directory.CreateDirectory(dir);
        var baseName = Path.GetFileNameWithoutExtension(ScriptFileName(name));
        for (var n = 1; ; n++)
        {
            var path = Inside(Path.Combine(dir, n == 1 ? baseName + Extension : $"{baseName} ({n}){Extension}"));
            try
            {
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                return path;
            }
            catch (IOException) when (File.Exists(path)) { /* taken: try the next number */ }
        }
    }

    /// <summary>The name <see cref="ReserveNew"/> would give now (nothing is created), for a hint in the save dialog.</summary>
    public string PreviewNewName(string? folder, string name)
    {
        var dir = ParentDir(folder);
        var baseName = Path.GetFileNameWithoutExtension(ScriptFileName(name));
        for (var n = 1; ; n++)
        {
            var file = n == 1 ? baseName + Extension : $"{baseName} ({n}){Extension}";
            if (!File.Exists(Path.Combine(dir, file))) return file;
        }
    }

    /// <summary>Creates a new script file with <paramref name="text"/> under a free name and returns its path.</summary>
    public string SaveNew(string? folder, string name, string text)
    {
        var path = ReserveNew(folder, name);
        try
        {
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { /* the empty reservation stays: harmless */ }
            throw;
        }
        return path;
    }

    /// <summary>Copies a script next to itself as <c>name (2).sql</c> (a free name) and returns the new path.</summary>
    public string Duplicate(string path)
    {
        var source = Inside(path);
        if (!File.Exists(source)) throw new ScriptLibraryException("The script no longer exists.");
        var copy = ReserveNew(Path.GetDirectoryName(source), Path.GetFileNameWithoutExtension(source));
        try
        {
            File.Copy(source, copy, overwrite: true);
        }
        catch
        {
            try { File.Delete(copy); } catch (IOException) { /* harmless */ }
            throw;
        }
        return copy;
    }

    // ---- path safety ----

    private string ParentDir(string? folder)
    {
        if (folder is null) return Root;
        var dir = Inside(folder);
        if (File.Exists(dir)) throw new ScriptLibraryException("The target is a script, not a folder.");
        if (!Directory.Exists(dir) && !SamePath(dir, Root)) throw new ScriptLibraryException("The target folder no longer exists.");
        return dir;
    }

    /// <summary>The full path of <paramref name="path"/> when it is the root or inside it; throws otherwise.</summary>
    private string Inside(string path)
    {
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ScriptLibraryException("That path is not valid."); }
        if (!IsInside(full)) throw new ScriptLibraryException("That path is outside the script library.");
        return full;
    }

    private bool IsInside(string full) => SamePath(full, Root) || IsBelow(full, Root);

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsBelow(string path, string parent) =>
        path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
