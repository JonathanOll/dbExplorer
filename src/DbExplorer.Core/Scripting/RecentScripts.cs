using System.Text.Json;

namespace DbExplorer.Core.Scripting;

/// <summary>
/// The scripts opened or saved lately (any path, not only library ones): at most <see cref="Max"/>, most recent first, no
/// duplicates. Kept in a small JSON file, read on first use (never at start-up) and written atomically (temp file, then move).
/// A corrupt file is ignored and left alone until the next change replaces it; files that no longer exist are dropped when
/// the list is read. Thread-safe. Never throws: a file that cannot be written only means the list is not remembered.
/// </summary>
public sealed class RecentScripts(string file, int max = RecentScripts.Max)
{
    public const int Max = 20;

    private readonly object _gate = new();
    private List<string>? _items;

    /// <summary>The recent paths, most recent first; entries whose file is gone are removed.</summary>
    public IReadOnlyList<string> Items
    {
        get
        {
            lock (_gate)
            {
                var items = Load();
                var alive = items.Where(File.Exists).ToList();
                if (alive.Count != items.Count)
                {
                    _items = alive;
                    Save(alive);
                }
                return [.. alive];
            }
        }
    }

    /// <summary>Puts <paramref name="path"/> first (moving it up when already listed).</summary>
    public void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        lock (_gate)
        {
            var items = Load();
            items.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
            items.Insert(0, full);
            if (items.Count > max) items.RemoveRange(max, items.Count - max);
            Save(items);
        }
    }

    public void Remove(string path)
    {
        lock (_gate)
        {
            var items = Load();
            if (items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0) Save(items);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items = [];
            Save(_items);
        }
    }

    private List<string> Load()
    {
        if (_items is not null) return _items;
        var items = new List<string>();
        try
        {
            if (File.Exists(file))
            {
                var read = JsonSerializer.Deserialize<List<string?>>(File.ReadAllText(file));
                if (read is not null)
                    foreach (var path in read)
                        if (!string.IsNullOrWhiteSpace(path) && !items.Contains(path, StringComparer.OrdinalIgnoreCase)) items.Add(path);
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            items.Clear();   // corrupt or unreadable: start empty, the file is not touched until the list changes
        }
        if (items.Count > max) items.RemoveRange(max, items.Count - max);
        return _items = items;
    }

    private void Save(List<string> items)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
            Directory.CreateDirectory(dir);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(items));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // not remembered across restarts; the in-memory list still works
        }
    }
}
