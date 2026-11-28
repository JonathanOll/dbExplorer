using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DbExplorer.Core.Diagrams;

namespace DbExplorer.App.Views;

/// <summary>The theme brushes and fonts a diagram is drawn with, read from the resources of a control.</summary>
internal sealed record DiagramPalette(
    IBrush Background, IBrush Card, IBrush Header, IBrush Border, IBrush Line, IBrush Text, IBrush Dim,
    IBrush Accent, IBrush Link, IBrush Key, IBrush Hairline, Geometry? KeyIcon)
{
    public static DiagramPalette From(StyledElement owner)
    {
        object? Find(string key) => owner.TryFindResource(key, owner.ActualThemeVariantOf(), out var value) ? value : null;
        // A missing token draws nothing (not a stray named colour) and says so in the trace, so it is seen in dev.
        IBrush Brush(string key)
        {
            if (Find(key) is IBrush brush) return brush;
            Trace.TraceWarning($"DiagramPalette: theme token '{key}' not found");
            return Brushes.Transparent;
        }
        return new DiagramPalette(
            Brush("DbxContentBg"), Brush("DbxSurface"), Brush("DbxGridHeaderBg"), Brush("DbxBorder"), Brush("DbxBorderStrong"),
            Brush("DbxText"), Brush("DbxTextDim"), Brush("DbxAccent"), Brush("DbxLink"), Brush("DbxIconKey"),
            Brush("DbxBorderSubtle"), Find("IconKey") as Geometry);
    }
}

/// <summary>
/// Draws a diagram: only what crosses the given clip, with less detail when zoomed out (no columns below 45%, no text when
/// it would be unreadable). Used by the canvas and by the PNG export. Keeps the laid out texts between frames.
/// </summary>
internal sealed class DiagramPainter(DiagramPalette palette)
{
    /// <summary>Below this zoom the cards show their name only.</summary>
    public const double DetailZoom = 0.45;

    private const double MarksZoom = 0.3;
    private const double Radius = 7;
    private const double TextSize = 11.5;

    private readonly Dictionary<(string Text, double Size, bool Bold, IBrush Brush, double Width), FormattedText> _texts = new();

    public DiagramPalette Palette => palette;

    /// <summary>The number of cards the last <see cref="Draw"/> drew.</summary>
    public int DrawnTables { get; private set; }

    /// <param name="selected">The table whose relations are highlighted, or -1.</param>
    /// <param name="hovered">As <paramref name="selected"/>, and the tables it is not linked to are dimmed; -1 for none.</param>
    public void Draw(DrawingContext context, DiagramScene scene, Rect clip, double zoom, int selected = -1, int hovered = -1)
    {
        var view = new DiagramRect(clip.X, clip.Y, clip.Width, clip.Height);
        var focus = hovered >= 0 ? hovered : selected;

        // Lines keep a visible width when zoomed out.
        var thin = Math.Max(1.2, 0.8 / zoom);
        var plain = new Pen(palette.Line, thin);
        var faded = new Pen(palette.Border, thin);
        var strong = new Pen(palette.Accent, Math.Max(2, 1.6 / zoom));
        var highlighted = new List<DiagramRelation>();
        foreach (var relation in scene.Model.Relations)
        {
            if (focus >= 0 && (relation.Child == focus || relation.Parent == focus)) highlighted.Add(relation);
            else Edge(context, scene.Route(relation), relation, view, zoom, hovered >= 0 ? faded : plain);
        }
        // On top of the other lines.
        foreach (var relation in highlighted) Edge(context, scene.Route(relation), relation, view, zoom, strong);

        DrawnTables = 0;
        foreach (var i in scene.DrawOrder)
        {
            var r = scene.Rect(i);
            if (!r.Intersects(view)) continue;
            DrawnTables++;
            var related = focus >= 0 && (i == focus || scene.AreLinked(i, focus));
            using var _ = context.PushOpacity(hovered >= 0 && !related ? 0.45 : 1);
            Card(context, scene, i, view, zoom, i == selected, related);
        }
    }

    private static void Edge(DrawingContext context, DiagramRoute route, DiagramRelation relation, DiagramRect view, double zoom, Pen pen)
    {
        if (!route.Bounds.Intersects(view)) return;
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(At(route.Start), false);
            g.CubicBezierTo(At(route.Control1), At(route.Control2), At(route.End));
            g.EndFigure(false);
        }
        // An optional key (it accepts NULL) is dashed.
        var stroke = relation.Optional ? new Pen(pen.Brush, pen.Thickness, new DashStyle([4, 3], 0)) : pen;
        context.DrawGeometry(null, stroke, line);
        if (zoom < MarksZoom) return;

        // Cardinality: a crow's foot where many rows can point at one, a bar for "one", a ring for "none allowed".
        var (start, s) = (At(route.Start), route.StartSide);
        if (relation.Unique) Bar(context, pen, start, s);
        else
        {
            var tip = start + new Vector(10 * s, 0);
            context.DrawLine(pen, tip, start + new Vector(0, -5));
            context.DrawLine(pen, tip, start + new Vector(0, 5));
        }
        var (end, e) = (At(route.End), route.EndSide);
        Bar(context, pen, end, e);
        if (relation.Optional) context.DrawEllipse(null, pen, end + new Vector(13 * e, 0), 3, 3);
    }

    private static void Bar(DrawingContext context, Pen pen, Point at, int side)
    {
        var x = at.X + 7 * side;
        context.DrawLine(pen, new Point(x, at.Y - 5), new Point(x, at.Y + 5));
    }

    private static Point At(DiagramPoint p) => new(p.X, p.Y);

    private void Card(DrawingContext context, DiagramScene scene, int index, DiagramRect view, double zoom, bool selected, bool related)
    {
        var table = scene.Model.Tables[index];
        var r = scene.Rect(index);
        var rect = new Rect(r.X, r.Y, r.Width, r.Height);
        var outline = selected ? new Pen(palette.Accent, Math.Max(2, 2 / zoom))
            : new Pen(related ? palette.Accent : palette.Line, Math.Max(1, 0.8 / zoom));
        context.DrawRectangle(palette.Card, null, rect, Radius, Radius);

        var title = scene.ShowSchema ? table.Ref.ToString() : table.Ref.Name;
        var header = new Rect(r.X, r.Y, r.Width, Math.Min(DiagramMetrics.HeaderHeight, r.Height));
        var onlyHeader = r.Height <= DiagramMetrics.HeaderHeight;
        context.DrawRectangle(palette.Header, null,
            new RoundedRect(header, new CornerRadius(Radius, Radius, onlyHeader ? Radius : 0, onlyHeader ? Radius : 0)));

        if (zoom < DetailZoom)
        {
            // Zoomed out: one name, as large as the card allows, while it still makes a shape on screen (a 40 px name
            // at 13% is 5 px: the overview of a big database still says where each table is).
            var size = Math.Min(Math.Min(11 / zoom, 40), r.Height * 0.7);
            if (size * zoom >= 4)
            {
                var name = Text(title, size, true, palette.Text, r.Width - 8);
                var y = onlyHeader ? r.Y + (r.Height - name.Height) / 2
                    : r.Y + DiagramMetrics.HeaderHeight + (r.Height - DiagramMetrics.HeaderHeight - name.Height) / 2;
                context.DrawText(name, new Point(r.X + Math.Max(4, (r.Width - name.Width) / 2), y));
            }
        }
        else
        {
            var name = Text(title, 12.5, true, palette.Text, r.Width - 20);
            context.DrawText(name, new Point(r.X + 10, r.Y + (DiagramMetrics.HeaderHeight - name.Height) / 2));
            if (!onlyHeader)
                context.DrawLine(new Pen(palette.Hairline), new Point(r.X, header.Bottom), new Point(r.Right, header.Bottom));
            Rows(context, scene.Columns(index), r, view);
        }
        context.DrawRectangle(null, outline, rect, Radius, Radius);
    }

    private void Rows(DrawingContext context, IReadOnlyList<DiagramColumn> columns, DiagramRect card, DiagramRect view)
    {
        // Only the rows in view: a table can have hundreds of columns.
        var top = card.Y + DiagramMetrics.HeaderHeight;
        var first = Math.Max(0, (int)((view.Y - top) / DiagramMetrics.RowHeight));
        var last = Math.Min(columns.Count - 1, (int)((view.Bottom - top) / DiagramMetrics.RowHeight));
        for (var i = first; i <= last; i++)
        {
            var column = columns[i];
            var middle = top + (i + 0.5) * DiagramMetrics.RowHeight;
            if (column.IsPrimaryKey && palette.KeyIcon is { } icon)
            {
                // The icons are drawn on a 24 x 24 grid.
                using var _ = context.PushTransform(Matrix.CreateScale(0.5, 0.5) * Matrix.CreateTranslation(card.X + 9, middle - 6));
                context.DrawGeometry(palette.Key, null, icon);
            }
            else if (column.IsForeignKey)
            {
                var mark = Text("FK", 9, true, palette.Link, 20);
                context.DrawText(mark, new Point(card.X + 9, middle - mark.Height / 2));
            }

            var room = card.Width - DiagramMetrics.MarkWidth - 10;
            var type = Text(column.DataType, 11, false, palette.Dim, room * 0.5);
            var name = Text(column.Name, TextSize, column.IsPrimaryKey, column.IsForeignKey ? palette.Link : palette.Text,
                Math.Max(20, room - type.Width - 10));
            context.DrawText(name, new Point(card.X + DiagramMetrics.MarkWidth, middle - name.Height / 2));
            context.DrawText(type, new Point(card.Right - 10 - type.Width, middle - type.Height / 2));
        }
    }

    private FormattedText Text(string text, double size, bool bold, IBrush brush, double width)
    {
        size = Math.Round(size, 1);
        width = Math.Max(1, Math.Floor(width));
        if (_texts.TryGetValue((text, size, bold, brush, width), out var known)) return known;
        if (_texts.Count > 20_000) _texts.Clear();
        var made = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, brush)
        {
            MaxTextWidth = width,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        return _texts[(text, size, bold, brush, width)] = made;
    }

    // ---- PNG export ----

    private const double MaxPixels = 48_000_000;
    private const double MaxSide = 16_000;

    /// <summary>The pixels per diagram unit of an export: 1.5, less for a diagram too large for one picture.</summary>
    public static double ExportScale(double width, double height) =>
        Math.Min(1.5, Math.Min(Math.Sqrt(MaxPixels / (width * height)), MaxSide / Math.Max(width, height)));

    /// <summary>Writes the whole diagram, with its columns, as a PNG. Call on the UI thread.</summary>
    public void SavePng(DiagramScene scene, Stream stream)
    {
        var scale = ExportScale(scene.Width, scene.Height);
        var pixels = new PixelSize(Math.Max(1, (int)(scene.Width * scale)), Math.Max(1, (int)(scene.Height * scale)));
        using var bitmap = new RenderTargetBitmap(pixels);
        using (var context = bitmap.CreateDrawingContext())
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            var all = new Rect(0, 0, scene.Width, scene.Height);
            context.FillRectangle(palette.Background, all);
            // Full detail whatever the scale: the picture can be zoomed afterwards.
            Draw(context, scene, all, 1);
        }
        bitmap.Save(stream);
    }
}
