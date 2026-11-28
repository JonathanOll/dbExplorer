using DbExplorer.Core.Claude;
using DbExplorer.Core.Claude.Assist;
using DbExplorer.Core.Config;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// What the questions to Claude Code (side panel) need from the application: the current settings, which decide what may
/// be sent, and the way to run <c>claude -p</c>.
/// </summary>
/// <param name="config">Read at every use, so revoking access applies at once.</param>
/// <param name="save">Stores the Claude settings (the accepted previews).</param>
/// <param name="findClaude">The claude executable, or null; may touch the disk, so it is called off the UI thread.</param>
/// <param name="workDirectory">An empty folder claude runs in.</param>
public sealed class ClaudeAssistant(
    Func<AppConfig> config, Func<ClaudeSettings, bool> save, Func<string?> findClaude, IClaudeProcessRunner runner,
    string workDirectory, ClaudeActivityLog log, ISecretProtector protector)
{
    public bool Enabled => config().Claude.Enabled;

    /// <summary>
    /// What may be sent about these connections together: the lowest of their levels, and nothing when one of them is not
    /// allowed.
    /// </summary>
    public ClaudeGrant GrantFor(IReadOnlyList<Guid> connections)
    {
        var current = config();
        if (!current.Claude.Enabled) return new ClaudeGrant(ClaudeAccess.None, ClaudePolicy.Disabled);
        var access = ClaudeAccess.SchemaAndData;
        foreach (var id in connections)
        {
            var connection = current.Connections.FirstOrDefault(c => c.Id == id);
            if (connection is null) return new ClaudeGrant(ClaudeAccess.None, "This connection no longer exists.");
            var grant = ClaudePolicy.For(current, connection);
            if (!grant.Allowed)
                return connections.Count == 1 ? grant : new ClaudeGrant(ClaudeAccess.None, $"{connection.Name}: {grant.Refusal}");
            if (grant.Access < access) access = grant.Access;
        }
        return connections.Count == 0 ? new ClaudeGrant(ClaudeAccess.None, "There is no connection.") : new ClaudeGrant(access, null);
    }

    public string NameOf(Guid connection) => config().Connections.FirstOrDefault(c => c.Id == connection)?.Name ?? "?";

    /// <summary>The user has not yet sent a question about one of them at its present level.</summary>
    public bool NeedsPreview(IReadOnlyList<Guid> connections)
    {
        var claude = config().Claude;
        return connections.Any(id => !claude.Previewed.TryGetValue(id, out var seen) || seen != claude.Of(id));
    }

    public void MarkPreviewed(IReadOnlyList<Guid> connections)
    {
        var claude = config().Claude;
        var seen = new Dictionary<Guid, ClaudeAccess>(claude.Previewed);
        foreach (var id in connections) seen[id] = claude.Of(id);
        save(claude with { Previewed = seen });
    }

    /// <summary>The payload with every stored secret masked: what is shown and what is sent are this one text.</summary>
    public ClaudePayload Scrubbed(ClaudePayload payload) =>
        payload with { Prompt = SecretScrubber.Scrub(payload.Prompt, config(), protector) };

    /// <summary>Notes the question in the activity log of the Settings page. Throws when it cannot: then nothing is sent.</summary>
    public void Record(ClaudeTask task, IReadOnlyList<Guid> connections, ClaudeAccess access, string outcome, string? message = null) =>
        log.Append(new ClaudeActivity(DateTimeOffset.Now, "ask:" + task, string.Join(", ", connections.Select(NameOf)),
            $"access {(access == ClaudeAccess.SchemaAndData ? "schema and data" : "schema only")}", outcome,
            message is null ? null : SecretScrubber.Scrub(message, config(), protector)));

    /// <summary>Runs one question off the calling thread. <paramref name="progress"/> is called on a pool thread.</summary>
    public Task<ClaudeAnswer> AskAsync(ClaudePayload payload, Action<ClaudeUpdate> progress, CancellationToken ct) =>
        Task.Run(() => ClaudeHeadless.AskAsync(runner, findClaude(), workDirectory, payload.Prompt, progress, ct), ct);
}
