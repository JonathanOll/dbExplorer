using System.Text.Json;

namespace DbExplorer.Core.Tunnels;

/// <summary>The identity an SSH server presented.</summary>
/// <param name="Fingerprint">"SHA256:..." of the host key, as <c>ssh-keygen -lf</c> prints it.</param>
public sealed record HostKeyInfo(string Host, int Port, string Algorithm, string Fingerprint);

/// <summary>How a presented host key compares with the stored one.</summary>
public enum HostKeyStatus { Unknown, Trusted, Changed }

/// <summary>
/// The SSH host keys the user accepted (trust on first use), one fingerprint per host and port, in a JSON file of the data
/// folder. A file that cannot be read is an error, never an empty list: forgetting the keys would accept any server again.
/// </summary>
public sealed class KnownHostsStore(string path)
{
    public const string FileName = "ssh_known_hosts.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public string FilePath => path;

    public static KnownHostsStore NextTo(string configPath) =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, FileName));

    public HostKeyStatus Check(HostKeyInfo key) =>
        Find(key.Host, key.Port) is not { } known ? HostKeyStatus.Unknown
        : string.Equals(known.Fingerprint, key.Fingerprint, StringComparison.Ordinal) ? HostKeyStatus.Trusted
        : HostKeyStatus.Changed;

    public HostKeyInfo? Find(string host, int port)
    {
        lock (Gate) return Load().FirstOrDefault(k => Same(k, host, port));
    }

    /// <summary>Stores the key of a host that has none yet; a stored key is never replaced.</summary>
    public void Add(HostKeyInfo key)
    {
        lock (Gate)
        {
            var keys = Load();
            if (keys.Any(k => Same(k, key.Host, key.Port))) return;
            keys.Add(key);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(keys, Options));
            File.Move(tmp, path, overwrite: true);
        }
    }

    private static bool Same(HostKeyInfo k, string host, int port) =>
        k.Port == port && string.Equals(k.Host, host, StringComparison.OrdinalIgnoreCase);

    private List<HostKeyInfo> Load()
    {
        if (!File.Exists(path)) return [];
        var keys = JsonSerializer.Deserialize<List<HostKeyInfo>>(File.ReadAllText(path), Options);
        if (keys is null || keys.Any(k => k?.Host is null || k.Fingerprint is null))
            throw new JsonException($"{path} contains invalid entries.");
        return keys;
    }
}
