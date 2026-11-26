using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// Turns a scroll viewer holding a zoomed plan into a free canvas: margins around the plan (see <see cref="PlanViewportMath"/>),
/// drag on the background to pan, wheel to zoom around the cursor (Shift+wheel pans sideways). Shared by the plan view and the
/// two panes of a comparison.
/// </summary>
internal sealed class PlanCanvas
{
    private const double WheelPanPixels = 50;

    private readonly ScrollViewer _scroller;
    private readonly LayoutTransformControl _zoomer;
    private readonly Action<double, Point> _wheelZoom;
    private Size _pad;
    private double _zoom = 1;
    private Point? _panStart;
    private Vector _panOffset;

    /// <param name="wheelZoom">Asked to change the zoom by a step, around a position in the viewport.</param>
    public PlanCanvas(ScrollViewer scroller, LayoutTransformControl zoomer, Action<double, Point> wheelZoom)
    {
        _scroller = scroller;
        _zoomer = zoomer;
        _wheelZoom = wheelZoom;
        // Tunnel: the scroll viewer's own presenter would otherwise scroll with the wheel before anyone else sees it.
        scroller.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        scroller.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Bubble);
        scroller.PointerMoved += OnMoved;
        scroller.PointerReleased += OnReleased;
        scroller.PointerCaptureLost += (_, _) => EndPan();
        // Posted: the size changes in the middle of a layout pass, where the new margins could not be laid out yet.
        scroller.SizeChanged += (_, _) => Dispatcher.UIThread.Post(() => Sync(), DispatcherPriority.Loaded);
    }

    /// <summary>Applies a zoom, keeping the plan point under <paramref name="anchor"/> (default: the centre) in place.</summary>
    public void SetZoom(double zoom, Point? anchor = null)
    {
        var ready = Sync();
        var (offset, old, view) = (_scroller.Offset, _zoom, _scroller.Bounds.Size);
        var content = _zoomer.Bounds.Size / old;
        _zoom = zoom;
        _zoomer.LayoutTransform = new ScaleTransform(zoom, zoom);
        if (!ready || old == zoom) { Sync(); return; }
        var at = anchor ?? new Point(view.Width / 2, view.Height / 2);
        var target = new Vector(
            PlanViewportMath.ZoomOffset(offset.X, at.X, view.Width, content.Width, old, zoom),
            PlanViewportMath.ZoomOffset(offset.Y, at.Y, view.Height, content.Height, old, zoom));
        Sync();
        _scroller.Offset = target;
    }

    /// <summary>The plan back in its place: its top left corner at the top left of the viewport.</summary>
    public void Home()
    {
        if (Sync()) _scroller.Offset = new Vector(_pad.Width, _pad.Height);
    }

    /// <summary>Pans so the operator's card is fully visible (the zoom is taken into account).</summary>
    public void BringIntoView(PlanNodeViewModel node)
    {
        if (!Sync()) return;
        var offset = _scroller.Offset;
        var view = _scroller.Viewport;
        _scroller.Offset = new Vector(
            PlanViewportMath.Reveal(offset.X, view.Width, _pad.Width + node.X * _zoom, node.Width * _zoom),
            PlanViewportMath.Reveal(offset.Y, view.Height, _pad.Height + node.Y * _zoom, node.Height * _zoom));
    }

    // Brings the margins up to date with the viewport and the plan, without moving the plan on screen. False before layout.
    private bool Sync()
    {
        _scroller.UpdateLayout();
        var view = _scroller.Bounds.Size;
        var content = _zoomer.Bounds.Size;
        if (view.Width <= 0 || view.Height <= 0 || content.Width <= 0 || content.Height <= 0) return false;
        var pad = new Size(PlanViewportMath.Pad(view.Width, content.Width), PlanViewportMath.Pad(view.Height, content.Height));
        if (pad == _pad) return true;
        var offset = _scroller.Offset + new Vector(pad.Width - _pad.Width, pad.Height - _pad.Height);
        _pad = pad;
        _zoomer.Margin = new Thickness(pad.Width, pad.Height);
        _scroller.UpdateLayout();
        _scroller.Offset = new Vector(Math.Max(0, offset.X), Math.Max(0, offset.Y));
        return true;
    }

    // ---- wheel ----

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Shift)
        {
            // Windows already turns Shift+wheel into a horizontal delta; other platforms may not.
            var x = _scroller.Offset.X - (e.Delta.X + e.Delta.Y) * WheelPanPixels;
            _scroller.Offset = new Vector(Math.Max(0, x), _scroller.Offset.Y);
        }
        else
        {
            // A sideways swipe of a touchpad stays a pan: left to the scroll viewer.
            if (e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Control) || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y)) return;
            _wheelZoom(PlanViewportMath.WheelStep(e.Delta.Y), e.GetPosition(_scroller));
        }
        e.Handled = true;
    }

    // ---- pan: drag the background ----

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _scroller.Focus();
        var point = e.GetCurrentPoint(_scroller);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsMiddleButtonPressed) return;
        if (e.Source is Visual v && v.FindAncestorOfType<Border>(true) is { } b && b.Classes.Contains("plannode")) return;   // a card handles its own press
        if (e.Source is ScrollBar || (e.Source as Visual)?.FindAncestorOfType<ScrollBar>() is not null) return;
        _panStart = e.GetPosition(_scroller);
        _panOffset = _scroller.Offset;
        e.Pointer.Capture(_scroller);
        _scroller.Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_panStart is not { } start) return;
        var d = start - e.GetPosition(_scroller);
        _scroller.Offset = new Vector(Math.Max(0, _panOffset.X + d.X), Math.Max(0, _panOffset.Y + d.Y));
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_panStart is null) return;
        e.Pointer.Capture(null);
        EndPan();
    }

    private void EndPan()
    {
        _panStart = null;
        _scroller.Cursor = null;
    }
}
