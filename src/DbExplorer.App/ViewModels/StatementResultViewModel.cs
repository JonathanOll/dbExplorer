using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.ViewModels;

/// <summary>One result set of a statement, shaped for a read-only <c>ResultGrid</c> (no filters, no edit state).</summary>
public sealed partial class ResultSetViewModel(IReadOnlyList<ResultColumn> columns, IReadOnlyList<object?[]> rows, bool truncated, int index = 1)
    : ObservableObject
{
    /// <summary>How many rows are selected in the grid (kept by the view); 0 when none.</summary>
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectionText), nameof(CopyTip))]
    [ObservableProperty] private int _selectedCount;

    public bool HasSelection => SelectedCount > 0;

    /// <summary>"3 selected": the counter next to the result actions.</summary>
    public string SelectionText => $"{SelectedCount:N0} selected";

    /// <summary>What Copy does now: the selected rows, or the whole result.</summary>
    public string CopyTip => HasSelection
        ? $"Copy the {SelectedCount:N0} selected rows with the column names, tab separated"
        : "Copy all rows with the column names, tab separated (pastes into a spreadsheet)";

    public IReadOnlyList<ResultColumn> Columns { get; } = columns;

    public IReadOnlyList<object?[]> Rows { get; } = rows;

    public bool Truncated { get; } = truncated;

    /// <summary>A header is shown over the set (the statement returned several).</summary>
    public bool ShowTitle { get; init; }

    /// <summary>1-based position among the result sets of the statement.</summary>
    public int Index { get; } = index;

    /// <summary>"Result 2 · 12 rows": the header over the set when a statement returned several.</summary>
    public string Title => $"Result {Index} · {StatementResultViewModel.RowsText(Rows.Count)}";
}

/// <summary>What one executed statement of a script produced: its result sets, or its error, and the status line.</summary>
public sealed partial class StatementResultViewModel : IResultTab
{
    /// <summary>Start of the message the read-only guard puts in <see cref="Error"/> when it refuses a write.</summary>
    public const string RefusedPrefix = "Refused:";

    /// <param name="title">Replaces the statement preview in <see cref="Header"/> (a group run names the member).</param>
    public StatementResultViewModel(int index, StatementResult result, int maxRows, string? title = null)
    {
        Index = index;
        StatementText = result.Statement.Text;
        StartOffset = result.Statement.Start;
        EndOffset = result.Statement.End;
        Duration = result.Duration;
        Error = result.Error;
        Messages = result.Messages ?? [];
        var number = 0;
        Sets = result.Sets
            .Select(s => new ResultSetViewModel(s.Columns.Select(c => new ResultColumn(c)).ToList(), s.Rows, s.Truncated, ++number) { ShowTitle = result.Sets.Count > 1 })
            .ToList();
        IsTruncated = Sets.Any(s => s.Truncated);
        TruncatedNotice = IsTruncated ? $"Showing first {Count(maxRows)} rows" : null;

        var ms = ((long)result.Duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms";
        if (result.Error is not null)
        {
            StatusMain = IsBlocked ? "Blocked" : "Failed";
            StatusDetail = IsBlocked ? null : ms;
        }
        else if (Sets.Count > 1)
        {
            StatusMain = $"{Sets.Count} result sets";
            StatusDetail = string.Join(" + ", Sets.Select(s => Count(s.Rows.Count))) + $" rows · {ms}";
        }
        else if (Sets.Count == 1)
        {
            StatusMain = RowsText(Sets[0].Rows.Count);
            StatusDetail = ms;
        }
        else if (result.RowsAffected is { } affected)
        {
            StatusMain = $"{RowsText(affected)} affected";
            StatusDetail = ms;
        }
        else
        {
            StatusMain = "Completed";
            StatusDetail = ms;
        }
        StatusText = StatusDetail is null ? StatusMain : $"{StatusMain} · {StatusDetail}";

        Header = title ?? $"{index} · {Preview(StatementText)}";
        ToolTipText = HasError ? $"Failed · {StatementText}" : StatementText;
        AutomationName = HasError ? $"Failed: {Header}" : Header;

        NoResultText = HasError || HasSets
            ? null
            : result.RowsAffected is { } n ? $"Statement ran, {RowsText(n)} affected" : "Statement ran, no result set";
    }

    /// <summary>1-based position of the statement in the run.</summary>
    public int Index { get; }

    public string StatementText { get; }

    /// <summary>Offsets of the statement in the script text it ran from (start inclusive, end exclusive).</summary>
    public int StartOffset { get; }

    public int EndOffset { get; }

    /// <summary>The statement can be located in the editor (a stand-in for a member that never ran has no range).</summary>
    public bool HasRange => EndOffset > StartOffset && StatementText.Length > 0;

    /// <summary>1-based line of the statement in the script it ran from; set by the tab that ran it.</summary>
    public int? StartLine { get; set; }

    public TimeSpan Duration { get; }

    /// <summary>Tab header: the number and the start of the statement ("1 · SELECT … FROM orders"). A failure is shown by the tab's icon.</summary>
    public string Header { get; }

    /// <summary>Spoken name of the tab: a failure is said in words, not only by colour.</summary>
    public string AutomationName { get; }

    /// <summary>Null on success.</summary>
    public string? Error { get; }

    public bool HasError => Error is not null;

    /// <summary>The read-only guard refused the statement: a policy block, not a database error.</summary>
    public bool IsBlocked => Error is { } e && e.StartsWith(RefusedPrefix, StringComparison.Ordinal);

    /// <summary>A failure that came from the database (red banner).</summary>
    public bool IsFailure => HasError && !IsBlocked;

    bool IResultTab.IsGrid => false;

    bool IResultTab.ShowFailure => IsFailure;

    /// <summary>"Statement 3 failed · line 10", or "Blocked: this connection is read-only".</summary>
    public string ErrorTitle => IsBlocked
        ? "Blocked: this connection is read-only"
        : StartLine is { } line ? $"Statement {Index} failed · line {line.ToString(CultureInfo.InvariantCulture)}" : $"Statement {Index} failed";

    /// <summary>What the banner says under the title: the engine's message, or how to allow the write.</summary>
    public string? ErrorDetail => IsBlocked
        ? "Write statements are not allowed here, so nothing ran. Turn read-only off in the connection settings to run it."
        : Error;

    /// <summary>The server messages of this statement (empty unless the run had statistics on).</summary>
    public IReadOnlyList<ServerMessage> Messages { get; }

    bool IResultTab.ShowOk => !HasError;

    public string? ToolTipText { get; }

    public IReadOnlyList<ResultSetViewModel> Sets { get; }

    public bool HasSets => Sets.Count > 0;

    /// <summary>More than one result set: each gets a header and its own slice.</summary>
    public bool HasSeveralSets => Sets.Count > 1;

    /// <summary>The only result set, whose Copy and Export sit in the result line; null when there are none or several (each has its own).</summary>
    public ResultSetViewModel? SingleSet => Sets.Count == 1 ? Sets[0] : null;

    public bool IsTruncated { get; }

    /// <summary>"Showing first 1,000 rows" when a set hit the row limit, else null.</summary>
    public string? TruncatedNotice { get; }

    /// <summary>The whole result line: <c>N rows · X ms</c> for a query, <c>N rows affected · X ms</c> for a write.</summary>
    public string StatusText { get; }

    /// <summary>The first part of the result line ("60 rows"), shown in the strong weight.</summary>
    public string StatusMain { get; }

    /// <summary>The rest of the result line ("12 ms"), shown dim; null when there is none.</summary>
    public string? StatusDetail { get; }

    /// <summary>Centered text for a statement that succeeded without a result set; null otherwise.</summary>
    public string? NoResultText { get; }

    public bool ShowNoResult => NoResultText is not null;

    internal static string RowsText(long n) => n == 1 ? "1 row" : $"{Count(n)} rows";

    private static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private const int PreviewLength = 26;

    // Longest part of a statement looked at to name it: a script can hold a very long statement.
    private const int PreviewWindow = 2000;

    [GeneratedRegex(@"^SELECT\b.*?\bFROM\s+(?<t>[\w.\[\]""`]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SelectFrom();

    [GeneratedRegex(@"^(?<v>UPDATE|INSERT\s+INTO|DELETE\s+FROM|MERGE\s+INTO|TRUNCATE\s+TABLE|DROP\s+TABLE|ALTER\s+TABLE|CREATE\s+TABLE|CREATE\s+VIEW)\s+(?<t>[\w.\[\]""`]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Verb();

    /// <summary>
    /// The short name of a statement for a tab: its first line of code (leading comments are skipped), whitespace collapsed;
    /// a long one reads "SELECT … FROM orders" or "UPDATE customers" when the verb and the table can be found.
    /// </summary>
    internal static string Preview(string sql)
    {
        var body = StripLeadingComments(sql);
        if (body.Length == 0) body = sql;
        if (body.Length > PreviewWindow) body = body[..PreviewWindow];
        var line = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length <= PreviewLength) return line;

        if (SelectFrom().Match(line) is { Success: true } select)
        {
            var named = $"SELECT … FROM {select.Groups["t"].Value}";
            if (named.Length <= PreviewLength + 12) return named;
        }
        if (Verb().Match(line) is { Success: true } verb)
        {
            var words = verb.Groups["v"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var named = $"{string.Join(' ', words).ToUpperInvariant()} {verb.Groups["t"].Value}";
            if (named.Length <= PreviewLength + 12) return named;
        }
        return line[..PreviewLength].TrimEnd() + "…";
    }

    // Drops the "-- line" and "/* block */" comments a statement starts with (they belong to it in the splitter).
    private static string StripLeadingComments(string sql)
    {
        var i = 0;
        while (i < sql.Length)
        {
            while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
            if (string.CompareOrdinal(sql, i, "--", 0, 2) == 0)
            {
                var eol = sql.IndexOf('\n', i);
                if (eol < 0) return "";
                i = eol + 1;
            }
            else if (string.CompareOrdinal(sql, i, "/*", 0, 2) == 0)
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) return "";
                i = close + 2;
            }
            else break;
        }
        return sql[i..];
    }
}
