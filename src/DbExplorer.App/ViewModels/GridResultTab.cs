using System.Globalization;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.ViewModels;

/// <summary>An entry of the result tab strip of a script: a statement's result, or a grid such as Statistics / Messages.</summary>
public interface IResultTab
{
    string Header { get; }

    bool HasError { get; }

    /// <summary>The "ok" icon is shown (a statement that succeeded); false for grids and failed statements.</summary>
    bool ShowOk { get; }

    string? ToolTipText { get; }
}

public enum GridTabKind { Statistics, Messages }

/// <summary>A result tab that is one read-only grid (see <see cref="StatisticsReport"/>): the Statistics or the Messages tab.</summary>
public sealed class GridResultTab : IResultTab
{
    public GridResultTab(GridTabKind kind, StatsTable table, string status, string? tooltip = null)
    {
        Kind = kind;
        Header = kind == GridTabKind.Statistics ? "Statistics" : "Messages";
        Columns = table.Columns.Select(c => new ResultColumn(c)).ToList();
        Rows = table.Rows;
        StatusText = status;
        ToolTipText = tooltip;
    }

    public GridTabKind Kind { get; }

    public string Header { get; }

    public IReadOnlyList<ResultColumn> Columns { get; }

    public IReadOnlyList<object?[]> Rows { get; }

    /// <summary>The line under the grid: what the table holds, or why it is empty.</summary>
    public string StatusText { get; }

    public string? ToolTipText { get; }

    public bool HasError => false;

    public bool ShowOk => false;

    /// <summary>
    /// The two tabs of a run with statistics on: the Statistics table (IO and TIME rows, totals) and the Messages lines.
    /// <paramref name="sources"/> are the statements' messages; a group run names the database of each.
    /// </summary>
    public static (GridResultTab Statistics, GridResultTab Messages) Build(IReadOnlyList<StatementMessages> sources)
    {
        var stats = StatisticsReport.BuildStatistics(sources);
        var messages = StatisticsReport.BuildMessages(sources);

        var statsStatus = stats.Rows.Count > 0
            ? Summary(stats)
            : "The server returned no STATISTICS IO / TIME lines for this run (statements that read no table report only times).";
        var messageStatus = messages.Rows.Count == 1 ? "1 line" : $"{messages.Rows.Count.ToString(CultureInfo.InvariantCulture)} lines";
        return (new GridResultTab(GridTabKind.Statistics, stats, statsStatus,
                    "STATISTICS IO / TIME of the run: one row per table / time line, a Total per statement"),
                new GridResultTab(GridTabKind.Messages, messages, messageStatus, "Every message the server sent during the run"));
    }

    // "N rows · logical reads L · CPU C ms · elapsed E ms" from the last Grand total / Total rows.
    private static string Summary(StatsTable stats)
    {
        var kind = stats.Columns.ToList().IndexOf("Kind");
        var logical = stats.Columns.ToList().IndexOf("Logical reads");
        var cpu = stats.Columns.ToList().IndexOf("CPU ms");
        var elapsed = stats.Columns.ToList().IndexOf("Elapsed ms");
        var totals = stats.Rows.Where(r => r[kind] is StatisticsReport.KindTotal or StatisticsReport.KindGrandTotal).ToList();
        string Sum(int column) => totals.Where(r => r[kind] is StatisticsReport.KindTotal).Sum(r => r[column] is long v ? v : 0)
            .ToString(CultureInfo.InvariantCulture);
        var rows = stats.Rows.Count == 1 ? "1 row" : $"{stats.Rows.Count.ToString(CultureInfo.InvariantCulture)} rows";
        return $"{rows} · logical reads {Sum(logical)} · CPU {Sum(cpu)} ms · elapsed {Sum(elapsed)} ms";
    }
}
