using Avalonia;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// Centered quick-open box. Created fresh for every opening (the host swaps its content), so it remembers
/// nothing. Closes on Esc, on a click outside the card, and when focus leaves it or the window is deactivated.
/// </summary>
public partial class QuickOpenOverlay : UserControl
{
    private readonly TextBox _queryBox;
    private readonly ListBox _list;
    private QuickOpenViewModel? _vm;
    private IInputElement? _previousFocus;
    private Window? _window;

    public QuickOpenOverlay()
    {
        InitializeComponent();
        _queryBox = this.FindControl<TextBox>("QueryBox")!;
        _list = this.FindControl<ListBox>("ResultList")!;

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        this.FindControl<Border>("Scrim")!.PointerPressed += (_, _) => _vm?.Cancel();
        this.FindControl<Border>("Card")!.PointerPressed += (_, e) => e.Handled = true; // clicks inside must not reach the scrim
        _list.Tapped += OnListTapped;
        // A chip clicked with the mouse gives the keyboard back to the search box: typing goes on.
        AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null)
                Dispatcher.UIThread.Post(() => _queryBox.Focus(), DispatcherPriority.Input);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        _queryBox.LostFocus += OnQueryBoxLostFocus;
        DataContextChanged += (_, _) => Bind(DataContext as QuickOpenViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = this.GetVisualRoot() as Window;
        _previousFocus = _window?.FocusManager?.GetFocusedElement();
        if (_window is not null) _window.Deactivated += OnWindowDeactivated;
        Dispatcher.UIThread.Post(() =>
        {
            _queryBox.Focus();
            _queryBox.SelectAll();   // a remembered query is selected: typing replaces it, Enter reuses it
        }, DispatcherPriority.Input);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is not null) _window.Deactivated -= OnWindowDeactivated;
        var restore = _previousFocus;
        _window = null;
        _previousFocus = null;
        Bind(null);
        // Give focus back to whatever had it before the overlay opened (tree or grid).
        if (restore is not null) Dispatcher.UIThread.Post(() => restore.Focus(), DispatcherPriority.Input);
    }

    private void Bind(QuickOpenViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
        Sync();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QuickOpenViewModel.Results) or nameof(QuickOpenViewModel.SelectedIndex) or nameof(QuickOpenViewModel.Query)) Sync();
    }

    /// <summary>List contents and selection are pushed from the view model in one place, so the list's own selection reset on a new items source cannot leak back.</summary>
    private void Sync()
    {
        if (_vm is null) return;
        if (!ReferenceEquals(_list.ItemsSource, _vm.Results)) _list.ItemsSource = _vm.Results;
        if (_list.SelectedIndex != _vm.SelectedIndex) _list.SelectedIndex = _vm.SelectedIndex;
        HighlightText.SetQuery(_list, _vm.Query);
        if (_vm.SelectedIndex >= 0) _list.ScrollIntoView(_vm.SelectedIndex);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key is >= Key.D1 and <= Key.D5 && _vm.HasTypeToggles)
        {
            _vm.ToggleType(e.Key - Key.D1);
            e.Handled = true;
            return;
        }

        var chip = e.Source as ToggleButton;
        // Shift+Tab goes from the search box to the chips; among them Tab and the arrows move, past either end is the box.
        if (_vm.HasTypeToggles && (chip is not null && e.Key is Key.Tab or Key.Left or Key.Right
                                   || chip is null && e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.Shift))
        {
            var chips = this.FindControl<ItemsControl>("TypeChips")!.GetVisualDescendants().OfType<ToggleButton>().ToList();
            var back = e.Key == Key.Left || e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.Shift;
            var next = chip is null ? 0 : chips.IndexOf(chip) + (back ? -1 : 1);
            if (next >= 0 && next < chips.Count) chips[next].Focus(NavigationMethod.Tab);
            else _queryBox.Focus();
            e.Handled = true;
            return;
        }

        if (chip is not null && e.Key is Key.Enter or Key.Space) return;   // the chip toggles itself
        if (chip is not null && e.Key is Key.Up or Key.Down) _queryBox.Focus();
        switch (e.Key)
        {
            case Key.Down: _vm.MoveDown(); break;
            case Key.Up: _vm.MoveUp(); break;
            case Key.Tab when _vm.HasScopes && e.KeyModifiers == KeyModifiers.None: _vm.SwitchScope(); break;
            case Key.PageDown: _vm.MoveBy(8); break;
            case Key.PageUp: _vm.MoveBy(-8); break;
            case Key.Escape: _vm.Cancel(); break;
            case Key.Enter: _ = ConfirmSafe(null); break;
            default: return;
        }

        e.Handled = true;
    }

    private void OnListTapped(object? sender, TappedEventArgs e)
    {
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as QuickOpenItem;
        if (item is not null) _ = ConfirmSafe(item);
    }

    // ConfirmAsync never throws (failures go to the status bar), so the discarded task cannot fault.
    private Task ConfirmSafe(QuickOpenItem? item) =>
        _vm is null ? Task.CompletedTask : item is null ? _vm.ConfirmAsync() : _vm.ConfirmAsync(item);
    private void OnQueryBoxLostFocus(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (this.GetVisualRoot() is not null && !IsKeyboardFocusWithin) _vm?.Cancel();
        }, DispatcherPriority.Background);

    private void OnWindowDeactivated(object? sender, EventArgs e) => _vm?.Cancel();
}
