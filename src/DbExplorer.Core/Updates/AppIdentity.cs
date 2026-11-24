using System.Reflection;

namespace DbExplorer.Core.Updates;

/// <summary>What the exe says about itself at build time: its version and the GitHub repo (owner/name) to update from.</summary>
/// <param name="Version">Null when unknown (dev build): never offer an update then.</param>
/// <param name="Repository">Null when missing or not a valid owner/name.</param>
public sealed record AppIdentity(AppVersion? Version, string? Repository)
{
    public const string RepositoryMetadataKey = "GitHubRepository";

    /// <summary>Identity of the entry assembly.</summary>
    public static AppIdentity Current { get; } = FromAssembly(Assembly.GetEntryAssembly());

    public static AppIdentity FromAssembly(Assembly? assembly)
    {
        var info = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var repo = assembly?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == RepositoryMetadataKey)?.Value;
        return FromValues(info, repo);
    }

    /// <summary>Builds an identity from raw stamped values (testable without an assembly).</summary>
    public static AppIdentity FromValues(string? informationalVersion, string? repository) =>
        new(AppVersion.FromInformational(informationalVersion), NormalizeRepository(repository));

    /// <summary>Returns "owner/name" when valid (GitHub charset, exactly one slash), else null.</summary>
    public static string? NormalizeRepository(string? repository)
    {
        var s = repository?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        var parts = s.Split('/');
        if (parts.Length != 2) return null;
        return parts.All(IsValidPart) ? s : null;
    }

    private static bool IsValidPart(string p) =>
        p.Length is > 0 and <= 100 && p != "." && p != ".." && p[0] != '-'
        && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
