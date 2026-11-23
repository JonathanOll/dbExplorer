using System.Globalization;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.ViewModels;

/// <summary>One result set of a statement, shaped for a read-only <c>ResultGrid</c> (no filters, no edit state).</summary>
public sealed class ResultSetViewModel(IReadOnlyList<ResultColumn> columns, IReadOnlyList<object?[]> rows, bool truncated)
{
    public IReadOnlyList<ResultColumn> Columns { get; } = columns;

    public IReadOnlyList<object?[]> Rows { get; } = rows;

    public bool Truncated { get; } = truncated;
}

/// <summary>What one executed statement of a script produced: its result sets, or its error, and the status line.</summary>
public sealed class StatementResultViewModel
{
    /// <param name="title">Replaces the statement preview in <see cref="Header"/> (a group run names the member).</param>
    public StatementResultViewModel(int index, StatementResult result, int maxRows, string? title = null)
    {
        Index = index;
        StatementText = result.Statement.Text;
        Error = result.Error;
        Sets = result.Sets
            .Select(s => new ResultSetViewModel(s.Columns.Select(c => new ResultColumn(c)).ToList(), s.Rows, s.Truncated))
            .ToList();
        IsTruncated = Sets.Any(s => s.Truncated);
        TruncatedNotice = IsTruncated ? $"showing first {maxRows} rows — raise the limit and re-run" : null;

        var ms = ((long)result.Duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        StatusText = result.Error is not null
            ? $"Failed · {ms} ms"
            : Sets.Count > 0
                ? $"{Rows(Sets.Sum(s => (long)s.Rows.Count))} · {ms} ms"
                : result.RowsAffected is { } affected ? $"{Rows(affected)} affected" : $"OK · {ms} ms";
        Header = title is not null
            ? $"{(result.Error is null ? "" : "! ")}{title}"
            : $"{(result.Error is null ? "" : "! ")}{index}: {Preview(StatementText)}";
    }

    /// <summary>1-based position of the statement in the run.</summary>
    public int Index { get; }

    public string StatementText { get; }

    /// <summary>Tab header: the number and the start of the statement; an error is marked with "!".</summary>
    public string Header { get; }

    /// <summary>Null on success.</summary>
    public string? Error { get; }

    public bool HasError => Error is not null;

    public IReadOnlyList<ResultSetViewModel> Sets { get; }

    public bool HasSets => Sets.Count > 0;

    public bool IsTruncated { get; }

    /// <summary>"showing first N rows — raise the limit and re-run" when a set hit the row limit, else null.</summary>
    public string? TruncatedNotice { get; }

    /// <summary><c>N rows · X ms</c> for a query, <c>N rows affected</c> for a write.</summary>
    public string StatusText { get; }

    private static string Rows(long n) => n == 1 ? "1 row" : $"{n.ToString(CultureInfo.InvariantCulture)} rows";

    private static string Preview(string sql)
    {
        var line = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 28 ? line : line[..28] + "…";
    }
}
