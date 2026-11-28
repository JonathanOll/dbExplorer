using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DbExplorer.Core.Claude;

/// <summary>One call Claude Code made. <see cref="Detail"/> is the request (SQL, object name), never any returned data.</summary>
/// <param name="Outcome">"ok", "refused" or "error".</param>
public sealed record ClaudeActivity(DateTimeOffset Time, string Tool, string? Connection, string? Detail, string Outcome, string? Message = null);

/// <summary>
/// The activity log shown in Settings: one JSON line per call, written by the MCP server processes and read by the app.
/// Past <see cref="MaxBytes"/> the file is moved to <c>.old</c> and a new one is started.
/// </summary>
public sealed class ClaudeActivityLog(string path)
{
    public const long MaxBytes = 1_000_000;
    public const int MaxTextLength = 600;

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string FilePath => path;

    private string OldPath => path + ".old";

    /// <summary>Appends the entry. Throws when it cannot be written: the caller then gives out nothing.</summary>
    public void Append(ClaudeActivity entry)
    {
        entry = entry with { Connection = Cut(entry.Connection), Detail = Cut(entry.Detail), Message = Cut(entry.Message) };
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json) + "\n");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, OldPath, overwrite: true);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(line);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(10);   // another server process is writing
            }
        }
    }

    /// <summary>The latest entries, newest first. Unreadable lines are skipped.</summary>
    public IReadOnlyList<ClaudeActivity> ReadLatest(int max)
    {
        var entries = ReadFile(path);
        if (entries.Count < max) entries.InsertRange(0, ReadFile(OldPath));
        entries.Reverse();
        return entries.Take(max).ToList();
    }

    public void Clear()
    {
        File.Delete(path);
        File.Delete(OldPath);
    }

    private static List<ClaudeActivity> ReadFile(string file)
    {
        var entries = new List<ClaudeActivity>();
        if (!File.Exists(file)) return entries;
        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (reader.ReadLine() is { } line)
        {
            try
            {
                if (JsonSerializer.Deserialize<ClaudeActivity>(line, Json) is { Tool: not null, Outcome: not null } entry) entries.Add(entry);
            }
            catch (JsonException) { /* a line cut short by a killed process */ }
        }
        return entries;
    }

    private static string? Cut(string? text) =>
        text is { Length: > MaxTextLength } ? text[..MaxTextLength] + "…" : text;
}
