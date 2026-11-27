using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>One production database a write is about to reach.</summary>
public sealed record ProductionTarget(string Connection, string Server, string Database)
{
    public static ProductionTarget Of(ConnectionConfig c) => new(
        c.Name,
        c.Kind == DbKind.Sqlite ? "SQLite file" : c.Port is { } port ? $"{c.Host}:{port}" : c.Host ?? "",
        c.Database);
}

/// <summary>What the confirmation dialog shows: where, what is about to be done and the statements (or scripts) concerned.</summary>
/// <param name="Action">One line, e.g. "Run 2 statements that may write".</param>
/// <param name="Items">The statements or script names, in full; <see cref="Summary"/> shortens them.</param>
/// <param name="CanSnooze">The "do not ask again in this tab" box is offered.</param>
public sealed record ProductionWriteRequest(
    IReadOnlyList<ProductionTarget> Targets, string Action, IReadOnlyList<string> Items, bool CanSnooze)
{
    public const int MaxLines = 8;
    private const int MaxLineLength = 100;

    public static string SnoozeText =>
        $"Do not ask again in this tab for {(int)ProductionGuard.SnoozeDuration.TotalMinutes} minutes (this connection only)";

    /// <summary>One line per item, at most <see cref="MaxLines"/>, then "and N more".</summary>
    public IReadOnlyList<string> Summary
    {
        get
        {
            var lines = Items.Take(MaxLines).Select(OneLine).ToList();
            if (Items.Count > MaxLines) lines.Add($"… and {Items.Count - MaxLines} more");
            return lines;
        }
    }

    private static string OneLine(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= MaxLineLength ? line : line[..(MaxLineLength - 1)] + "…";
    }
}

/// <summary>The "do not ask again" of one tab: valid for one connection, exactly as configured, until a deadline.</summary>
public sealed class ProductionApproval
{
    internal ConnectionConfig? Connection { get; set; }
    internal DateTime Until { get; set; }
}

/// <summary>
/// The one gate every write path goes through before it touches a production connection (script runs, grid edits, database
/// upgrade). It judges the latest saved settings as well as the ones the caller holds: either saying production is enough.
/// Without a dialog it refuses.
/// </summary>
public sealed class ProductionGuard(
    IProductionDialogs? dialogs = null, Func<Guid, ConnectionConfig?>? latest = null, Func<DateTime>? now = null)
{
    public static readonly TimeSpan SnoozeDuration = TimeSpan.FromMinutes(5);

    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);

    /// <summary>Whether a write on this connection needs the confirmation.</summary>
    public bool IsProduction(ConnectionConfig connection) =>
        connection.IsProduction || latest?.Invoke(connection.Id) is { IsProduction: true };

    /// <summary>
    /// True when the write may go ahead: nothing in <paramref name="connections"/> is production, the tab's approval is still
    /// valid, or the user confirms. Never throws; a failing dialog refuses.
    /// </summary>
    public async Task<bool> AllowAsync(
        IReadOnlyList<ConnectionConfig> connections, string action, IReadOnlyList<string> items, ProductionApproval? approval = null)
    {
        var production = connections.Where(IsProduction).Select(c => latest?.Invoke(c.Id) ?? c).ToList();
        if (production.Count == 0) return true;

        var single = production.Count == 1 ? production[0] : null;
        if (approval is not null && single is not null && approval.Connection == single && _now() < approval.Until) return true;
        if (dialogs is null) return false;

        var snooze = approval is not null && single is not null;
        ProductionWriteAnswer answer;
        try
        {
            answer = await dialogs.ConfirmProductionWriteAsync(
                new ProductionWriteRequest([.. production.Select(ProductionTarget.Of)], action, items, snooze));
        }
        catch (Exception)
        {
            return false;
        }
        if (!answer.Confirmed) return false;
        if (snooze && answer.DontAskAgain)
        {
            approval!.Connection = single;
            approval.Until = _now() + SnoozeDuration;
        }
        return true;
    }
}
