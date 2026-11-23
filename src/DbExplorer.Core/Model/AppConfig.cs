namespace DbExplorer.Core.Model;

/// <summary>Application configuration. VirtualKeys: connection id -> "schema.table" -> key columns.</summary>
public record AppConfig(
    List<string> Folders,
    List<ConnectionConfig> Connections,
    Dictionary<Guid, Dictionary<string, string[]>> VirtualKeys)
{
    public static AppConfig Empty() => new(new(), new(), new());
}
