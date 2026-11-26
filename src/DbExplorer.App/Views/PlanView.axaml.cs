using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// The execution plan of a run: the operator canvas (zoom, pan, keyboard walk), the properties / top operators / issues panel,
/// Copy and Save. All state lives in <see cref="PlanResultTab"/>; this class only does what needs a visual: scrolling an
/// operator into view, fitting the zoom to the window, the clipboard and the file picker.
/// </summary>
public partial class PlanView : UserControl
{
    private PlanResultTab? _tab;
    private PlanStatementViewModel? _statement;
    private readonly PlanCanvas _canvas;
    private Point? _zoomAnchor;

    public PlanView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        _canvas = new PlanCanvas(Scroller, Zoomer, OnWheelZoom);
        Scroller.KeyDown += OnKeyDown;
        Scroller.SizeChanged += (_, _) => Dispatcher.UIThread.Post(() => { if (_fitPending) { _fitPending = false; Fit(auto: true); } }, DispatcherPriority.Loaded);
    }

    private bool _fitPending;

    private void Attach()
    {
        if (_tab is not null) _tab.PropertyChanged -= OnTabChanged;
        if (_statement is not null) _statement.PropertyChanged -= OnStatementChanged;
        _tab = DataContext as PlanResultTab;
        _statement = _tab?.Selected;
        if (_tab is null) return;
        _tab.PropertyChanged += OnTabChanged;
        if (_statement is not null) _statement.PropertyChanged += OnStatementChanged;
        ApplyZoom();
        // A big plan opens fitted; a small one at 100%.
        _fitPending = _statement is { } s && (s.CanvasWidth > 1100 || s.CanvasHeight > 700);
        Dispatcher.UIThread.Post(() =>
        {
            _canvas.Home();
            if (_fitPending && Scroller.Bounds.Width > 0) { _fitPending = false; Fit(); }
        }, DispatcherPriority.Loaded);
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlanResultTab.Zoom)) ApplyZoom();
        else if (e.PropertyName == nameof(PlanResultTab.Selected))
        {
            if (_statement is not null) _statement.PropertyChanged -= OnStatementChanged;
            _statement = _tab?.Selected;
            if (_statement is not null) _statement.PropertyChanged += OnStatementChanged;
            Dispatcher.UIThread.Post(() => Fit(auto: true), DispatcherPriority.Loaded);
        }
        else if (e.PropertyName == nameof(PlanResultTab.ShowSidePanel))
        {
            var side = Body.ColumnDefinitions[2];
            var show = _tab?.ShowSidePanel == true;
            side.MinWidth = show ? 240 : 0;
            side.Width = show ? new GridLength(360) : new GridLength(0);
        }
    }

    private void OnStatementChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlanStatementViewModel.SelectedNode) && _statement?.SelectedNode is { } node) BringIntoView(node);
    }

    // ---- zoom and fit ----

    private void ApplyZoom() => _canvas.SetZoom(_tab?.Zoom ?? 1, _zoomAnchor);

    /// <param name="auto">On opening: never smaller than 70%, where the text stays readable (the Fit button shows the whole plan whatever the size).</param>
    private void Fit(bool auto = false)
    {
        if (_tab is null || _statement is not { CanvasWidth: > 0, CanvasHeight: > 0 } s) return;
        var view = Scroller.Bounds.Size;
        if (view.Width < 50 || view.Height < 50) { _fitPending = auto; return; }
        _tab.Zoom = Math.Clamp(Math.Min(Math.Min((view.Width - 4) / s.CanvasWidth, (view.Height - 4) / s.CanvasHeight), 1), auto ? 0.7 : 0.25, 1);
        _canvas.Home();
        // Opening: show the operator that is selected (the costliest), not the empty corner next to the root.
        if (auto && _statement?.SelectedNode is { } hot) Dispatcher.UIThread.Post(() => BringIntoView(hot), DispatcherPriority.Loaded);
    }

    private void SetZoom(double zoom) { if (_tab is not null) _tab.Zoom = Math.Clamp(Math.Round(zoom, 2), 0.25, 2); }

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => SetZoom((_tab?.Zoom ?? 1) + 0.1);

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => SetZoom((_tab?.Zoom ?? 1) - 0.1);

    private void OnFitClick(object? sender, RoutedEventArgs e) => Fit();

    // The zoom changes through the tab: the anchor is remembered while it does, so the canvas zooms around the cursor.
    private void OnWheelZoom(double step, Point cursor)
    {
        _zoomAnchor = cursor;
        SetZoom((_tab?.Zoom ?? 1) + step);
        _zoomAnchor = null;
    }

    // ---- selection ----

    private void OnNodePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: PlanNodeViewModel node } || _statement is null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed && !e.GetCurrentPoint(null).Properties.IsRightButtonPressed) return;
        _statement.SelectedNode = node;
        Scroller.Focus();
        e.Handled = true;
    }

    // Double-click: open the properties of the operator.
    private void OnNodeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_tab is null) return;
        _tab.ShowSidePanel = true;
        SideTabs.SelectedIndex = 0;
    }

    private void OnTopRowClick(object? sender, RoutedEventArgs e) => Select((sender as Control)?.Tag as PlanNodeViewModel);

    private void OnIssueShowClick(object? sender, RoutedEventArgs e)
    {
        Select((sender as Control)?.Tag as PlanNodeViewModel);
        SideTabs.SelectedIndex = 0;
    }

    private void Select(PlanNodeViewModel? node)
    {
        if (node is null || _statement is null) return;
        _statement.SelectedNode = node;
        BringIntoView(node);
    }

    private void OnStatementClick(object? sender, RoutedEventArgs e)
    {
        if (_tab is not null && (sender as Control)?.Tag is PlanStatementViewModel s) _tab.Selected = s;
    }

    private void OnCompareOpening(object? sender, EventArgs e) => _tab?.RefreshCompareChoices();

    private void OnCompareChoiceClick(object? sender, RoutedEventArgs e)
    {
        if (_tab is null || (sender as Control)?.Tag is not PlanCompareChoice choice) return;
        CompareButton.Flyout?.Hide();
        _tab.CompareWithCommand.Execute(choice);
    }

    private void OnShowIssuesClick(object? sender, RoutedEventArgs e)
    {
        if (_tab is null) return;
        _tab.ShowSidePanel = true;
        SideTabs.SelectedIndex = 2;
    }

    private void OnMetricClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { IsChecked: not true } button) button.IsChecked = true;
    }

    private void BringIntoView(PlanNodeViewModel node) => _canvas.BringIntoView(node);

    // ---- keyboard: arrows walk the tree, Ctrl +/-/0 zoom ----

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_tab is null || _statement is null) return;
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add: SetZoom(_tab.Zoom + 0.1); break;
                case Key.OemMinus or Key.Subtract: SetZoom(_tab.Zoom - 0.1); break;
                case Key.D0 or Key.NumPad0: Fit(); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None) return;

        var direction = e.Key switch { Key.Left => "left", Key.Right => "right", Key.Up => "up", Key.Down => "down", _ => null };
        if (direction is null) return;
        var from = _statement.SelectedNode ?? _statement.Nodes.FirstOrDefault();
        if (from is null) return;
        if (_statement.Neighbour(from, direction) is { } next) Select(next);
        e.Handled = true;
    }

    // ---- Copy and Save ----

    private async void OnCopyPlanClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_tab?.Raw is { } raw && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(raw);
        }
        catch (Exception) { /* the clipboard can be busy; nothing to report */ }
    }

    private async void OnCopyIndexClick(object? sender, RoutedEventArgs e) => await CopyAsync(_statement?.MissingIndexScript);

    private async void OnIssueCopyClick(object? sender, RoutedEventArgs e) => await CopyAsync((sender as Control)?.Tag as string);

    private async Task CopyAsync(string? text)
    {
        try
        {
            if (text is not null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch (Exception) { /* busy clipboard */ }
    }

    private async void OnSavePlanClick(object? sender, RoutedEventArgs e)
    {
        if (_tab?.Raw is not { } raw || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var ext = _tab.FileExtension;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the plan",
                SuggestedFileName = _tab.SuggestedFileName,
                DefaultExtension = ext.TrimStart('.'),
                FileTypeChoices = [new FilePickerFileType(_tab.FileKindName) { Patterns = [$"*{ext}"] }],
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(raw);
        }
        catch (Exception ex)
        {
            // Saving is a convenience: say what happened in the details panel's place, never crash the tab.
            System.Diagnostics.Trace.WriteLine("Saving the plan failed: " + ex.Message);
        }
    }
}
