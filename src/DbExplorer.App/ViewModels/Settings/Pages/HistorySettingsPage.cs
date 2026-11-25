using System.Globalization;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.ViewModels;

/// <summary>The query history: recording on or off, how much is stored, and a confirmed "Clear history".</summary>
public sealed class HistorySettingsPage : SettingsPageViewModel
{
    public HistorySettingsPage(SettingsContext ctx)
        : base("history", "Query history", "IconHistory", "The statements run from script tabs, kept in View > Query history (Ctrl+Shift+H).")
    {
        var record = Make.Toggle("Record query history",
            "Remember every statement run from a script tab, with its connection, duration and result. Turning it off keeps what is already stored.",
            ctx.RecordHistory!, true, "history record queries log statements");
        AddGroup("Recording", record);

        if (ctx.History is { } history)
            AddGroup("Stored history", new ClearHistoryItem(history) { Keywords = "history delete erase empty remove" });
    }
}

/// <summary>"Clear history": the first click arms the button, the second one empties the history now (it cannot be undone).</summary>
public sealed class ClearHistoryItem : ActionSettingItem
{
    private const string Idle = "Clear history…";
    private readonly QueryHistory _history;
    private bool _armed;

    public ClearHistoryItem(QueryHistory history)
        : base("Clear history", "Delete every recorded statement. This cannot be undone.", Idle)
    {
        _history = history;
        Info = Summary();
    }

    private string Summary()
    {
        var count = _history.Count;
        return $"{count:N0} {(count == 1 ? "entry" : "entries")}, {FormatSize(_history.FileSize)}";
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
    };

    public override void Run()
    {
        if (!_armed)
        {
            _armed = true;
            ButtonText = "Click again to clear";
            return;
        }

        _armed = false;
        _history.Clear();
        ButtonText = Idle;
        Info = "History cleared. " + Summary();
    }

    public override void Refresh()
    {
        _armed = false;
        ButtonText = Idle;
        Info = Summary();
    }
}
