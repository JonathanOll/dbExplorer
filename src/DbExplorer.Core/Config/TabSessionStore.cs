using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbExplorer.Core.Config;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SavedTabKind { Table, Script }

/// <summary>
/// One open tab as remembered between runs. <see cref="ConnectionId"/> is the connection the tab is on NOW (after a group
/// switch, the member). Tables keep their filter boxes, raw WHERE and sort; scripts keep only their identity and file: the
/// unsaved text lives in the script store, under the same <see cref="ScriptId"/>.
/// </summary>
public sealed record SavedTab(
    SavedTabKind Kind,
    Guid ConnectionId,
    string? Schema = null,
    string? Table = null,
    Dictionary<string, string>? Filters = null,
    string? RawWhere = null,
    string? SortColumn = null,
    bool SortDescending = false,
    Guid? ScriptId = null,
    string? FilePath = null);

/// <summary>The open tabs in order; <see cref="ActiveIndex"/> is the active one in <see cref="Tabs"/> (-1: none).</summary>
public sealed record SavedTabs(int ActiveIndex, List<SavedTab> Tabs);

/// <summary>Result of <see cref="TabSessionStore.Load"/>: no session (missing or unusable file), or the tabs; a notice when the file had to be set aside.</summary>
public sealed record TabSessionLoad(SavedTabs? Session, string? Notice);

/// <summary>
/// The tabs that were open when the app closed, in one small JSON file (<c>session.json</c>) next to the config. Same
/// conventions as <see cref="ConfigStore"/>: atomic write (temp file + move), a corrupt file is moved to
/// <c>.bad-&lt;timestamp&gt;</c>. Unlike the config, nothing here is ever fatal: loading and saving never throw.
/// </summary>
public sealed class TabSessionStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string FilePath => path;

    /// <summary>A missing file or one that cannot be read (locked) is "no session"; a corrupt one is moved aside, with a notice.</summary>
    public TabSessionLoad Load()
    {
        try
        {
            if (!File.Exists(path)) return new TabSessionLoad(null, null);
            var json = File.ReadAllText(path);
            try
            {
                var session = JsonSerializer.Deserialize<SavedTabs>(json, Options);
                if (session?.Tabs is null || session.Tabs.Any(t => t is null)) throw new JsonException("The session contains null entries.");
                return new TabSessionLoad(session, null);
            }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            {
                var bad = $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Move(path, bad, overwrite: true);
                return new TabSessionLoad(null,
                    $"{Path.GetFileName(path)} was corrupt ({e.Message}); moved to {Path.GetFileName(bad)}. The tabs were not restored.");
            }
        }
        catch (Exception)
        {
            return new TabSessionLoad(null, null);   // locked or unreadable: start without a session, leave the file alone
        }
    }

    /// <summary>Atomic best-effort save. False when it failed: losing the session must never get in the way of anything.</summary>
    public bool Save(SavedTabs session)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(session, Options));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
