namespace DbExplorer.Core.Diagrams;

/// <summary>A rectangle in diagram coordinates (zoom 1).</summary>
public readonly record struct DiagramRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    public bool Intersects(DiagramRect other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;
}

/// <summary>The size of a table card. Text widths are estimated (layout runs off the UI thread); the drawing trims.</summary>
public static class DiagramMetrics
{
    public const double HeaderHeight = 30;
    public const double RowHeight = 20;
    public const double BottomPad = 6;
    public const double MinWidth = 160;
    public const double MaxWidth = 340;

    /// <summary>Space left of a column name (the key mark).</summary>
    public const double MarkWidth = 30;

    private const double NameChar = 6.7;
    private const double TypeChar = 5.9;
    private const double TitleChar = 7.6;

    /// <summary>The columns a card lists in a mode.</summary>
    public static IReadOnlyList<DiagramColumn> VisibleColumns(DiagramTable table, DiagramColumnMode mode) =>
        mode switch
        {
            DiagramColumnMode.All => table.Columns,
            DiagramColumnMode.Keys => table.Columns.Where(c => c.IsKey).ToList(),
            _ => [],
        };

    public static (double Width, double Height) Size(DiagramTable table, DiagramColumnMode mode, bool showSchema)
    {
        var columns = VisibleColumns(table, mode);
        var title = (table.Ref.Name.Length + (showSchema && table.Ref.Schema is { } s ? s.Length + 1 : 0)) * TitleChar + 24;
        var rows = columns.Count == 0 ? 0 : columns.Max(c =>
            MarkWidth + c.Name.Length * NameChar + 16 + c.DataType.Length * TypeChar + 10);
        var width = Math.Clamp(Math.Max(title, rows), MinWidth, MaxWidth);
        var height = HeaderHeight + columns.Count * RowHeight + (columns.Count > 0 ? BottomPad : 0);
        return (Math.Ceiling(width), height);
    }

    /// <summary>
    /// The height, inside a card, where a relation on <paramref name="column"/> attaches: the middle of its row, or the
    /// middle of the header when the card does not list it.
    /// </summary>
    public static double AnchorY(IReadOnlyList<DiagramColumn> visible, string? column)
    {
        for (var i = 0; i < visible.Count; i++)
            if (string.Equals(visible[i].Name, column, StringComparison.OrdinalIgnoreCase))
                return HeaderHeight + (i + 0.5) * RowHeight;
        return HeaderHeight / 2;
    }
}
