using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbExplorer.Core.Updates;

/// <summary>
/// <see cref="IGhRunner"/> that needs no gh: it understands the two gh calls the updater makes
/// (<c>release view</c> and <c>release download</c>) and answers them with plain HTTPS to the GitHub REST API.
/// The answer of <c>release view</c> is shaped like gh's JSON (tagName, isDraft, isPrerelease, assets) so
/// <see cref="UpdateChecker"/> and <see cref="UpdateDownloader"/> stay unchanged. Never throws.
/// GITHUB_TOKEN / GH_TOKEN (when set) lifts the 60 requests/hour limit and allows private repositories.
/// </summary>
public sealed class GitHubHttpRunner : IGhRunner
{
    private const string ApiHost = "api.github.com";
    private const int MaxRedirects = 5;

    private readonly HttpClient _http;
    private readonly Func<string, string?> _env;

    /// <param name="handler">Test seam. The default handler never follows redirects itself (this class does, so the token never leaves api.github.com).</param>
    /// <param name="env">Test seam for environment variables.</param>
    public GitHubHttpRunner(HttpMessageHandler? handler = null, Func<string, string?>? env = null)
    {
        _env = env ?? Environment.GetEnvironmentVariable;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan, // each call has its own timeout
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("dbExplorer-updater");
    }

    public async Task<GhResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            if (args.Count >= 2 && args[0] == "release" && args[1] == "view") return await ViewAsync(args, linked.Token).ConfigureAwait(false);
            if (args.Count >= 3 && args[0] == "release" && args[1] == "download") return await DownloadAsync(args, linked.Token).ConfigureAwait(false);
            return GhResult.Fail(2, "unsupported request");
        }
        catch (OperationCanceledException)
        {
            return new GhResult(ct.IsCancellationRequested ? GhRunStatus.Cancelled : GhRunStatus.TimedOut, -1, "", "");
        }
        catch (HttpRequestException ex)
        {
            return GhResult.Fail(1, "GitHub is not reachable (network error): " + ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NotSupportedException)
        {
            return GhResult.Fail(1, ex.Message);
        }
    }

    private async Task<GhResult> ViewAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        // release view [TAG] --repo owner/name ...
        var repo = Option(args, "--repo");
        if (repo is null) return GhResult.Fail(2, "missing repository");
        var tag = args.Count > 2 && !args[2].StartsWith('-') ? args[2] : null;
        var path = tag is null ? $"repos/{repo}/releases/latest" : $"repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}";

        using var resp = await SendAsync("https://" + ApiHost + "/" + path, "application/vnd.github+json", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return GhResult.Fail(1, await DescribeFailureAsync(resp, tag is not null, ct).ConfigureAwait(false));

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var root = JsonNode.Parse(json) as JsonObject;
        if (root is null) return GhResult.Fail(1, "GitHub answered with something that is not a release");

        var assets = new JsonArray();
        if (root["assets"] is JsonArray src)
        {
            foreach (var a in src.OfType<JsonObject>())
            {
                assets.Add(new JsonObject
                {
                    ["name"] = a["name"]?.DeepClone(),
                    ["size"] = a["size"]?.DeepClone(),
                    ["digest"] = a["digest"]?.DeepClone(),
                    ["url"] = a["url"]?.DeepClone(),
                    ["browser_download_url"] = a["browser_download_url"]?.DeepClone(),
                });
            }
        }
        var shaped = new JsonObject
        {
            ["tagName"] = root["tag_name"]?.DeepClone(),
            ["isDraft"] = root["draft"]?.DeepClone() ?? false,
            ["isPrerelease"] = root["prerelease"]?.DeepClone() ?? false,
            ["assets"] = assets,
        };
        return GhResult.Ok(shaped.ToJsonString());
    }

    private async Task<GhResult> DownloadAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        // release download TAG --repo owner/name --pattern NAME --dir DIR --clobber
        var tag = args[2];
        var repo = Option(args, "--repo");
        var name = Option(args, "--pattern");
        var dir = Option(args, "--dir");
        if (repo is null || name is null || dir is null) return GhResult.Fail(2, "missing argument");
        if (name != Path.GetFileName(name)) return GhResult.Fail(2, "invalid asset name");

        using var rel = await SendAsync($"https://{ApiHost}/repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}", "application/vnd.github+json", ct).ConfigureAwait(false);
        if (!rel.IsSuccessStatusCode) return GhResult.Fail(1, await DescribeFailureAsync(rel, true, ct).ConfigureAwait(false));
        var root = JsonNode.Parse(await rel.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
        var asset = (root?["assets"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(a => (string?)a["name"] == name);
        if (asset is null) return GhResult.Fail(1, $"release not found: asset {name} is not in release {tag}");

        // With a token (private repository) the API asset url is the one that accepts it; without, the public url avoids the API quota.
        var browserUrl = (string?)asset["browser_download_url"];
        var apiUrl = (string?)asset["url"];
        var url = HasToken() ? apiUrl ?? browserUrl : browserUrl ?? apiUrl;
        if (url is null) return GhResult.Fail(1, $"release not found: no download url for {name}");

        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        using var resp = await SendAsync(url, "application/octet-stream", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return GhResult.Fail(1, await DescribeFailureAsync(resp, true, ct).ConfigureAwait(false));
        try
        {
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: true))
            await using (var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            {
                await body.CopyToAsync(file, 81920, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        return GhResult.Ok();
    }

    private bool HasToken() => Token() is not null;

    private string? Token()
    {
        foreach (var n in new[] { "GITHUB_TOKEN", "GH_TOKEN" })
        {
            var v = _env(n);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }

    /// <summary>GET following redirects by hand: the token is only ever sent to api.github.com.</summary>
    private async Task<HttpResponseMessage> SendAsync(string url, string accept, CancellationToken ct)
    {
        var token = Token();
        for (var hop = 0; ; hop++)
        {
            var uri = new Uri(url);
            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Accept.ParseAdd(accept);
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            if (token is not null && uri.Host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && resp.Headers.Location is { } loc && hop < MaxRedirects)
            {
                url = new Uri(uri, loc).ToString();
                resp.Dispose();
                continue;
            }
            return resp;
        }
    }

    private async Task<string> DescribeFailureAsync(HttpResponseMessage resp, bool tagged, CancellationToken ct)
    {
        string body = "";
        try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch (Exception ex) when (ex is HttpRequestException or IOException) { }
        var remaining = resp.Headers.TryGetValues("X-RateLimit-Remaining", out var r) ? r.FirstOrDefault() : null;
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests &&
            (remaining == "0" || body.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || resp.StatusCode == HttpStatusCode.TooManyRequests))
            return "GitHub rate limit reached. Set the GITHUB_TOKEN environment variable (a personal access token) or retry later.";
        return resp.StatusCode switch
        {
            HttpStatusCode.NotFound => tagged ? "release not found (HTTP 404)" : "no release found (HTTP 404): the repository has no release, or it is private (set GITHUB_TOKEN)",
            HttpStatusCode.Unauthorized => "GitHub rejected the token (HTTP 401): check GITHUB_TOKEN",
            HttpStatusCode.Forbidden => "GitHub refused the request (HTTP 403)",
            _ => $"GitHub answered HTTP {(int)resp.StatusCode}",
        };
    }

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}

/// <summary>Tries the HTTP runner first; when it fails (not cancelled) and gh is installed, asks gh. The HTTP failure wins when gh is missing.</summary>
public sealed class FallbackGhRunner(IGhRunner primary, IGhRunner fallback) : IGhRunner
{
    public async Task<GhResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var first = await primary.RunAsync(args, timeout, ct).ConfigureAwait(false);
        if (first.Succeeded || first.Status == GhRunStatus.Cancelled || ct.IsCancellationRequested) return first;
        var second = await fallback.RunAsync(args, timeout, ct).ConfigureAwait(false);
        return second.Status == GhRunStatus.NotInstalled ? first : second;
    }
}
