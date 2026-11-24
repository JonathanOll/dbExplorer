using System.Globalization;
using System.Reflection;

namespace DbExplorer.Core.Updates;

/// <summary>A semantic version (major.minor.patch[-prerelease]); build metadata is dropped.</summary>
public sealed class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    /// <summary>What an unstamped build reports (SDK default); treated as "unknown".</summary>
    private static readonly AppVersion DefaultStamp = new(1, 0, 0, null);

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    /// <summary>Dot-separated prerelease identifiers ("beta.1"), or null for a release.</summary>
    public string? Prerelease { get; }

    public AppVersion(int major, int minor, int patch, string? prerelease = null)
    {
        Major = major; Minor = minor; Patch = patch;
        Prerelease = string.IsNullOrEmpty(prerelease) ? null : prerelease;
    }

    /// <summary>Parses "v1.2.3", "1.2.3", "1.2.3+sha", "1.2.3-beta.1+sha". Never throws.</summary>
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        if (s[0] is 'v' or 'V') s = s[1..];

        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];

        string? pre = null;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (!ValidPrerelease(pre)) return false;
        }

        var parts = s.Split('.');
        if (parts.Length != 3) return false;
        if (!TryPart(parts[0], out var major) || !TryPart(parts[1], out var minor) || !TryPart(parts[2], out var patch))
            return false;

        version = new AppVersion(major, minor, patch, pre);
        return true;
    }

    public static AppVersion? ParseOrNull(string? text) => TryParse(text, out var v) ? v : null;

    private static bool TryPart(string s, out int value) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool ValidPrerelease(string pre) =>
        pre.Length > 0 && pre.Split('.').All(id => id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));

    /// <summary>
    /// The running app version, or null when unknown (dev build, unstamped "1.0.0", unparsable).
    /// Callers must never offer an update when this is null.
    /// </summary>
    public static AppVersion? Current { get; } = FromAssembly(Assembly.GetEntryAssembly());

    /// <summary>Reads the InformationalVersion of an assembly; null when unknown.</summary>
    public static AppVersion? FromAssembly(Assembly? assembly) =>
        FromInformational(assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Parses an InformationalVersion string; null when unknown (unparsable or the unstamped default).</summary>
    public static AppVersion? FromInformational(string? informational)
    {
        if (!TryParse(informational, out var v)) return null;
        return v.Equals(DefaultStamp) ? null : v;
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    // A prerelease sorts before the release it precedes; identifiers compare per semver 2.0.
    private static int ComparePrerelease(string? a, string? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return 1;
        if (b is null) return -1;

        var xs = a.Split('.');
        var ys = b.Split('.');
        for (var i = 0; i < Math.Min(xs.Length, ys.Length); i++)
        {
            var xNum = ulong.TryParse(xs[i], NumberStyles.None, CultureInfo.InvariantCulture, out var xn);
            var yNum = ulong.TryParse(ys[i], NumberStyles.None, CultureInfo.InvariantCulture, out var yn);
            int c;
            if (xNum && yNum) c = xn.CompareTo(yn);
            else if (xNum) c = -1;
            else if (yNum) c = 1;
            else c = string.CompareOrdinal(xs[i], ys[i]);
            if (c != 0) return c;
        }
        return xs.Length.CompareTo(ys.Length);
    }

    public bool Equals(AppVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => Equals(obj as AppVersion);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}
