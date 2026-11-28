using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// Two plans next to each other (see <see cref="PlanCompareViewModel"/>). Code-behind only places the two panes (side by side or
/// stacked), applies the zoom, fits it and scrolls the selected operators into view.
/// </summary>
public partial class PlanCompareView : UserControl
{
    private PlanCompareViewModel? _vm;
    private bool _fitPending;
    private readonly FreeCanvas _baseline;
    private readonly FreeCanvas _candidate;
    private Point? _zoomAnchor;

    public PlanCompareView()
    {
        InitializeComponent();
        _baseline = new FreeCanvas(BaselineScroller, BaselineZoomer, OnWheelZoom);
        _candidate = new FreeCanvas(CandidateScroller, CandidateZoomer, OnWheelZoom);
        DataContextChanged += (_, _) => Attach();
        // Tunnel: the scroll viewers would take Ctrl+- / Ctrl++ before the view sees them (same keys as the plan view).
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SizeChanged += (_, _) => { if (_fitPending && BaselineScroller.Bounds.Width > 50) { _fitPending = false; Fit(auto: true); } };
    }

    private void Attach()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.Baseline.PropertyChanged -= OnSelectionChanged;
            _vm.Candidate.PropertyChanged -= OnSelectionChanged;
        }
        _vm = DataContext as PlanCompareViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnVmChanged;
        _vm.Baseline.PropertyChanged += OnSelectionChanged;
        _vm.Candidate.PropertyChanged += OnSelectionChanged;
        ApplyLayout();
        ApplyZoom();
        _fitPending = true;
        Dispatcher.UIThread.Post(() => { if (_fitPending && BaselineScroller.Bounds.Width > 50) { _fitPending = false; Fit(auto: true); } }, DispatcherPriority.Loaded);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlanCompareViewModel.Stacked))
        {
            ApplyLayout();
            Dispatcher.UIThread.Post(() => Fit(auto: true), DispatcherPriority.Loaded);
        }
        else if (e.PropertyName == nameof(PlanCompareViewModel.Zoom)) ApplyZoom();
        else if (e.PropertyName == nameof(PlanCompareViewModel.ShowDifferences))
        {
            var side = Body.ColumnDefinitions[2];
            var show = _vm?.ShowDifferences == true;
            side.MinWidth = show ? 280 : 0;
            side.Width = show ? new GridLength(420) : new GridLength(0);
            Dispatcher.UIThread.Post(() => Fit(auto: true), DispatcherPriority.Loaded);
        }
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlanStatementViewModel.SelectedNode) || sender is not PlanStatementViewModel statement || statement.SelectedNode is not { } node) return;
        (statement == _vm?.Baseline ? _baseline : _candidate).BringIntoView(new Rect(node.X, node.Y, node.Width, node.Height));
    }

    // ---- layout ----

    private void ApplyLayout()
    {
        var stacked = _vm?.Stacked == true;
        Panes.RowDefinitions[1].Height = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Panes.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(CandidatePane, stacked ? 1 : 0);
        Grid.SetColumn(CandidatePane, stacked ? 0 : 1);
    }

    private void OnSideBySideClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.Stacked = false;
        SideBySideButton.IsChecked = true;
    }

    private void OnStackedClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.Stacked = true;
        StackedButton.IsChecked = true;
    }

    // ---- zoom ----

    private void ApplyZoom()
    {
        var z = _vm?.Zoom ?? 1;
        _baseline.SetZoom(z, _zoomAnchor);
        _candidate.SetZoom(z, _zoomAnchor);
    }

    // Both panes zoom around the same spot of their viewport, so they stay aligned.
    private void OnWheelZoom(double step, Point cursor)
    {
        if (_vm is null) return;
        _zoomAnchor = cursor;
        _vm.Zoom += step;
        _zoomAnchor = null;
    }

    /// <param name="auto">On opening: also brings the selected pair into view.</param>
    /// <remarks>Never below 70%, where the operator names stay readable (a bigger plan is scrolled), with a gutter so no card touches the edge.</remarks>
    private void Fit(bool auto = false)
    {
        if (_vm is null) return;
        var view = BaselineScroller.Bounds.Size;
        if (view.Width < 50 || view.Height < 50) { _fitPending = auto; return; }
        var width = Math.Max(_vm.Baseline.CanvasWidth, _vm.Candidate.CanvasWidth);
        var height = Math.Max(_vm.Baseline.CanvasHeight, _vm.Candidate.CanvasHeight);
        const double gutter = PlanView.FitGutter;
        _vm.Zoom = Math.Clamp(Math.Min(Math.Min((view.Width - 2 * gutter) / width, (view.Height - 2 * gutter) / height), 1), PlanView.MinFitZoom, 1);
        _baseline.Frame(gutter);
        _candidate.Frame(gutter);
        if (auto && _vm.SelectedPair is { } pair)
            Dispatcher.UIThread.Post(() =>
            {
                if (pair.Before is { } b && _vm.Baseline.Nodes.FirstOrDefault(n => n.Node == b) is { } left) _baseline.BringIntoView(new Rect(left.X, left.Y, left.Width, left.Height));
                if (pair.After is { } a && _vm.Candidate.Nodes.FirstOrDefault(n => n.Node == a) is { } right) _candidate.BringIntoView(new Rect(right.X, right.Y, right.Width, right.Height));
            }, DispatcherPriority.Loaded);
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e) { if (_vm is not null) _vm.Zoom += 0.1; }

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) { if (_vm is not null) _vm.Zoom -= 0.1; }

    private void OnFitClick(object? sender, RoutedEventArgs e) => Fit();

    // Ctrl+- / Ctrl++ / Ctrl+0, as in the plan view and the ER diagram.
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.KeyModifiers != KeyModifiers.Control) return;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add: _vm.Zoom += 0.1; break;
            case Key.OemMinus or Key.Subtract: _vm.Zoom -= 0.1; break;
            case Key.D0 or Key.NumPad0: Fit(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---- selection ----

    private void OnNodePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: PlanNodeViewModel node } || _vm is null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        _vm.SelectNode(node);
        e.Handled = true;
    }
}
