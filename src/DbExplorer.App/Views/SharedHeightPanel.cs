using Avalonia;
using Avalonia.Controls;

namespace DbExplorer.App.Views;

/// <summary>
/// Stacks sections inside a given height: a section that needs less than its share gets what it asks for, the others share
/// the rest. Each child is measured with a finite height, so a data grid in it scrolls and virtualizes its own rows. A plain
/// StackPanel measures with an infinite height, which a DataGrid cannot work with: rows become unreachable and the scroll jumps.
/// </summary>
public sealed class SharedHeightPanel : Panel
{
    public static readonly StyledProperty<double> FitHeightProperty =
        AvaloniaProperty.Register<SharedHeightPanel, double>(nameof(FitHeight));

    public static readonly StyledProperty<double> MinShareProperty =
        AvaloniaProperty.Register<SharedHeightPanel, double>(nameof(MinShare), 130);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<SharedHeightPanel, double>(nameof(Spacing));

    static SharedHeightPanel() => AffectsMeasure<SharedHeightPanel>(FitHeightProperty, MinShareProperty, SpacingProperty);

    /// <summary>The height to share, usually the viewport of the surrounding ScrollViewer (which offers an infinite one).</summary>
    public double FitHeight { get => GetValue(FitHeightProperty); set => SetValue(FitHeightProperty, value); }

    /// <summary>No section is squeezed below this: in a very short window the panel grows and the ScrollViewer around it scrolls.</summary>
    public double MinShare { get => GetValue(MinShareProperty); set => SetValue(MinShareProperty, value); }

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private readonly Dictionary<Control, double> _heights = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        _heights.Clear();
        var shown = Children.Where(c => c.IsVisible).ToList();
        if (shown.Count == 0) return default;
        var gaps = Spacing * (shown.Count - 1);
        var budget = Math.Max(MinShare, FitHeight - Margin.Top - Margin.Bottom - gaps);

        // What each section would take if it had the whole height to itself.
        foreach (var child in shown)
        {
            child.Measure(new Size(availableSize.Width, budget));
            _heights[child] = child.DesiredSize.Height;
        }

        // Smallest first: what it leaves of its share goes to the larger ones.
        var left = shown.Count;
        foreach (var child in shown.OrderBy(c => _heights[c]))
        {
            var share = Math.Max(MinShare, budget / left--);
            if (_heights[child] > share) _heights[child] = share;
            budget = Math.Max(0, budget - _heights[child]);
        }

        double width = 0;
        foreach (var child in shown)
        {
            child.Measure(new Size(availableSize.Width, _heights[child]));
            width = Math.Max(width, child.DesiredSize.Width);
        }
        return new Size(width, gaps + _heights.Values.Sum());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0.0;
        foreach (var child in Children.Where(c => c.IsVisible))
        {
            // The allotted height, not the desired one: a data grid asks for a few pixels more or less as it scrolls.
            var height = _heights.GetValueOrDefault(child, child.DesiredSize.Height);
            child.Arrange(new Rect(0, y, finalSize.Width, height));
            y += height + Spacing;
        }
        return finalSize;
    }
}
