using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.ViewModels;

/// <summary>The query history: statements run from script tabs are recorded, View > Query history (Ctrl+Shift+H) shows them.</summary>
public partial class MainViewModel
{
    private QueryHistory? _history;
    private IHistoryDialogs? _historyDialogs;
    private IDialogService? _historyConfirm;

    /// <summary>Gives the shell its history store and window. Only stores them: the file is read when the window first opens.</summary>
    public void UseHistory(QueryHistory history, IHistoryDialogs dialogs, IDialogService confirm)
    {
        _history = history;
        _historyDialogs = dialogs;
        _historyConfirm = confirm;
    }

    /// <summary>The store behind the history window and the options' "Clear history" button; null before <see cref="UseHistory"/>.</summary>
    public QueryHistory? History => _history;

    [RelayCommand]
    public async Task OpenHistoryAsync()
    {
        try
        {
            if (_history is not { } history || _historyDialogs is not { } dialogs || _historyConfirm is not { } confirm) return;
            var vm = new HistoryViewModel(
                history,
                id => Tree.Config.Connections.Any(c => c.Id == id),
                CurrentConnection?.Id,
                confirm,
                OpenHistoryEntryAsync,
                CopyToClipboard);
            var loading = vm.LoadAsync();
            await dialogs.ShowHistoryAsync(vm);
            await loading;
        }
        catch (Exception ex)
        {
            Status.Error($"Could not open the history: {ErrorText.Describe(ex)}");
        }
    }

    /// <summary>
    /// Opens a recorded statement in a new script tab: on its connection when that still exists, else on the active one.
    /// <paramref name="run"/> runs it right away. Says so in the status bar when there is no connection to use.
    /// </summary>
    public Task OpenHistoryEntryAsync(HistoryEntry entry, bool run)
    {
        var connection = Tree.Config.Connections.FirstOrDefault(c => c.Id == entry.ConnectionId) ?? ScriptTarget();
        if (connection is null)
            throw new InvalidOperationException("The connection of this statement no longer exists and no other one is active. Select a connection first.");

        var tab = CreateScriptTab(connection, null);
        tab.Text = entry.Sql;
        Tabs.Add(tab);
        ActiveTab = tab;
        // Not awaited: the history window closes now, the run shows in the tab (RunAsync reports its own failures).
        if (run) _ = tab.RunScriptAsync();
        return Task.CompletedTask;
    }
}
