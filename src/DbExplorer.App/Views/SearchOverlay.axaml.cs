using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// The search overlay (scripts and data). Unlike quick open it stays up when focus moves between its own controls or to another
/// window (a search can take minutes): it closes on Esc, on a click outside the card, and when a result is opened.
/// </summary>
public partial class SearchOverlay : UserControl
{
    private readonly TextBox _queryBox;
    private readonly ListBox _list;
    private SearchViewModel? _vm;
    private IInputElement? _previousFocus;

    public SearchOverlay()
    {
        InitializeComponent();
        _queryBox = this.FindControl<TextBox>("QueryBox")!;
        _list = this.FindControl<ListBox>("ResultList")!;

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        this.FindControl<Border>("Scrim")!.PointerPressed += (_, _) => _vm?.Close();
        this.FindControl<Border>("Card")!.PointerPressed += (_, e) => e.Handled = true;   // clicks inside must not reach the scrim
        _list.Tapped += OnListTapped;
        _list.SelectionChanged += OnListSelectionChanged;
        DataContextChanged += (_, _) => Bind(DataContext as SearchViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _previousFocus = (this.GetVisualRoot() as Window)?.FocusManager?.GetFocusedElement();
        Dispatcher.UIThread.Post(() =>
        {
            _queryBox.Focus();
            _queryBox.SelectAll();   // the last query is selected: typing replaces it, Enter reuses it
        }, DispatcherPriority.Input);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        var restore = _previousFocus;
        _previousFocus = null;
        Bind(null);
        if (restore is not null) Dispatcher.UIThread.Post(() => restore.Focus(), DispatcherPriority.Input);
    }

    private void Bind(SearchViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
        SyncSelection();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchViewModel.SelectedRow)) SyncSelection();
    }

    private bool _syncing;

    private void SyncSelection()
    {
        if (_vm is null) return;
        _syncing = true;
        try
        {
            if (!ReferenceEquals(_list.SelectedItem, _vm.SelectedRow)) _list.SelectedItem = _vm.SelectedRow;
            if (_vm.SelectedRow is not null) _list.ScrollIntoView(_vm.SelectedRow);
        }
        finally { _syncing = false; }
    }

    // A click selects through the list itself; headers are not results, so the selection snaps back to the view model's.
    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _vm is null) return;
        if (_list.SelectedItem is SearchHitRow hit) _vm.SelectedRow = hit;
        else SyncSelection();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        var openCombo = e.Source is ComboBox { IsDropDownOpen: true };
        switch (e.Key)
        {
            case Key.Down when !openCombo: _vm.MoveDown(); break;
            case Key.Up when !openCombo: _vm.MoveUp(); break;
            case Key.Escape when !openCombo: _vm.Close(); break;
            // Enter on a button or a combo box keeps its own meaning; in the text boxes it searches or opens the selected hit.
            case Key.Enter when e.Source is TextBox: _ = ConfirmSafe(); break;
            default: return;
        }

        e.Handled = true;
    }

    private void OnListTapped(object? sender, TappedEventArgs e)
    {
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as SearchHitRow;
        if (item is not null && _vm is not null) _ = OpenSafe(item);
    }

    // The view model reports failures to the status bar itself; these tasks never fault.
    private Task ConfirmSafe() => _vm is null ? Task.CompletedTask : _vm.ConfirmAsync();
    private Task OpenSafe(SearchHitRow hit) => _vm is null ? Task.CompletedTask : _vm.OpenAsync(hit);
}
