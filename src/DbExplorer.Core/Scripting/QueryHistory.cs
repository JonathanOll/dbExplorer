using System.Text.Json;
using System.Text.Json.Serialization;
using DbExplorer.Core.Model;
using DbExplorer.Core.Plans;

namespace DbExplorer.Core.Scripting;

/// <summary>One statement that ran from a script tab.</summary>
/// <param name="Rows">Rows returned (all result sets) or affected; null when the provider reports none.</param>
/// <param name="Error">Null on success; the failure (or the read-only refusal) otherwise.</param>
public sealed record HistoryEntry(
    Guid Id,
    DateTime At,
    Guid ConnectionId,
    string ConnectionName,
    DbKind Kind,
    string Database,
    string Sql,
    long DurationMs,
    long? Rows,
    string? Error,
    PlanMode Plan = PlanMode.None);

/// <summary>
/// The statements run from script tabs: an append-only JSON-lines file, read lazily (nothing is read at start-up; an append to a
/// file that was not read yet only appends a line). Keeps at most <see cref="MaxEntries"/> (the file is compacted, atomically,
/// when it grows past the cap) and cuts statements longer than <see cref="MaxSqlLength"/>. A corrupt line is skipped and the
/// file is never deleted because of it. Thread-safe; appends are written off the caller's thread (see <see cref="Append"/>). Never throws: an unwritable file only means the history is not remembered.
/// </summary>
public sealed class QueryHistory(string file, int maxEntries = QueryHistory.MaxEntries, int maxSqlLength = QueryHistory.MaxSqlLength)
{
    public const int MaxEntries = 5000;
    public const int MaxSqlLength = 20_000;

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly object _gate = new();
    private List<HistoryEntry>? _items;   // oldest first; null until something needs the content
    private int _appendedUnread;
    private int _deadLines;   // lines in the file that the cap already dropped from the list
    private readonly object _pendingGate = new();   // never held while waiting for _gate (lock order: _gate, then _pendingGate)
    private List<HistoryEntry> _pending = [];       // appended, not yet written; oldest first
    private bool _writing;                          // a background writer is draining _pending

    /// <summary>The entry as it is stored: an over-long statement is cut with a marker.</summary>
    public HistoryEntry Normalize(HistoryEntry entry) =>
        entry.Sql.Length <= maxSqlLength
            ? entry
            : entry with { Sql = entry.Sql[..maxSqlLength] + $"\n-- … truncated ({entry.Sql.Length - maxSqlLength:N0} more characters)" };

    /// <summary>
    /// Queues the entry and returns at once: a single background writer appends it to the file, in call order, so a run never
    /// waits on the disk (nor on a compaction). Readers of this instance see it at once; <see cref="Flush"/> forces it to disk.
    /// </summary>
    public void Append(HistoryEntry entry)
    {
        bool start;
        lock (_pendingGate)
        {
            _pending.Add(entry);
            start = !_writing;
            _writing = true;
        }
        if (start) _ = Task.Run(Drain);
    }

    /// <summary>Writes every queued entry to the file before returning (used on exit and by tests). Never throws.</summary>
    public void Flush()
    {
        lock (_gate) WritePending();
    }

    private void Drain()
    {
        while (true)
        {
            lock (_gate) WritePending();
            lock (_pendingGate)
            {
                // checked under the same lock as Append sets the flag: an entry queued now starts a new writer or is seen here
                if (_pending.Count == 0)
                {
                    _writing = false;
                    return;
                }
            }
        }
    }

    /// <summary>Writes the queued entries as one append. Taken and written under <c>_gate</c>, so batches reach the file in order.</summary>
    private void WritePending()
    {
        List<HistoryEntry> batch;
        lock (_pendingGate)
        {
            if (_pending.Count == 0) return;
            batch = _pending;
            _pending = [];
        }
        try
        {
            var stored = batch.ConvertAll(Normalize);
            var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(file, string.Concat(stored.Select(e => JsonSerializer.Serialize(e, Json) + "\n")));
            foreach (var e in stored)
            {
                if (_items is { } items)
                {
                    items.Add(e);
                    if (items.Count > maxEntries)
                    {
                        items.RemoveAt(0);   // the list never shows more than the cap; the file is rewritten once enough lines are dead
                        if (++_deadLines >= Slack) Compact(items);
                    }
                }
                else if (++_appendedUnread >= Slack)
                {
                    _appendedUnread = 0;
                    Compact(Load());   // the file may have grown past the cap since the last read
                }
            }
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine("Query history: append failed: " + e.Message);
        }
    }

    /// <summary>Entries matching every given filter, most recent first. The text matches the statement and the connection name, ignoring case.</summary>
    public IReadOnlyList<HistoryEntry> Query(string? text = null, Guid? connectionId = null, bool onlyErrors = false, DateTime? since = null, int limit = 200)
    {
        try
        {
            lock (_gate)
            {
                WritePending();
                var items = Load();
                var result = new List<HistoryEntry>();
                for (var i = items.Count - 1; i >= 0 && result.Count < limit; i--)
                {
                    var e = items[i];
                    if (connectionId is { } id && e.ConnectionId != id) continue;
                    if (onlyErrors && e.Error is null) continue;
                    if (since is { } s && e.At < s) continue;
                    if (!string.IsNullOrWhiteSpace(text)
                        && e.Sql.IndexOf(text.Trim(), StringComparison.OrdinalIgnoreCase) < 0
                        && e.ConnectionName.IndexOf(text.Trim(), StringComparison.OrdinalIgnoreCase) < 0) continue;
                    result.Add(e);
                }
                return result;
            }
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine("Query history: query failed: " + e.Message);
            return [];
        }
    }

    /// <summary>Size of the history file in bytes (0 when it does not exist); reads no content.</summary>
    public long FileSize
    {
        get
        {
            try { return File.Exists(file) ? new FileInfo(file).Length : 0; }
            catch (IOException) { return 0; }
        }
    }


    /// <summary>Number of stored entries (reads the file when needed).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                WritePending();
                return Load().Count;
            }
        }
    }

    public void Delete(IEnumerable<Guid> ids)
    {
        try
        {
            var set = ids.ToHashSet();
            lock (_gate)
            {
                WritePending();
                var items = Load();
                if (items.RemoveAll(e => set.Contains(e.Id)) > 0) Rewrite(items);
            }
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine("Query history: delete failed: " + e.Message);
        }
    }

    public void Clear()
    {
        try
        {
            lock (_gate)
            {
                lock (_pendingGate) _pending = [];   // queued before the clear: cleared with the rest
                _items = [];
                Rewrite(_items);
            }
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine("Query history: clear failed: " + e.Message);
        }
    }

    private int Slack => Math.Max(1, maxEntries / 10);

    private List<HistoryEntry> Load()
    {
        if (_items is not null) return _items;
        var items = new List<HistoryEntry>();
        try
        {
            if (File.Exists(file))
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var e = JsonSerializer.Deserialize<HistoryEntry>(line, Json);
                        if (e is { Sql: not null, ConnectionName: not null, Database: not null } && e.Id != Guid.Empty) items.Add(e);
                    }
                    catch (JsonException) { /* corrupt line: skipped, the file stays as it is */ }
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("Query history: read failed: " + e.Message);
        }
        _appendedUnread = 0;
        _items = items;
        if (items.Count > maxEntries) Compact(items);
        return items;
    }

    private void Compact(List<HistoryEntry> items)
    {
        if (items.Count > maxEntries) items.RemoveRange(0, items.Count - maxEntries);
        _deadLines = 0;
        Rewrite(items);
    }

    /// <summary>Writes the whole list to a temp file, then moves it over the history (atomic enough: a crash keeps the old file).</summary>
    private void Rewrite(List<HistoryEntry> items)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
            Directory.CreateDirectory(dir);
            var temp = file + ".tmp";
            File.WriteAllLines(temp, items.Select(e => JsonSerializer.Serialize(e, Json)));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("Query history: rewrite failed: " + e.Message);
        }
    }
}
