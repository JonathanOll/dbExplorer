using System.Text.Json;
using System.Text.Json.Serialization;
using DbExplorer.Core.Config;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Tests;

/// <summary>
/// The files read at start-up use source-generated serialization (reflection cost about 75 ms of the launch). These tests
/// pin it to what the reflection serializer did, above all for files that lack properties (older versions).
/// </summary>
public class StartupJsonTests
{
    private static readonly JsonSerializerOptions Reflection = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private const string Connection =
        """{"Id":"7d0f0b0e-0000-0000-0000-000000000001","Name":"a","Kind":"sqlite","Database":"a.db"}""";

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"Folders":[],"Connections":[],"VirtualKeys":{}}""")]
    [InlineData("""{"Settings":{},"Export":{},"Format":{}}""")]
    [InlineData("""{"Settings":{"Appearance":{},"Editor":{},"Results":{}},"Export":{"Copy":{}}}""")]
    [InlineData("""{"Connections":[""" + Connection + """],"Groups":[],"RecentCommands":["a"]}""")]
    [InlineData("""{"AutoUpdateCheck":false,"ShowFolders":false,"QuickOpenHidden":"Views, Columns","Format":{"IndentSize":2}}""")]
    [InlineData("""{"Settings":null,"RecentCommands":null,"Unknown":1}""")]
    public void Config_LoadsLikeTheReflectionSerializer(string json)
    {
        var expected = JsonSerializer.Deserialize<AppConfig>(json, Reflection)!;
        var actual = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig)!;

        Assert.Equal(JsonSerializer.Serialize(expected, Reflection), JsonSerializer.Serialize(actual, Reflection));
    }

    [Fact]
    public void Config_IsWrittenLikeTheReflectionSerializer()
    {
        var config = JsonSerializer.Deserialize<AppConfig>(
            """{"Folders":["f"],"Connections":[""" + Connection + """],"VirtualKeys":{}}""", Reflection)!;
        config.Connections.Add(config.Connections[0] with
        {
            Environment = DbEnvironment.Production,
            Ssh = new SshTunnelConfig("h", 22, "u", SshAuthKind.PrivateKey, KeyFile: "k"),
        });

        Assert.Equal(JsonSerializer.Serialize(config, Reflection),
            JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig));
    }

    [Fact]
    public void SessionFiles_ReadAndWriteLikeTheReflectionSerializer()
    {
        var plain = new JsonSerializerOptions { WriteIndented = true };
        const string tabs = """{"ActiveIndex":0,"Tabs":[{"Kind":"Script","ConnectionId":"7d0f0b0e-0000-0000-0000-000000000001"}]}""";
        var session = JsonSerializer.Deserialize(tabs, SessionJsonContext.Default.SavedTabs)!;
        Assert.Equal(JsonSerializer.Serialize(JsonSerializer.Deserialize<SavedTabs>(tabs, plain), plain),
            JsonSerializer.Serialize(session, SessionJsonContext.Default.SavedTabs));

        var script = new SavedScript(Guid.NewGuid(), Guid.NewGuid(), "select 'é' -- <x>", null);
        Assert.Equal(JsonSerializer.Serialize(script),
            JsonSerializer.Serialize(script, ScriptJsonContext.Default.SavedScript));
    }
}
