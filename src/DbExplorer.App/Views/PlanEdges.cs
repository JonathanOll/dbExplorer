using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// The arrows of a plan, drawn under the operator cards: from every operator to its parent (data flows right to left), as thick
/// as the rows it carries, with the row count over the line. The path of the selected operator to the root is lit.
/// Colours are theme brushes, looked up when drawing, so a live light/dark switch repaints.
/// </summary>
public sealed class PlanEdges : Control
{
    public static readonly StyledProperty<PlanStatementViewModel?> StatementProperty =
        AvaloniaProperty.Register<PlanEdges, PlanStatementViewModel?>(nameof(Statement));

    private PlanStatementViewModel? _watched;

    public PlanStatementViewModel? Statement
    {
        get => GetValue(StatementProperty);
        set => SetValue(StatementProperty, value);
    }

    static PlanEdges()
    {
        AffectsRender<PlanEdges>(StatementProperty);
        AffectsMeasure<PlanEdges>(StatementProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != StatementProperty) return;
        if (_watched is not null) foreach (var n in _watched.Nodes) n.PropertyChanged -= OnNodeChanged;
        _watched = Statement;
        if (_watched is not null) foreach (var n in _watched.Nodes) n.PropertyChanged += OnNodeChanged;
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlanNodeViewModel.IsOnPath)) InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        Statement is { } s ? new Size(s.CanvasWidth, s.CanvasHeight) : default;

    // A missing token draws nothing (not a stray named colour) and says so in the trace, so it is seen in dev.
    private IBrush Brush(string key)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b) return b;
        Trace.TraceWarning($"PlanEdges: theme token '{key}' not found");
        return Brushes.Transparent;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Statement is not { } statement) return;

        var normal = Brush("DbxBorderStrong");
        var lit = Brush("DbxAccent");
        var text = Brush("DbxText");
        var chip = Brush("DbxContentBg");
        var typeface = new Typeface(FontFamily.Default);

        // Unlit edges first, so a lit one is never hidden under a crossing line.
        foreach (var pass in new[] { false, true })
            foreach (var child in statement.Nodes)
            {
                if (child.Parent is not { } parent || child.IsOnPath != pass) continue;

                var start = new Point(child.Box.X, child.Box.CenterY);
                var end = new Point(parent.Box.Right, parent.Box.CenterY);
                var midX = end.X + (start.X - end.X) / 2;
                var brush = pass ? lit : normal;
                var pen = new Pen(brush, pass ? Math.Max(child.EdgeThickness, 2.2) : child.EdgeThickness, lineCap: PenLineCap.Flat, lineJoin: PenLineJoin.Round);

                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(start, false);
                    g.LineTo(new Point(midX, start.Y));
                    g.LineTo(new Point(midX, end.Y));
                    g.LineTo(new Point(end.X + 6, end.Y));
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, pen, geometry);

                // arrow head on the parent's side
                var arrow = new StreamGeometry();
                using (var g = arrow.Open())
                {
                    g.BeginFigure(end, true);
                    g.LineTo(new Point(end.X + 8, end.Y - 4.5));
                    g.LineTo(new Point(end.X + 8, end.Y + 4.5));
                    g.EndFigure(true);
                }
                context.DrawGeometry(brush, null, arrow);

                if (child.EdgeLabel is { } label)
                {
                    // Centred over the run next to the child, on a chip of the canvas colour: readable over any line behind it.
                    var formatted = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 12, pass ? lit : text);
                    var x = Math.Max(midX + 2, start.X - formatted.Width - 8);
                    var y = start.Y - formatted.Height - child.EdgeThickness / 2 - 3;
                    context.DrawRectangle(chip, null, new Rect(x - 3, y - 1, formatted.Width + 6, formatted.Height + 2), 4, 4);
                    context.DrawText(formatted, new Point(x, y));
                }
            }
    }
}
