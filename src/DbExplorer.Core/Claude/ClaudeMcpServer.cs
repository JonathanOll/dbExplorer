using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DbExplorer.Core.Claude;

/// <summary>The Model Context Protocol face of <see cref="ClaudeTools"/>, over a pair of streams (stdin and stdout).</summary>
public static class ClaudeMcpServer
{
    public const string Name = "dbexplorer";

    /// <summary>Serves until the client closes the input or <paramref name="ct"/> is cancelled.</summary>
    public static async Task RunAsync(ClaudeTools tools, Stream input, Stream output, string version, CancellationToken ct)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = Name, Title = "dbExplorer", Version = version },
            ServerInstructions = ClaudeToolCatalog.Instructions,
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = ClaudeToolCatalog.All.Select(Tool).ToList() }),
                CallToolHandler = async (request, token) =>
                {
                    var arguments = request.Params?.Arguments is { } given
                        ? new Dictionary<string, JsonElement>(given)
                        : new Dictionary<string, JsonElement>();
                    var reply = await tools.CallAsync(request.Params?.Name ?? "", arguments, token).ConfigureAwait(false);
                    return new CallToolResult { Content = [new TextContentBlock { Text = reply.Text }], IsError = reply.IsError };
                },
            },
        };
        await using var transport = new StreamServerTransport(input, output, Name);
        await using var server = McpServer.Create(transport, options);
        await server.RunAsync(ct).ConfigureAwait(false);
    }

    private static Tool Tool(ClaudeToolDefinition definition)
    {
        using var schema = JsonDocument.Parse(definition.InputSchema);
        return new Tool
        {
            Name = definition.Name,
            Title = definition.Title,
            Description = definition.Description,
            InputSchema = schema.RootElement.Clone(),
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = definition.ReadOnly, DestructiveHint = false, OpenWorldHint = false,
            },
        };
    }
}
