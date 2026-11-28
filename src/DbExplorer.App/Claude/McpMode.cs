using System.Reflection;
using System.Runtime.Versioning;
using DbExplorer.Core.Claude;
using DbExplorer.Core.Config;
using DbExplorer.Core.Tunnels;

namespace DbExplorer.App.Claude;

/// <summary>
/// <c>DbExplorer.exe --mcp</c>: the MCP server Claude Code starts, on stdin and stdout. It is the same exe, so publishing,
/// releases and updates carry it. No window and no prompt ever comes from this mode.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class McpMode
{
    public const string Flag = "--mcp";

    public static int Run(string[] args)
    {
        if (AppPaths.CheckArguments(args) is { } problem)
        {
            Console.Error.WriteLine(problem);
            return 2;
        }

        var paths = AppPaths.Resolve(args);
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        Console.SetOut(TextWriter.Null);   // stdout carries the protocol and nothing else

        var protector = new DpapiSecretProtector();
        var store = new ConfigStore(paths.ConfigFile, protector);
        using var database = new ClaudeDatabase(protector, KnownHostsStore.NextTo(store.FilePath));
        var tools = new ClaudeTools(store.Load, database, protector, new ClaudeActivityLog(paths.ClaudeActivityFile),
            new ClaudeBridge.Client(ClaudeBridge.PipeName(paths.DataDir)));
        var version = typeof(McpMode).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        try
        {
            ClaudeMcpServer.RunAsync(tools, input, output, version, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"dbExplorer MCP server stopped: {e.GetType().Name}");
            return 1;
        }
    }
}
