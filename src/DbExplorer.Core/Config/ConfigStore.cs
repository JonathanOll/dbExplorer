using System.Text.Json;
using System.Text.Json.Serialization;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Config;

public class ConfigStore(string path, ISecretProtector protector)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Path of the config file.</summary>
    public string FilePath => path;

    /// <summary>Protector used to encrypt/decrypt connection passwords stored in the file.</summary>
    public ISecretProtector Protector { get; } = protector;

    /// <summary>Loads the config. Throws <see cref="JsonException"/> on a corrupt file; null collections become empty.</summary>
    public AppConfig Load()
    {
        if (!File.Exists(path)) return AppConfig.Empty();
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AppConfig>(json, Options);
        if (config is null) return AppConfig.Empty();
        // A hand-edited file may contain explicit nulls, which the record constructor does not reject.
        // An explicit "Groups": null is treated as corrupt (a missing property keeps the init default).
        if (config.Groups is null)
            throw new JsonException("The config contains null entries.");
        var normalized = new AppConfig(config.Folders ?? new(), config.Connections ?? new(), config.VirtualKeys ?? new())
        {
            Groups = config.Groups,
            AutoUpdateCheck = config.AutoUpdateCheck,
            ShowFolders = config.ShowFolders,
            ShowGroups = config.ShowGroups,
            ShowScripts = config.ShowScripts,
            DbUpdatePath = config.DbUpdatePath,
            RecordHistory = config.RecordHistory
        };
        if (normalized.Groups.Any(g => g is null || g.Name is null || g.ConnectionIds is null)
            || normalized.Folders.Any(f => f is null)
            || normalized.Connections.Any(c => c is null || c.Name is null || c.Database is null)
            || normalized.VirtualKeys.Values.Any(v => v is null))
            throw new JsonException("The config contains null entries.");
        return normalized;
    }

    /// <summary>
    /// Startup-safe load: a corrupt file is moved to <c>config.json.bad-&lt;timestamp&gt;</c> and an empty config
    /// is returned together with a warning message. I/O errors (locked file) still throw, so that an empty
    /// config can never silently replace a config that merely could not be read.
    /// </summary>
    public ConfigLoadResult LoadOrRecover()
    {
        try
        {
            return new ConfigLoadResult(Load(), null);
        }
        catch (JsonException e)
        {
            var bad = $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, bad, overwrite: true);
            return new ConfigLoadResult(
                AppConfig.Empty(),
                $"Config file was corrupt ({e.Message}); moved to {Path.GetFileName(bad)} and started with an empty config.");
        }
    }

    /// <summary>Atomic save: writes a temp file next to the target, then replaces the target.</summary>
    public void Save(AppConfig config)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}

/// <summary>Result of <see cref="ConfigStore.LoadOrRecover"/>; <see cref="Warning"/> is set when the file had to be replaced.</summary>
public record ConfigLoadResult(AppConfig Config, string? Warning);


