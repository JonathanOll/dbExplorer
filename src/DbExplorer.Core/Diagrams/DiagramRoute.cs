namespace DbExplorer.Core.Diagrams;

public readonly record struct DiagramPoint(double X, double Y);

/// <summary>
/// The curve of one relation: a cubic Bezier from the child's side (<see cref="Start"/>) to the parent's
/// (<see cref="End"/>). <see cref="StartSide"/> / <see cref="EndSide"/> give the direction away from each card (-1 left, 1
/// right), for the cardinality marks.
/// </summary>
public readonly record struct DiagramRoute(
    DiagramPoint Start, DiagramPoint Control1, DiagramPoint Control2, DiagramPoint End, int StartSide, int EndSide)
{
    private const double MinBend = 40;

    /// <summary>A box that holds the whole curve and its end marks.</summary>
    public DiagramRect Bounds
    {
        get
        {
            var left = Math.Min(Math.Min(Start.X, End.X), Math.Min(Control1.X, Control2.X));
            var right = Math.Max(Math.Max(Start.X, End.X), Math.Max(Control1.X, Control2.X));
            var top = Math.Min(Start.Y, End.Y);
            var bottom = Math.Max(Start.Y, End.Y);
            return new DiagramRect(left - 12, top - 12, right - left + 24, bottom - top + 24);
        }
    }

    /// <summary>Between the facing sides when the cards are apart; around the same side when they overlap horizontally.</summary>
    public static DiagramRoute Between(DiagramRect child, double childAnchor, DiagramRect parent, double parentAnchor, bool self)
    {
        var y1 = child.Y + childAnchor;
        var y2 = parent.Y + parentAnchor;
        int s1, s2;
        if (self) (s1, s2) = (1, 1);
        else if (parent.Right + MinBend / 2 < child.X) (s1, s2) = (-1, 1);
        else if (child.Right + MinBend / 2 < parent.X) (s1, s2) = (1, -1);
        else (s1, s2) = child.CenterX <= parent.CenterX ? (-1, -1) : (1, 1);

        // A self reference whose two ends fall on the same row still needs a visible loop.
        if (self && Math.Abs(y1 - y2) < 1) y2 = y1 - DiagramMetrics.HeaderHeight / 2;

        var x1 = s1 < 0 ? child.X : child.Right;
        var x2 = s2 < 0 ? parent.X : parent.Right;
        var bend = s1 == s2 ? MinBend + Math.Min(60, Math.Abs(y2 - y1) / 6) : Math.Max(MinBend, Math.Abs(x2 - x1) / 2);
        // On the same side both handles reach past the outermost edge, or the curve would cross the wider card.
        var outer = s1 < 0 ? Math.Min(x1, x2) : Math.Max(x1, x2);
        var c1 = s1 == s2 ? outer + s1 * bend : x1 + s1 * bend;
        var c2 = s1 == s2 ? c1 : x2 + s2 * bend;
        return new DiagramRoute(new(x1, y1), new(c1, y1), new(c2, y2), new(x2, y2), s1, s2);
    }
}
