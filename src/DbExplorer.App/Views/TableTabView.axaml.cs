using Avalonia;
using Avalonia.VisualTree;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

public partial class TableTabView : UserControl
{
    private static readonly TimeSpan NoticeLifetime = TimeSpan.FromSeconds(6);

    private TableTabViewModel? _subscribed;
    private DispatcherTimer? _noticeTimer;

    public TableTabView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe(DataContext as TableTabViewModel);
        DetachedFromVisualTree += (_, _) => Subscribe(null);
        AttachedToVisualTree += (_, _) => Subscribe(DataContext as TableTabViewModel);   // symmetric with detach
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        DataGridView.SelectedRowsChanged += (_, _) => UpdateSelectionState();
    }

    private TableTabViewModel? ViewModel => DataContext as TableTabViewModel;

    // Ctrl+S in a table tab: finish a half-typed cell, then review the SQL and save. (Ctrl+S in a script tab is the
    // editor's own; this handler only exists inside table views.) Also: Ctrl+F the WHERE box, Ctrl+Shift+F the first filter
    // box, Alt+Left / Alt+Right the previous / next page.
    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        switch (e.Key, e.KeyModifiers)
        {
            case (Key.S, KeyModifiers.Control):
                e.Handled = true;
                await SaveAsync(vm);
                break;
            case (Key.F, KeyModifiers.Control):
                e.Handled = true;
                ShowFilters();
                WhereBox.Focus();
                WhereBox.SelectAll();
                break;
            case (Key.F, KeyModifiers.Control | KeyModifiers.Shift):
                e.Handled = true;
                ShowFilters();
                DataGridView.FocusFirstFilter();
                break;
            case (Key.Right, KeyModifiers.Alt):
                e.Handled = true;
                if (vm.NextPageCommand.CanExecute(null)) vm.NextPageCommand.Execute(null);
                break;
            case (Key.Left, KeyModifiers.Alt):
                e.Handled = true;
                if (vm.PrevPageCommand.CanExecute(null)) vm.PrevPageCommand.Execute(null);
                break;
        }
    }

    private async Task SaveAsync(TableTabViewModel vm)
    {
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

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await SaveAsync(vm);
    }

    private void OnAddRowClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && vm.AddRow() is >= 0 and var index) DataGridView.StartEditingNewRow(index);
    }

    private void OnDeleteRowsClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (DataGridView.DeleteSelectedRows() == 0) vm.Notice = "Select the rows to delete first.";
    }

    // ---- filter rows ----

    private void OnFilterToggleClick(object? sender, RoutedEventArgs e) => ApplyFilterVisibility();

    private void ShowFilters()
    {
        if (FilterToggle.IsChecked == true) return;
        FilterToggle.IsChecked = true;
        ApplyFilterVisibility();
    }

    private void ApplyFilterVisibility()
    {
        var on = FilterToggle.IsChecked == true;
        DataGridView.ShowFilterRow = on;
        WhereRow.IsVisible = on;
    }

    // ---- selection ----

    private void UpdateSelectionState()
    {
        var count = DataGridView.SelectedRows.Count;
        SelectionText.Text = count switch { 0 => "", 1 => "1 row selected", _ => $"{count:N0} rows selected" };
        DeleteButton.Content = count > 1 ? $"Delete {count:N0} rows" : "Delete row";
    }

    // ---- structure ----

    // Double-click a column of the Structure sub-tab: show it in the Data sub-tab (scrolls to it and lights it, like Ctrl+L).
    private void OnStructureColumnDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Only a row counts: two quick clicks on the grid's scroll bar or header must not leave the sub-tab.
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is StructureColumn column) column.Jump();
    }

    // The tab control recreates this view when the tab is selected again: always follow the current view-model.
    private void Subscribe(TableTabViewModel? next)
    {
        if (ReferenceEquals(_subscribed, next)) return;
        if (_subscribed is not null)
        {
            _subscribed.FocusColumnRequested -= OnFocusColumn;
            _subscribed.PropertyChanged -= OnViewModelChanged;
        }
        StopNoticeTimer();
        _subscribed = next;
        if (_subscribed is not null)
        {
            _subscribed.FocusColumnRequested += OnFocusColumn;
            _subscribed.PropertyChanged += OnViewModelChanged;
            WatchNotice();
        }
    }

    // A "Saved ..." notice is only a confirmation: it goes away by itself. Anything else (a warning about a key kept for the
    // session only, a refusal) stays until dismissed.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TableTabViewModel.Notice)) WatchNotice();
    }

    private void WatchNotice()
    {
        StopNoticeTimer();
        if (_subscribed?.Notice is not { } notice || !IsConfirmation(notice)) return;
        var timer = new DispatcherTimer { Interval = NoticeLifetime };
        timer.Tick += (_, _) =>
        {
            StopNoticeTimer();
            if (_subscribed is { } vm && vm.Notice == notice) vm.DismissNoticeCommand.Execute(null);
        };
        _noticeTimer = timer;
        timer.Start();
    }

    private static bool IsConfirmation(string notice) =>
        notice.StartsWith("Saved", StringComparison.Ordinal) || notice.StartsWith("Key saved", StringComparison.Ordinal)
        || notice.StartsWith("No pending", StringComparison.Ordinal) || notice.StartsWith("Select the rows", StringComparison.Ordinal);

    private void StopNoticeTimer()
    {
        _noticeTimer?.Stop();
        _noticeTimer = null;
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
