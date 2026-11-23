using System.Text.Json;

namespace DbExplorer.Core.Scripting;

/// <param name="TabId">Identity of the script tab; also the file name inside the store folder.</param>
/// <param name="ConnectionId">The connection the script runs on.</param>
/// <param name="FilePath">The file the script was opened from or saved to, when it has one.</param>
public sealed record SavedScript(Guid TabId, Guid ConnectionId, string Text, string? FilePath);

/// <summary>
/// Keeps the unsaved text of script tabs on disk (one small JSON file per tab) so it survives a restart or a crash.
/// Writes are atomic (temp file + move). Anything unreadable is skipped by <see cref="LoadAll"/> and left in place:
/// it is never deleted here, since it may be recoverable by hand.
/// </summary>
public sealed class ScriptSessionStore(string dir)
{
    private const string Extension = ".json";

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
