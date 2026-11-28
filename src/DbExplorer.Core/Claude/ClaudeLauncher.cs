using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbExplorer.Core.Claude;

/// <summary>
/// Starts Claude Code in a terminal with the dbExplorer MCP server added for that session only
/// (<c>--mcp-config</c>): the user's own Claude configuration is never touched.
/// </summary>
public static class ClaudeLauncher
{
    private static readonly string[] Names = ["claude.exe", "claude.cmd", "claude.bat"];

    public const string NotInstalled =
        "Claude Code is not installed: the 'claude' command was not found. Install it (https://code.claude.com), then try again.";

    /// <summary>The claude executable on the PATH, or where its installer puts it; null when it is not installed.</summary>
    public static string? FindClaude(string? path, string userProfile, Func<string, bool> exists)
    {
        var folders = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.Trim('"'))
            // A relative entry would find a claude.exe planted in the scripts folder.
            .Where(Path.IsPathFullyQualified)
            .Append(Path.Combine(userProfile, ".local", "bin"));
        return folders.SelectMany(f => Names.Select(n => Path.Combine(f, n))).FirstOrDefault(exists);
    }

    /// <summary>The MCP configuration file of a session: how to start the server, and nothing secret.</summary>
    public static string McpConfig(string serverExe, string dataDirectory) => new JsonObject
    {
        ["mcpServers"] = new JsonObject
        {
            [ClaudeMcpServer.Name] = new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = serverExe,
                ["args"] = new JsonArray("--mcp", "--data-dir", dataDirectory),
            },
        },
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>The command a user can run to add the server to their own sessions (Settings shows it).</summary>
    public static string AddCommand(string serverExe, string dataDirectory) =>
        $"claude mcp add --scope user {ClaudeMcpServer.Name} -- \"{serverExe}\" --mcp --data-dir \"{dataDirectory}\"";

    /// <summary>
    /// A new PowerShell window, left open, that runs claude in <paramref name="workingDirectory"/>. The command is passed
    /// encoded, so no character of a path is read as syntax.
    /// </summary>
    public static ProcessStartInfo Terminal(string claude, string configFile, string workingDirectory)
    {
        var command = $"& {Literal(claude)} --mcp-config {Literal(configFile)}";
        return new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command)),
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,   // its own window
        };
    }

    // A single-quoted PowerShell string; PowerShell also ends one at the typographic quotes.
    internal static string Literal(string text)
    {
        var literal = new StringBuilder("'");
        foreach (var c in text)
        {
            literal.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛') literal.Append(c);
        }
        return literal.Append('\'').ToString();
    }
}
