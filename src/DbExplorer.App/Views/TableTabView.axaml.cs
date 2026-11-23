using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

public partial class TableTabView : UserControl
{
    private TableTabViewModel? _subscribed;

    public TableTabView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe(DataContext as TableTabViewModel);
        DetachedFromVisualTree += (_, _) => Subscribe(null);
        AttachedToVisualTree += (_, _) => Subscribe(DataContext as TableTabViewModel);   // symmetric with detach
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private TableTabViewModel? ViewModel => DataContext as TableTabViewModel;

    // Ctrl+S in a table tab: finish a half-typed cell, then review the SQL and save. (Ctrl+S in a script tab is the
    // editor's own; this handler only exists inside table views.)
    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.S || e.KeyModifiers != KeyModifiers.Control || ViewModel is not { } vm) return;
        e.Handled = true;
        try
        {
            if (!DataGridView.CommitPendingEdit()) return;   // an invalid half-typed cell: do not preview without it
            await vm.ReviewAndSaveAsync();
        }
        catch (Exception ex)
        {
            vm.ErrorMessage = "Unexpected error: " + ex.Message;
        }
    }

    private void OnAddRowClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && vm.AddRow() is >= 0 and var index) DataGridView.StartEditingNewRow(index);
    }

    private void OnDeleteRowsClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        foreach (var row in DataGridView.SelectedRows)   // by identity: deleting a new row shifts the indexes
            if (vm.Rows.IndexOf(row) is >= 0 and var index) vm.DeleteRow(index);
    }

    // The tab control recreates this view when the tab is selected again: always follow the current view-model.
    private void Subscribe(TableTabViewModel? next)
    {
        if (ReferenceEquals(_subscribed, next)) return;
        if (_subscribed is not null) _subscribed.FocusColumnRequested -= OnFocusColumn;
        _subscribed = next;
        if (_subscribed is not null) _subscribed.FocusColumnRequested += OnFocusColumn;
    }

    // The column jump (Ctrl+L) needs the grid on screen: from the Structure sub-tab it switches back to Data first. The grid
    // is then (re)attached and has not been measured yet, so the scroll is posted to run after that layout pass instead of
    // acting on it now. (A grid that is detached when asked remembers the request itself: ResultGrid.ScrollToColumn.)
    private void OnFocusColumn(int index)
    {
        if (SubTabs.SelectedIndex != 0)
        {
            SubTabs.SelectedIndex = 0;
            Dispatcher.UIThread.Post(() => DataGridView.ScrollToColumn(index), DispatcherPriority.Loaded);
            return;
        }
        DataGridView.ScrollToColumn(index);
    }
}
