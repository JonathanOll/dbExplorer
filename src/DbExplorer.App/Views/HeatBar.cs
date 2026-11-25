using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DbExplorer.App.Views;

/// <summary>
/// The thin cost bar of a plan operator: a track and a fill of <see cref="Value"/> (0 to 1) of its width, both drawn inside the
/// control's own bounds, so it can never run past its card (the stock ProgressBar indicator did under the plan's zoom).
/// </summary>
public sealed class HeatBar : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<HeatBar, double>(nameof(Value));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<HeatBar, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<HeatBar, IBrush?>(nameof(Track));

    static HeatBar()
    {
        AffectsRender<HeatBar>(ValueProperty, FillProperty, TrackProperty);
        HeightProperty.OverrideDefaultValue<HeatBar>(5);
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var radius = h / 2;
        context.DrawRectangle(Track, null, new Rect(0, 0, w, h), radius, radius);
        var fill = Math.Clamp(Value, 0, 1) * w;
        if (fill <= 0) return;
        fill = Math.Max(fill, h);   // a sliver stays a visible dot
        context.DrawRectangle(Fill, null, new Rect(0, 0, Math.Min(fill, w), h), radius, radius);
    }
}
