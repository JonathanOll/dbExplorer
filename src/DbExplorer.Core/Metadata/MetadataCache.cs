using System.Text.Json;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Metadata;

/// <summary>Lightweight searchable snapshot of a table or view: name and column names only.</summary>
public record CachedTable(TableRef Ref, bool IsView, string[] Columns);

/// <summary>Persists one JSON file per connection id so searches work without connecting.</summary>
public class MetadataCache(string dir)
{
    private static readonly JsonSerializerOptions Options = new();

    public void Save(Guid connectionId, IReadOnlyList<TableInfo> schema)
    {
        var entries = schema
            .Select(t => new CachedTable(t.Ref, t.IsView, t.Columns.Select(c => c.Name).ToArray()))
            .ToList();

        Directory.CreateDirectory(dir);
        var path = PathFor(connectionId);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries, Options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Returns the cached tables, or an empty list when there is no cache or the file is unreadable.</summary>
    public IReadOnlyList<CachedTable> Load(Guid connectionId)
    {
        var path = PathFor(connectionId);
        try
        {
            if (!File.Exists(path)) return [];
            var entries = JsonSerializer.Deserialize<List<CachedTable>>(File.ReadAllText(path), Options);
            if (entries is null || entries.Any(e => e is null || e.Ref is null || e.Columns is null)) return [];
            return entries;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Routines are read after the tables, so they have a file of their own.</summary>
    public void SaveRoutines(Guid connectionId, IReadOnlyList<RoutineInfo> routines)
    {
        Directory.CreateDirectory(dir);
        var path = RoutinesPathFor(connectionId);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(routines, Options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Returns the cached procedures and functions, or an empty list when there are none or the file is unreadable.</summary>
    public IReadOnlyList<RoutineInfo> LoadRoutines(Guid connectionId)
    {
        var path = RoutinesPathFor(connectionId);
        try
        {
            if (!File.Exists(path)) return [];
            var entries = JsonSerializer.Deserialize<List<RoutineInfo>>(File.ReadAllText(path), Options);
            if (entries is null || entries.Any(e => e is null || e.Ref is null || e.Ref.Name is null)) return [];
            return entries;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Delete(Guid connectionId)
    {
        foreach (var path in new[] { PathFor(connectionId), RoutinesPathFor(connectionId) })
            if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(Guid connectionId) => Path.Combine(dir, connectionId.ToString("N") + ".json");

    private string RoutinesPathFor(Guid connectionId) => Path.Combine(dir, connectionId.ToString("N") + ".routines.json");
}
