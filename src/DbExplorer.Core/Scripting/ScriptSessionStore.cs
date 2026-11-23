using System.Text.Json;

namespace DbExplorer.Core.Scripting;

/// <param name="TabId">Identity of the script tab; also the file name inside the store folder.</param>
/// <param name="ConnectionId">The connection the script runs on.</param>
/// <param name="Text">The unsaved text. Null only for a script restored from the tab session that had nothing unsaved:
/// the tab then reads its text from <paramref name="FilePath"/> (or starts empty). Never null in the store itself.</param>
/// <param name="FilePath">The file the script was opened from or saved to, when it has one.</param>
public sealed record SavedScript(Guid TabId, Guid ConnectionId, string? Text, string? FilePath);

/// <summary>
/// Keeps the unsaved text of script tabs on disk (one small JSON file per tab) so it survives a restart or a crash.
/// Writes are atomic (temp file + move). Anything unreadable is skipped by <see cref="LoadAll"/> and left in place:
/// it is never deleted here, since it may be recoverable by hand.
/// </summary>
public sealed class ScriptSessionStore(string dir)
{
    private const string Extension = ".json";

    /// <summary>The folder holding the copies.</summary>
    public string Folder => dir;

    /// <summary>True when a copy file exists for the tab, readable or not (a corrupt one is skipped by <see cref="LoadAll"/> but kept).</summary>
    public bool Exists(Guid tabId) => File.Exists(PathOf(tabId));

    /// <summary>When the copy of the tab was last written (UTC); the minimum date when there is none.</summary>
    public DateTime LastWriteUtc(Guid tabId) => File.GetLastWriteTimeUtc(PathOf(tabId));

    public void Save(Guid tabId, Guid connectionId, string text, string? filePath)
    {
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new SavedScript(tabId, connectionId, text, filePath));
        var target = PathOf(tabId);
        var temp = target + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, target, overwrite: true);
    }

    public IReadOnlyList<SavedScript> LoadAll()
    {
        if (!Directory.Exists(dir)) return [];

        var result = new List<SavedScript>();
        foreach (var file in Directory.EnumerateFiles(dir, "*" + Extension))
        {
            try
            {
                var script = JsonSerializer.Deserialize<SavedScript>(File.ReadAllText(file));
                if (script is { Text: not null } && script.TabId != Guid.Empty) result.Add(script);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // unreadable: skip, keep the file
            }
        }
        return result;
    }

    public void Delete(Guid tabId)
    {
        try
        {
            File.Delete(PathOf(tabId));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort: a leftover file only means the script is offered again at the next start
        }
    }

    private string PathOf(Guid tabId) => Path.Combine(dir, tabId.ToString("N") + Extension);
}
