using System.Text.RegularExpressions;

namespace DbExplorer.Core.Scripting;

/// <summary>One message sent by the server while a statement ran (PRINT, a warning, STATISTICS IO / TIME output).</summary>
public sealed record ServerMessage(DateTimeOffset At, string Text);

public enum TimePhase { ParseCompile, Execution }

/// <summary>What one line of server output means. Never null, never throws: see <see cref="StatisticsParser"/>.</summary>
public abstract record StatsItem;

/// <summary>
/// One STATISTICS IO line. Counters are in the canonical (newest server) order; a server that does not report page-server
/// counters leaves them at 0.
/// </summary>
public sealed record IoStats(
    string Table,
    long ScanCount,
    long LogicalReads,
    long PhysicalReads,
    long PageServerReads,
    long ReadAheadReads,
    long PageServerReadAheadReads,
    long LobLogicalReads,
    long LobPhysicalReads,
    long LobPageServerReads,
    long LobReadAheadReads,
    long LobPageServerReadAheadReads) : StatsItem;

/// <summary>One STATISTICS TIME message (parse and compile time, or execution time).</summary>
public sealed record TimeStats(TimePhase Phase, long CpuMs, long ElapsedMs) : StatsItem;

/// <summary>Any other line (PRINT text, warnings, lines that do not look like statistics).</summary>
public sealed record TextLine(string Text) : StatsItem;

/// <summary>
/// Turns server messages into STATISTICS IO / TIME items. The server writes them in the language of the login (English,
/// French, ...), so nothing depends on keywords: an IO line is a table name between the language's quotes ('x', "x",
/// « x », „x“) followed by exactly 7 or 11
/// integers (older servers report 7 counters, newer ones add the page-server ones), and a TIME message is two
/// <c>= N ms</c> values; only the parse/compile versus execution split looks at words (English and French markers, else
/// execution). Anything that does not match stays a <see cref="TextLine"/>.
/// </summary>
public static partial class StatisticsParser
{
    [GeneratedRegex(@"=\s*(\d+)\s*ms", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MsValue();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    // A whole number as the server prints it: set off by white space, ended by a comma, a full stop or the end.
    [GeneratedRegex(@"(?<![\w.,'-])\d+(?=[,.]?(?:\s|$))")]
    private static partial Regex StandaloneNumber();

    private static readonly System.Buffers.SearchValues<char> OpeningQuotes = System.Buffers.SearchValues.Create("'\"«„");

    /// <summary>Parses one server message (it may hold several lines). An empty message gives no items.</summary>
    public static IReadOnlyList<StatsItem> Parse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return [];
        try
        {
            if (TryParseTime(message, out var time)) return [time];

            var items = new List<StatsItem>();
            foreach (var raw in message.Split('\n'))
            {
                var line = raw.Trim('\r', ' ', '\t');
                if (line.Length == 0) continue;
                items.Add(TryParseIo(line, out var io) ? io : new TextLine(line));
            }
            return items;
        }
        catch (Exception)
        {
            return [new TextLine(message.Trim())];   // a parser must never be the reason a run shows nothing
        }
    }

    private static bool TryParseTime(string message, out TimeStats time)
    {
        time = null!;
        var values = MsValue().Matches(message);
        // Two "= N ms" values and no other number: "CPU time = 15 ms, elapsed time = 20 ms" in any language.
        if (values.Count != 2 || Digits().Matches(message).Count != 2) return false;
        if (!long.TryParse(values[0].Groups[1].Value, out var cpu) || !long.TryParse(values[1].Groups[1].Value, out var elapsed))
            return false;

        var header = message[..values[0].Index].ToLowerInvariant();
        var parse = header.Contains("compil") || header.Contains("pars") || header.Contains("analys");
        time = new TimeStats(parse ? TimePhase.ParseCompile : TimePhase.Execution, cpu, elapsed);
        return true;
    }

    private static bool TryParseIo(string line, out IoStats io)
    {
        io = null!;
        var open = line.AsSpan().IndexOfAny(OpeningQuotes);
        if (open < 0 || line.AsSpan(0, open).ContainsAnyInRange('0', '9')) return false;   // "Table 'x'." / "Table « x »."
        var close = line[open] switch { '«' => '»', '„' => '“', var same => same };

        // The name ends at the first quote followed by a full stop and a space whose rest is a list of 7 or 11 numbers: the
        // name itself may contain quotes (and digits: #temp tables), and French counters hold an apostrophe ("d'analyses").
        for (var i = open + 1; i < line.Length - 2; i++)
        {
            if (line[i] != close || line[i + 1] != '.' || !char.IsWhiteSpace(line[i + 2])) continue;
            if (!TryCounters(line[(i + 2)..], out var n)) continue;

            var name = Unpad(line[(open + 1)..i], close);
            io = n.Length == 7
                ? new IoStats(name, n[0], n[1], n[2], 0, n[3], 0, n[4], n[5], 0, n[6], 0)
                : new IoStats(name, n[0], n[1], n[2], n[3], n[4], n[5], n[6], n[7], n[8], n[9], n[10]);
            return true;
        }
        return false;
    }

    // French pads the name with one no-break space inside its guillemets; Portuguese doubles the apostrophes.
    private static string Unpad(string name, char close)
    {
        if (name.Length < 2) return name;
        var padded = close switch
        {
            '»' => char.IsWhiteSpace(name[0]) && char.IsWhiteSpace(name[^1]),
            '\'' => name[0] == '\'' && name[^1] == '\'',
            _ => false,
        };
        return padded ? name[1..^1] : name;
    }

    private static bool TryCounters(string rest, out long[] numbers)
    {
        numbers = [];
        var all = Digits().Matches(rest);
        if (all.Count is not (7 or 11) || StandaloneNumber().Matches(rest).Count != all.Count) return false;

        var values = new long[all.Count];
        for (var i = 0; i < values.Length; i++)
            if (!long.TryParse(all[i].Value, out values[i])) return false;
        numbers = values;
        return true;
    }
}

/// <summary>The messages one statement produced, with where they come from.</summary>
/// <param name="Database">The database (group member) the statement ran on; null outside a group run.</param>
/// <param name="Statement">1-based position of the statement in the run.</param>
public sealed record StatementMessages(string? Database, int Statement, IReadOnlyList<ServerMessage> Messages);

/// <summary>A table shaped for a result grid: column names and rows of plain values (numbers are boxed <c>long</c>).</summary>
public sealed record StatsTable(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);

/// <summary>
/// Builds the two tables shown after a run with statistics on. Pure.
/// <para>
/// <b>Statistics</b>: one table, one row per statistic line, in the order the server sent them, then a total per statement.
/// Columns: [Database,] Statement, Kind, Table, Scan count, Logical reads, Physical reads, Read-ahead reads, LOB logical
/// reads, LOB physical reads, LOB read-ahead reads, [the four page-server columns, only when one is not 0,] CPU ms, Elapsed ms.
/// <c>Kind</c> says what the row is: "IO" (a table: IO columns filled, CPU and elapsed empty), "Parse/compile" or "Execution"
/// (STATISTICS TIME: only CPU and elapsed filled), "Total" (the sums of the statement: every IO column, CPU and elapsed) and
/// "Grand total" (the sums of the whole run, or of the database in a group run; only when more than one statement has
/// statistics). Sorting by Kind or Table makes the sections. A stored procedure reports its inner statements and then the
/// call itself, so its Total can exceed the wall-clock time.
/// </para>
/// <para><b>Messages</b>: [Database,] Statement, Time, Message: every line the server sent, parsed or not, one per row.</para>
/// </summary>
public static class StatisticsReport
{
    public const string KindIo = "IO";
    public const string KindParse = "Parse/compile";
    public const string KindExecution = "Execution";
    public const string KindTotal = "Total";
    public const string KindGrandTotal = "Grand total";

    private static readonly string[] IoNames =
    [
        "Scan count", "Logical reads", "Physical reads", "Read-ahead reads", "LOB logical reads", "LOB physical reads",
        "LOB read-ahead reads",
    ];

    private static readonly string[] PageServerNames =
    [
        "Page server reads", "Page server read-ahead reads", "LOB page server reads", "LOB page server read-ahead reads",
    ];

    // Slots of the 11 counters, in the order of the columns above (the 4 page-server ones last).
    private const int Slots = 11;

    private sealed record Row(string? Database, int? Statement, string Kind, string? Table, long[]? Io, long? Cpu, long? Elapsed);

    /// <summary>True when at least one line of the messages is a STATISTICS IO / TIME line.</summary>
    public static bool HasStatistics(IEnumerable<StatementMessages> sources) =>
        sources.Any(s => s.Messages.Any(m => StatisticsParser.Parse(m.Text).Any(i => i is not TextLine)));

    public static StatsTable BuildStatistics(IEnumerable<StatementMessages> sources)
    {
        var rows = new List<Row>();
        foreach (var database in sources.GroupBy(s => s.Database))
        {
            var statementTotals = new List<Row>();
            foreach (var source in database)
            {
                var own = new List<Row>();
                foreach (var message in source.Messages)
                    foreach (var item in StatisticsParser.Parse(message.Text))
                    {
                        switch (item)
                        {
                            case IoStats io:
                                own.Add(new Row(database.Key, source.Statement, KindIo, io.Table, IoSlots(io), null, null));
                                break;
                            case TimeStats t:
                                own.Add(new Row(database.Key, source.Statement,
                                    t.Phase == TimePhase.ParseCompile ? KindParse : KindExecution, null, null, t.CpuMs, t.ElapsedMs));
                                break;
                        }
                    }
                if (own.Count == 0) continue;
                rows.AddRange(own);
                var total = Sum(own, database.Key, source.Statement, KindTotal);
                rows.Add(total);
                statementTotals.Add(total);
            }
            if (statementTotals.Count > 1) rows.Add(Sum(statementTotals, database.Key, null, KindGrandTotal));
        }

        var withDatabase = sources.Any(s => s.Database is not null);
        var withPageServer = rows.Any(r => r.Io is { } io && io.AsSpan(IoNames.Length).ContainsAnyExcept(0L));

        var columns = new List<string>();
        if (withDatabase) columns.Add("Database");
        columns.AddRange(["Statement", "Kind", "Table"]);
        columns.AddRange(IoNames);
        if (withPageServer) columns.AddRange(PageServerNames);
        columns.AddRange(["CPU ms", "Elapsed ms"]);

        var slotCount = IoNames.Length + (withPageServer ? PageServerNames.Length : 0);
        var output = rows.Select(r =>
        {
            var values = new List<object?>();
            if (withDatabase) values.Add(r.Database);
            values.Add(r.Statement is { } s ? (long)s : null);
            values.Add(r.Kind);
            values.Add(r.Table);
            for (var i = 0; i < slotCount; i++) values.Add(r.Io is null ? null : r.Io[i]);
            values.Add(r.Cpu);
            values.Add(r.Elapsed);
            return values.ToArray();
        }).ToList();
        return new StatsTable(columns, output);
    }

    public static StatsTable BuildMessages(IEnumerable<StatementMessages> sources)
    {
        var withDatabase = sources.Any(s => s.Database is not null);
        var columns = new List<string>();
        if (withDatabase) columns.Add("Database");
        columns.AddRange(["Statement", "Time", "Message"]);

        var rows = new List<object?[]>();
        foreach (var source in sources)
            foreach (var message in source.Messages)
                foreach (var raw in message.Text.Split('\n'))
                {
                    var line = raw.Trim('\r', ' ', '\t');
                    if (line.Length == 0) continue;
                    var time = message.At.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
                    rows.Add(withDatabase ? [source.Database, (long)source.Statement, time, line] : [(long)source.Statement, time, line]);
                }
        return new StatsTable(columns, rows);
    }

    // Canonical order of IoStats -> column order: the 7 classic counters first, then the page-server ones.
    private static long[] IoSlots(IoStats s) =>
    [
        s.ScanCount, s.LogicalReads, s.PhysicalReads, s.ReadAheadReads, s.LobLogicalReads, s.LobPhysicalReads, s.LobReadAheadReads,
        s.PageServerReads, s.PageServerReadAheadReads, s.LobPageServerReads, s.LobPageServerReadAheadReads,
    ];

    private static Row Sum(List<Row> rows, string? database, int? statement, string kind)
    {
        var io = new long[Slots];
        long cpu = 0, elapsed = 0;
        foreach (var r in rows)
        {
            if (r.Io is not null)
                for (var i = 0; i < Slots; i++) io[i] += r.Io[i];
            cpu += r.Cpu ?? 0;
            elapsed += r.Elapsed ?? 0;
        }
        return new Row(database, statement, kind, null, io, cpu, elapsed);
    }
}
