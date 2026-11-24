namespace DbExplorer.Core.Model;

/// <summary>Application configuration. VirtualKeys: connection id -> "schema.table" -> key columns.</summary>
public record AppConfig(
    List<string> Folders,
    List<ConnectionConfig> Connections,
    Dictionary<Guid, Dictionary<string, string[]>> VirtualKeys)
{
    /// <summary>Connection groups. Init property (not positional) so old configs without it load with an empty list.</summary>
    public List<ConnectionGroup> Groups { get; init; } = new();

    /// <summary>Whether the app looks for a newer release in the background after start. Missing in old files: on.</summary>
    public bool AutoUpdateCheck { get; set; } = true;

    public static AppConfig Empty() => new(new(), new(), new());
}
