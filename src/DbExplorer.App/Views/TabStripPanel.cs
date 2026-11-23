using Avalonia;
using Avalonia.Controls;

namespace DbExplorer.App.Views;

/// <summary>
/// Items panel of the document tab strip: tabs sit side by side at their natural width; when they no longer fit they
/// shrink evenly (like browser tabs) instead of wrapping onto a second row, down to <see cref="MinTabWidth"/>.
/// </summary>
public sealed class TabStripPanel : Panel
{
    public const double MinTabWidth = 96;

    protected override Size MeasureOverride(Size availableSize)
    {
        var natural = new double[Children.Count];
        double total = 0, height = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(double.PositiveInfinity, availableSize.Height));
            natural[i] = Children[i].DesiredSize.Width;
            total += natural[i];
            height = Math.Max(height, Children[i].DesiredSize.Height);
        }

        if (total <= availableSize.Width || double.IsInfinity(availableSize.Width)) return new Size(total, height);

        var widths = Shrink(natural, availableSize.Width);
        for (var i = 0; i < Children.Count; i++) Children[i].Measure(new Size(widths[i], availableSize.Height));
        return new Size(Math.Min(widths.Sum(), availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var natural = Children.Select(c => c.DesiredSize.Width).ToArray();
        var widths = natural.Sum() <= finalSize.Width ? natural : Shrink(Children.Select(c => c.DesiredSize.Width).ToArray(), finalSize.Width);
        double x = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Arrange(new Rect(x, 0, widths[i], finalSize.Height));
            x += widths[i];
        }
        return finalSize;
    }

    /// <summary>Water-filling: short tabs keep their width, the long ones share what is left.</summary>
    private static double[] Shrink(double[] natural, double available)
    {
        var widths = (double[])natural.Clone();
        var open = Enumerable.Range(0, natural.Length).ToList();
        var left = available;
        while (open.Count > 0)
        {
            var share = Math.Max(MinTabWidth, left / open.Count);
            var fitting = open.Where(i => natural[i] <= share).ToList();
            if (fitting.Count == 0)
            {
                foreach (var i in open) widths[i] = share;
                break;
            }
            foreach (var i in fitting)
            {
                widths[i] = natural[i];
                left -= natural[i];
                open.Remove(i);
            }
        }
        return widths;
    }
}
