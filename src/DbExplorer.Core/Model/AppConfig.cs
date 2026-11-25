using DbExplorer.Core.Scripting;

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

    /// <summary>View menu: the navigator shows folder nodes. Off: every connection is listed flat. Missing in old files: on.</summary>
    public bool ShowFolders { get; set; } = true;

    /// <summary>View menu: the navigator shows the Groups section. Missing in old files: on.</summary>
    public bool ShowGroups { get; set; } = true;

    /// <summary>View menu: the navigator shows the Scripts section (the script library). Missing in old files: on.</summary>
    public bool ShowScripts { get; set; } = true;

    /// <summary>Folder with the n2f dbupdate scripts; null switches the upgrade feature off. Missing in old files: null.</summary>
    public string? DbUpdatePath { get; set; }

    /// <summary>Whether the statements run from script tabs are recorded in the query history. Missing in old files: on.</summary>
    public bool RecordHistory { get; set; } = true;

    /// <summary>SQL formatter style (Options dialog). Missing in old files: the defaults.</summary>
    public SqlFormatOptions Format { get; set; } = new();

    /// <summary>Settings dialog values (appearance, editor, results...). Missing in old files: the defaults.</summary>
    public AppSettings Settings { get; set; } = new();

    public static AppConfig Empty() => new(new(), new(), new());
}
