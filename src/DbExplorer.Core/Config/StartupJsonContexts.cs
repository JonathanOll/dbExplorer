using System.Text.Json.Serialization;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Config;

// Source-generated serialization of the files read at start-up: building the same metadata by reflection costs about
// 75 ms of the launch (some 800 JIT-ed methods). Anything else read before the window is usable belongs here too.

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppConfig))]
internal partial class ConfigJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SavedTabs))]
internal partial class SessionJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(SavedScript))]
internal partial class ScriptJsonContext : JsonSerializerContext;
