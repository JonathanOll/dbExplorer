namespace DbExplorer.App.ViewModels;

/// <summary>
/// The geometry of the free plan canvas, one axis at a time (call it once for x, once for y). The content is surrounded by an
/// empty margin on both sides, so it can be panned far past its edges; the margin is sized so that a strip of it stays on
/// screen. "Offset" is the scroll offset in that padded extent; "content" is the canvas size at zoom 1 unless said otherwise.
/// </summary>
public static class PlanViewportMath
{
    /// <summary>How much of the plan stays on screen at the far end of a pan.</summary>
    public const double Keep = 120;

    private const double RevealPad = 24;

    /// <summary>The margin on each side of the content (<paramref name="content"/> already zoomed).</summary>
    public static double Pad(double view, double content) =>
        Math.Max(0, view - Math.Max(0, Math.Min(Math.Min(Keep, view / 2), content)));

    /// <summary>The offset kept inside the padded extent.</summary>
    public static double Clamp(double offset, double view, double content) =>
        Math.Clamp(offset, 0, Math.Max(0, content + 2 * Pad(view, content) - view));

    /// <summary>
    /// The offset after a zoom change that leaves the content point under <paramref name="anchor"/> (a position in the viewport)
    /// where it is.
    /// </summary>
    public static double ZoomOffset(double offset, double anchor, double view, double content, double oldZoom, double newZoom)
    {
        var point = (offset + anchor - Pad(view, content * oldZoom)) / oldZoom;
        return Clamp(point * newZoom + Pad(view, content * newZoom) - anchor, view, content * newZoom);
    }

    /// <summary>
    /// The offset that shows the segment <paramref name="start"/> / <paramref name="length"/> (in extent coordinates), moving as
    /// little as possible; unchanged when it is already fully visible.
    /// </summary>
    public static double Reveal(double offset, double view, double start, double length)
    {
        if (start < offset) return Math.Max(0, start - RevealPad);
        if (start + length > offset + view) return Math.Max(0, start + length - view + RevealPad);
        return offset;
    }

    /// <summary>The zoom change of a wheel move: 0.1 per notch, never so small that the two-decimal zoom would swallow it.</summary>
    public static double WheelStep(double delta) =>
        delta == 0 ? 0 : Math.Sign(delta) * Math.Max(0.01, 0.1 * Math.Min(1, Math.Abs(delta)));
}
