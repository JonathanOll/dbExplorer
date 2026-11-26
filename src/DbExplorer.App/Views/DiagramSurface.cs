using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DbExplorer.Core.Diagrams;

namespace DbExplorer.App.Views;

internal static class ThemeVariantExtensions
{
    public static ThemeVariant ActualThemeVariantOf(this StyledElement element) =>
        (element as IThemeVariantHost)?.ActualThemeVariant ?? Application.Current?.ActualThemeVariant ?? ThemeVariant.Light;
}

/// <summary>
/// The drawing of a diagram inside a free canvas: one control that paints the tables in view, whatever their number. Hover
/// highlights the relations of a table, a press selects it and starts dragging it; a press on the background is left to the
/// canvas (pan).
/// </summary>
public sealed class DiagramSurface : Control, ICustomHitTest
{
    private const double DragThreshold = 3;

    public static readonly StyledProperty<DiagramScene?> SceneProperty =
        AvaloniaProperty.Register<DiagramSurface, DiagramScene?>(nameof(Scene));

    public static readonly StyledProperty<int> SelectedTableProperty =
        AvaloniaProperty.Register<DiagramSurface, int>(nameof(SelectedTable), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<DiagramSurface, double>(nameof(Zoom), 1);

    private DiagramPainter? _painter;
    private DiagramScene? _watched;
    private int _hovered = -1;
    private (int Table, Point Press, Vector Grab, bool Moved)? _drag;

    static DiagramSurface()
    {
        AffectsRender<DiagramSurface>(SceneProperty, SelectedTableProperty, ZoomProperty);
        AffectsMeasure<DiagramSurface>(SceneProperty);
    }

    public DiagramSurface()
    {
        ActualThemeVariantChanged += (_, _) =>
        {
            _painter = null;
            InvalidateVisual();
        };
    }

    public DiagramScene? Scene { get => GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public int SelectedTable { get => GetValue(SelectedTableProperty); set => SetValue(SelectedTableProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    /// <summary>The table under the pointer, or -1.</summary>
    public int HoveredTable => _hovered;

    /// <summary>How many cards the last frame drew (the others were outside the view).</summary>
    public int DrawnTables => _painter?.DrawnTables ?? 0;

    /// <summary>A table was double-clicked.</summary>
    public event Action<int>? TableActivated;

    internal DiagramPainter Painter => _painter ??= new DiagramPainter(DiagramPalette.From(this));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SceneProperty) return;
        _hovered = -1;
        _drag = null;
        Watch(Scene);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _painter = null;
        Watch(Scene);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Watch(null);   // the scene outlives the view: it must not keep it alive
    }

    private void Watch(DiagramScene? scene)
    {
        if (_watched is not null) _watched.Changed -= OnSceneChanged;
        _watched = scene;
        if (scene is not null) scene.Changed += OnSceneChanged;
    }

    private void OnSceneChanged()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        Scene is { } s ? new Size(s.Width, s.Height) : default;

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public override void Render(DrawingContext context)
    {
        if (Scene is not { } scene) return;
        Painter.Draw(context, scene, Visible(), Zoom, SelectedTable, _hovered);
    }

    // The part of the drawing the scroll viewer shows (with a small margin); everything when there is none.
    private Rect Visible()
    {
        var all = new Rect(Bounds.Size);
        if (this.FindAncestorOfType<ScrollViewer>() is not { } host || host.TransformToVisual(this) is not { } toHere) return all;
        return new Rect(host.Bounds.Size).TransformToAABB(toHere).Inflate(40);
    }

    // ---- pointer ----

    private void Hover(int table)
    {
        if (table == _hovered) return;
        _hovered = table;
        Cursor = table >= 0 ? new Cursor(StandardCursorType.Hand) : null;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Scene is not { } scene) return;
        var at = e.GetPosition(this);
        if (_drag is { } drag)
        {
            var distance = at - drag.Press;
            // A click that trembles must not move the table (the threshold is in screen pixels).
            if (!drag.Moved && Math.Max(Math.Abs(distance.X), Math.Abs(distance.Y)) * Zoom < DragThreshold) return;
            _drag = drag with { Moved = true };
            scene.Move(drag.Table, at.X - drag.Grab.X, at.Y - drag.Grab.Y);
            return;
        }
        // While the canvas pans, the pointer is captured elsewhere and nothing is hovered.
        Hover(scene.HitTest(at.X, at.Y));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_drag is null) Hover(-1);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Scene is not { } scene) return;
        var at = e.GetPosition(this);
        var table = scene.HitTest(at.X, at.Y);
        var buttons = e.GetCurrentPoint(this).Properties;
        if (buttons.IsRightButtonPressed) SelectedTable = table;
        if (table < 0 || !buttons.IsLeftButtonPressed) return;   // the background: the canvas pans

        this.FindAncestorOfType<ScrollViewer>()?.Focus();
        SelectedTable = table;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            TableActivated?.Invoke(table);
            return;
        }
        var rect = scene.Rect(table);
        _drag = (table, at, at - new Point(rect.X, rect.Y), false);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null) return;
        _drag = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = null;
    }
}
