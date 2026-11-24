using System.Text.Json;

namespace DbExplorer.Core.Updates;

/// <summary>A published release the app could update to.</summary>
/// <param name="Repository">owner/name the release belongs to (used for the download).</param>
/// <param name="Sha256">Lower-case hex from the GitHub asset digest; null when GitHub gave none.</param>
public sealed record ReleaseInfo(string Repository, string Tag, AppVersion Version, string AssetName, long Size, string? Sha256);

public enum UnavailableReason
{
    /// <summary>gh is not installed / not on PATH.</summary>
    GhMissing,
    /// <summary>gh failed (not logged in, offline, repo or release not reachable, timeout).</summary>
    NotAuthenticatedOrNetwork,
    /// <summary>gh answered but the JSON was not what we expect.</summary>
    BadResponse,
    /// <summary>The running version is unknown (dev build): never offer an update.</summary>
    DevBuild,
    /// <summary>The exe carries no valid owner/name repository.</summary>
    NoRepoConfigured,
    /// <summary>The caller cancelled.</summary>
    Cancelled,
}

public abstract record UpdateCheckResult
{
    private UpdateCheckResult() { }

    public sealed record UpToDate(AppVersion Current, AppVersion? Latest) : UpdateCheckResult;
    public sealed record UpdateAvailable(ReleaseInfo Release) : UpdateCheckResult;
    public sealed record Unavailable(UnavailableReason Reason, string? Detail = null) : UpdateCheckResult;
}

/// <summary>Asks GitHub (through the gh CLI) whether a newer release exists. Never throws.</summary>
public sealed class UpdateChecker
{
    /// <summary>Name of the zip asset the release workflow publishes.</summary>
    public const string DefaultAssetName = "DbExplorer.zip";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly IGhRunner _gh;

    public UpdateChecker(IGhRunner gh) => _gh = gh;

    public Task<UpdateCheckResult> CheckAsync(AppIdentity identity, CancellationToken ct = default) =>
        CheckAsync(identity.Version, identity.Repository, ct);

    public async Task<UpdateCheckResult> CheckAsync(AppVersion? current, string? repository, CancellationToken ct = default)
    {
        try
        {
            if (current is null) return new UpdateCheckResult.Unavailable(UnavailableReason.DevBuild);
            var repo = AppIdentity.NormalizeRepository(repository);
            if (repo is null) return new UpdateCheckResult.Unavailable(UnavailableReason.NoRepoConfigured);

            var run = await _gh.RunAsync(
                ["release", "view", "--repo", repo, "--json", "tagName,isDraft,isPrerelease,assets"], Timeout, ct).ConfigureAwait(false);

            switch (run.Status)
            {
                case GhRunStatus.NotInstalled: return new UpdateCheckResult.Unavailable(UnavailableReason.GhMissing);
                case GhRunStatus.Cancelled: return new UpdateCheckResult.Unavailable(UnavailableReason.Cancelled);
                case GhRunStatus.TimedOut: return new UpdateCheckResult.Unavailable(UnavailableReason.NotAuthenticatedOrNetwork, "gh timed out");
            }
            if (run.ExitCode != 0)
                return new UpdateCheckResult.Unavailable(UnavailableReason.NotAuthenticatedOrNetwork, FirstLine(run.StdErr));

            return Evaluate(current, repo, run.StdOut);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult.Unavailable(UnavailableReason.Cancelled);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult.Unavailable(UnavailableReason.BadResponse, ex.Message);
        }
    }

    private static UpdateCheckResult Evaluate(AppVersion current, string repo, string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return Bad("not JSON"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Bad("not an object");

            var tag = Str(root, "tagName");
            var latest = AppVersion.ParseOrNull(tag);
            if (tag is null || latest is null) return Bad("missing or invalid tagName");

            // gh's "latest" is never a draft/prerelease; stay safe if it ever says otherwise.
            if (Bool(root, "isDraft") || Bool(root, "isPrerelease"))
                return new UpdateCheckResult.UpToDate(current, null);

            if (latest <= current) return new UpdateCheckResult.UpToDate(current, latest);

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return Bad("missing assets");

            JsonElement? pick = null;
            foreach (var a in assets.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                var name = Str(a, "name");
                if (name is null) continue;
                if (string.Equals(name, DefaultAssetName, StringComparison.OrdinalIgnoreCase)) { pick = a; break; }
                if (pick is null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) pick = a;
            }
            if (pick is not { } asset) return Bad("no zip asset");

            var assetName = Str(asset, "name")!;
            long size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : 0;
            return new UpdateCheckResult.UpdateAvailable(
                new ReleaseInfo(repo, tag, latest, assetName, size, ParseDigest(Str(asset, "digest"))));
        }
    }

    /// <summary>"sha256:ABC..." -> "abc..."; null when absent, another algorithm, or malformed.</summary>
    internal static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..].Trim().ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    private static UpdateCheckResult Bad(string detail) => new UpdateCheckResult.Unavailable(UnavailableReason.BadResponse, detail);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
