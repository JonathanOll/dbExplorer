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
/// The command palette card. Created for every opening. Keys go to the view model from the search box; the focus returns to
/// what had it when the palette goes away, before the chosen command runs. Closes on Esc, on a click outside the card, and
/// when the focus leaves it or the window is deactivated.
/// </summary>
public partial class CommandPaletteOverlay : UserControl
{
    private readonly TextBox _queryBox;
    private readonly ListBox _list;
    private CommandPaletteViewModel? _vm;
    private IInputElement? _previousFocus;
    private Window? _window;

    public CommandPaletteOverlay()
    {
        InitializeComponent();
        _queryBox = this.FindControl<TextBox>("QueryBox")!;
        _list = this.FindControl<ListBox>("CommandList")!;

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        this.FindControl<Border>("Scrim")!.PointerPressed += (_, _) => _vm?.Close();
        this.FindControl<Border>("Card")!.PointerPressed += (_, e) => e.Handled = true; // clicks inside must not reach the scrim
        this.FindControl<Button>("StepChip")!.Click += (_, _) =>
        {
            _vm?.Back();
            _queryBox.Focus();
        };
        _list.Tapped += OnListTapped;
        _queryBox.LostFocus += OnQueryBoxLostFocus;
        DataContextChanged += (_, _) => Bind(DataContext as CommandPaletteViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = this.GetVisualRoot() as Window;
        _previousFocus = _window?.FocusManager?.GetFocusedElement();
        if (_window is not null) _window.Deactivated += OnWindowDeactivated;
        Dispatcher.UIThread.Post(() => _queryBox.Focus(), DispatcherPriority.Input);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is not null) _window.Deactivated -= OnWindowDeactivated;
        var restore = _previousFocus;
        _window = null;
        _previousFocus = null;
        Bind(null);
        if (restore is not null) Dispatcher.UIThread.Post(() => restore.Focus(), DispatcherPriority.Input);
    }

    private void Bind(CommandPaletteViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = vm;
        if (_vm is null) return;
        _vm.PropertyChanged += OnViewModelChanged;
        // A command runs only once the focus is back where it was (posted above at Input priority).
        _vm.BeforeRun = async () => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Sync();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CommandPaletteViewModel.Rows) or nameof(CommandPaletteViewModel.SelectedIndex)) Sync();
    }

    // List contents and selection are pushed from the view model in one place, so the list's own selection reset on a new
    // items source cannot leak back.
    private void Sync()
    {
        if (_vm is null) return;
        if (!ReferenceEquals(_list.ItemsSource, _vm.Rows)) _list.ItemsSource = _vm.Rows;
        if (_list.SelectedIndex != _vm.SelectedIndex) _list.SelectedIndex = _vm.SelectedIndex;
        if (_vm.SelectedIndex >= 0) _list.ScrollIntoView(_vm.SelectedIndex);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        var plain = e.KeyModifiers == KeyModifiers.None;
        switch (e.Key)
        {
            case Key.Down: _vm.MoveDown(); break;
            case Key.Up: _vm.MoveUp(); break;
            case Key.PageDown when plain: _vm.MoveBy(9); break;
            case Key.PageUp when plain: _vm.MoveBy(-9); break;
            // With Shift, Home and End keep selecting in the search box.
            case Key.Home when plain: _vm.MoveFirst(); break;
            case Key.End when plain: _vm.MoveLast(); break;
            case Key.Escape: _vm.Close(); break;
            case Key.Enter: _ = Confirm(null); break;
            case Key.Tab:
                // The focus stays in the box; on a command with choices Tab opens them.
                if (plain && _vm.SelectedRow is { HasChoices: true } row) _ = Confirm(row);
                else if (e.KeyModifiers == KeyModifiers.Shift) _vm.Back();
                break;
            case Key.Back when plain && string.IsNullOrEmpty(_queryBox.Text) && _vm.InStep: _vm.Back(); break;
            default: return;
        }

        e.Handled = true;
    }

    private void OnListTapped(object? sender, TappedEventArgs e)
    {
        // Only the row itself: a click on a section caption does nothing.
        for (var visual = e.Source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Border border && border.Classes.Contains("cmdRow") && border.DataContext is PaletteRow row)
            {
                var index = _vm?.Rows.ToList().IndexOf(row) ?? -1;
                if (index >= 0) _vm!.SelectedIndex = index;
                _ = Confirm(row);
                return;
            }
        }
    }

    // ConfirmAsync never throws (failures go to the status bar), so the discarded task cannot fault.
    private Task Confirm(PaletteRow? row) =>
        _vm is null ? Task.CompletedTask : row is null ? _vm.ConfirmAsync() : _vm.ConfirmAsync(row);

    private void OnQueryBoxLostFocus(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (this.GetVisualRoot() is not null && !IsKeyboardFocusWithin) _vm?.Close();
        }, DispatcherPriority.Background);

    private void OnWindowDeactivated(object? sender, EventArgs e) => _vm?.Close();
}
