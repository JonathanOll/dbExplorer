using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Claude;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>What the Claude Code integration needs from the application; the window layer provides it, tests fake it.</summary>
/// <param name="ServerExe">The exe Claude Code starts with <c>--mcp</c>.</param>
/// <param name="ConfigFile">Where the MCP configuration of "Open in Claude Code" is written.</param>
/// <param name="LibraryDir">The folder the terminal opens in when no scripts repository is set.</param>
/// <param name="FindClaude">The claude executable, or null when it is not installed.</param>
/// <param name="Start">Starts the terminal.</param>
/// <param name="Dialogs">Shows the plain messages.</param>
/// <param name="Listen">Starts answering the MCP server; disposing stops. Null result: another instance answers already.</param>
/// <param name="IsWindowActive">The user is working in the app right now.</param>
public sealed record ClaudeHooks(
    string ServerExe,
    string DataDir,
    string ConfigFile,
    string LibraryDir,
    ClaudeActivityLog Log,
    Func<string?> FindClaude,
    Action<ProcessStartInfo> Start,
    IDialogService Dialogs,
    Func<Func<ClaudeAppRequest, CancellationToken, Task<ClaudeAppReply>>, IDisposable?>? Listen = null,
    Func<bool>? IsWindowActive = null);

/// <summary>The Claude Code integration in the app: what the MCP server may ask of it, and "Open in Claude Code".</summary>
public partial class MainViewModel
{
    /// <summary>Tabs proposed by Claude that may be open at once: a runaway session must not bury the user's work.</summary>
    internal const int MaxProposedTabs = 20;

    internal const string ProposedHeader = "-- Proposed by Claude Code. Review it before running: nothing was executed.\n";

    private ClaudeHooks? _claude;
    private IDisposable? _claudeListener;
    private readonly HashSet<Guid> _proposedTabs = [];

    /// <summary>Only stores the hooks (nothing at start-up); <see cref="SyncClaudeListener"/> starts listening.</summary>
    public void UseClaude(ClaudeHooks hooks) => _claude = hooks;

    /// <summary>Listens for the MCP server while the integration is on, and only then.</summary>
    public void SyncClaudeListener()
    {
        if (_claude?.Listen is not { } listen) return;
        if (!Tree.Config.Claude.Enabled)
        {
            _claudeListener?.Dispose();
            _claudeListener = null;
        }
        else
        {
            _claudeListener ??= listen(HandleOnUiThreadAsync);
        }
    }

    /// <summary>Runs a delegate on the UI thread; replaced by the window layer (tests run it in place).</summary>
    public Func<Func<ClaudeAppReply>, Task<ClaudeAppReply>> ClaudeDispatch { get; set; } = work => Task.FromResult(work());

    private Task<ClaudeAppReply> HandleOnUiThreadAsync(ClaudeAppRequest request, CancellationToken ct) =>
        ClaudeDispatch(() => HandleClaudeRequest(request));

    /// <summary>
    /// A request of the MCP server. Access is decided here again, from the settings in memory: what the other process says
    /// is not taken on trust.
    /// </summary>
    public ClaudeAppReply HandleClaudeRequest(ClaudeAppRequest request)
    {
        if (!Tree.Config.Claude.Enabled) return new ClaudeAppReply(false, ClaudePolicy.Disabled);
        return request.Op switch
        {
            ClaudeAppRequest.OpenScript => OpenProposedScript(request),
            ClaudeAppRequest.ActiveTab => ReadActiveTab(),
            _ => new ClaudeAppReply(false, "Unknown request."),
        };
    }

    private ClaudeAppReply OpenProposedScript(ClaudeAppRequest request)
    {
        var connection = Tree.Config.Connections.FirstOrDefault(c => c.Id == request.ConnectionId);
        if (connection is null) return new ClaudeAppReply(false, "This connection no longer exists.");
        if (ClaudePolicy.For(Tree.Config, connection) is { Allowed: false } refused) return new ClaudeAppReply(false, refused.Refusal);
        if (string.IsNullOrWhiteSpace(request.Sql) || request.Sql.Length > 100_000) return new ClaudeAppReply(false, "The SQL is empty or too long.");

        _proposedTabs.IntersectWith(Tabs.OfType<ScriptTabViewModel>().Select(t => t.Id));
        if (_proposedTabs.Count >= MaxProposedTabs)
            return new ClaudeAppReply(false, $"{MaxProposedTabs} proposed tabs are open already: the user has to close some.");

        var title = Visible(request.Title ?? "", keepLines: false).Trim();
        if (title.Length > 60) title = title[..60];
        var tab = CreateScriptTab(connection, null);
        tab.NameProposal(title.Length == 0 ? "Claude" : "Claude: " + title);
        tab.Text = ProposedHeader + Visible(request.Sql, keepLines: true);
        _proposedTabs.Add(tab.Id);
        Tabs.Add(tab);
        // While the user types here, the tab stays behind: an F5 meant for their script must not run this one.
        if (ActiveTab is null || _claude?.IsWindowActive?.Invoke() != true) ActiveTab = tab;
        Status.Info($"Claude Code opened a script tab on {connection.Name}. Nothing was executed.");
        return new ClaudeAppReply(true);
    }

    /// <summary>
    /// What the editor shows is what would run: line breaks the engines do not see become real ones, and characters that
    /// hide or reorder text are removed.
    /// </summary>
    internal static string Visible(string text, bool keepLines)
    {
        text = text.ReplaceLineEndings("\n");
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\n' or '\u2028' or '\u2029' or '\u0085') clean.Append(keepLines ? '\n' : ' ');
            else if (c == '\t') clean.Append(c);
            else if (char.IsControl(c)) clean.Append(' ');
            // Bidirectional controls and invisible characters ("Trojan source").
            else if (c is not (>= '\u200B' and <= '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2060' and <= '\u2069' or '\u061C' or '\uFEFF'))
                clean.Append(c);
        }
        return clean.ToString();
    }

    private ClaudeAppReply ReadActiveTab()
    {
        if (ActiveTab is not ScriptTabViewModel tab) return new ClaudeAppReply(false, "The active tab is not a script tab.");
        var connection = Tree.Config.Connections.FirstOrDefault(c => c.Id == tab.Connection.Id);
        if (connection is null) return new ClaudeAppReply(false, "The connection of the active tab no longer exists.");
        var grant = ClaudePolicy.For(Tree.Config, connection);
        if (!grant.Allowed) return new ClaudeAppReply(false, "The connection of the active tab is not allowed for Claude Code.");

        var (error, note) = tab.ErrorToShare;
        // An error message can quote values of the tables.
        if (error is not null && !grant.CanReadData)
            (error, note) = (null, "The last run failed. Its message is not shared: it may quote data, and this connection is schema only.");
        return new ClaudeAppReply(true, Tab: new ClaudeActiveTab(
            connection.Id, connection.Name, tab.Title.TrimStart('•', ' '), tab.Text, error, note));
    }

    // ---- Open in Claude Code ----

    [RelayCommand]
    private async Task OpenInClaudeCodeAsync()
    {
        if (_claude is not { } claude) return;
        try
        {
            if (!Tree.Config.Claude.Enabled)
            {
                if (await claude.Dialogs.ConfirmAsync(new ConfirmRequest("Open in Claude Code",
                        "The Claude Code integration is turned off.", "Open settings",
                        "Turn it on and choose the connections Claude Code may use, then try again.", DialogTone.Info)))
                    await OpenOptionsAsync("claude");
                return;
            }

            var repository = Tree.Config.DbUpdatePath;
            // Disk and PATH are read off the UI thread: the repository may be on a share.
            var (executable, folder) = await Task.Run(() => (claude.FindClaude(), ScriptsFolder(repository, claude.LibraryDir)));
            if (executable is null)
            {
                await claude.Dialogs.ConfirmAsync(new ConfirmRequest("Claude Code not found", ClaudeLauncher.NotInstalled, "OK",
                    Tone: DialogTone.Info));
                return;
            }

            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(claude.ConfigFile)!);
                File.WriteAllText(claude.ConfigFile, ClaudeLauncher.McpConfig(claude.ServerExe, claude.DataDir));
            });
            SyncClaudeListener();
            claude.Start(ClaudeLauncher.Terminal(executable, claude.ConfigFile, folder));
            Status.Info($"Claude Code is starting in {folder}.");
        }
        catch (Exception e)
        {
            Status.Error($"Cannot open Claude Code: {ErrorText.Describe(e)}");
        }
    }

    /// <summary>
    /// The scripts repository: the Git repository that holds the dbupdate folder (that folder without one). Without a
    /// dbupdate folder, the script library.
    /// </summary>
    internal static string ScriptsFolder(string? dbUpdatePath, string libraryDir)
    {
        if (!string.IsNullOrWhiteSpace(dbUpdatePath) && Directory.Exists(dbUpdatePath))
        {
            var folder = Path.GetFullPath(dbUpdatePath);
            for (var at = new DirectoryInfo(folder); at is not null; at = at.Parent)
                if (Path.Exists(Path.Combine(at.FullName, ".git"))) return at.FullName;
            return folder;
        }
        Directory.CreateDirectory(libraryDir);
        return libraryDir;
    }

    private ClaudeSettingsAccess? ClaudeSettingsAccess => _claude is not { } claude ? null : new(
        () => Tree.Config.Claude,
        () => Tree.Config.Connections.ToList(),
        settings =>
        {
            var saved = Tree.SetClaude(settings);
            SyncClaudeListener();
            return saved;
        },
        claude.Log,
        ClaudeLauncher.AddCommand(claude.ServerExe, claude.DataDir));
}
