using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace DbExplorer.App.Views;

/// <summary>
/// What the last run did to the text of the editor: a thin bar in the gutter next to each statement that ran (green when it
/// worked, red when it failed) and a soft band over the statement whose result tab is selected. The marks point into the text
/// of the run, so the view clears them as soon as the text is edited. Register the renderer on the text view and add
/// <see cref="Margin"/> to the text area's left margins.
/// </summary>
public sealed class StatementMarkRenderer : IBackgroundRenderer
{
    public readonly record struct Mark(int Start, int End, bool IsError);

    private IReadOnlyList<Mark> _marks = [];
    private (int Start, int End)? _selected;

    public StatementMarkRenderer() => Margin = new MarkMargin(this);

    /// <summary>The gutter strip that shows the bars; the view adds it to the left margins of the text area.</summary>
    public AbstractMargin Margin { get; }

    public IBrush? OkBrush { get; set; }

    public IBrush? ErrorBrush { get; set; }

    public IBrush? BandBrush { get; set; }

    public KnownLayer Layer => KnownLayer.Background;

    public bool HasMarks => _marks.Count > 0;

    public void Set(IReadOnlyList<Mark> marks, (int Start, int End)? selected)
    {
        _marks = marks;
        _selected = selected;
        Margin.InvalidateVisual();
    }

    public void Clear() => Set([], null);

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_selected is not { } sel || BandBrush is not { } brush || textView.Document is not { } document || !textView.VisualLinesValid) return;
        if (Band(textView, document, sel.Start, sel.End) is not { } band) return;
        using (drawingContext.PushOpacity(0.55))
            drawingContext.DrawRectangle(brush, null, new Rect(0, band.Top, textView.Bounds.Width, band.Height));
    }

    // The vertical extent of the visible lines of a statement (relative to the top of the text view), or null when none is on screen.
    private static Rect? Band(TextView textView, TextDocument document, int start, int end)
    {
        start = Math.Clamp(start, 0, document.TextLength);
        end = Math.Clamp(end, start, document.TextLength);
        if (end <= start) return null;
        var first = document.GetLineByOffset(start).LineNumber;
        var last = document.GetLineByOffset(Math.Max(start, end - 1)).LineNumber;
        double top = double.MaxValue, bottom = double.MinValue;
        for (var number = first; number <= last; number++)
        {
            if (textView.GetVisualLine(number) is not { } line) continue;
            top = Math.Min(top, line.VisualTop - textView.VerticalOffset);
            bottom = Math.Max(bottom, line.VisualTop + line.Height - textView.VerticalOffset);
        }
        return bottom > top ? new Rect(0, top, 0, bottom - top) : null;
    }

    private sealed class MarkMargin(StatementMarkRenderer owner) : AbstractMargin
    {
        private const double GutterWidth = 10;
        private const double BarLeft = 3;
        private const double BarWidth = 3;

        protected override Size MeasureOverride(Size availableSize) => new(GutterWidth, 0);

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null) oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null) newTextView.VisualLinesChanged += OnVisualLinesChanged;
        }

        private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

        public override void Render(DrawingContext context)
        {
            if (owner._marks.Count == 0 || TextView is not { } textView || textView.Document is not { } document || !textView.VisualLinesValid) return;
            foreach (var mark in owner._marks)
            {
                if (Band(textView, document, mark.Start, mark.End) is not { } r) continue;
                var brush = mark.IsError ? owner.ErrorBrush : owner.OkBrush;
                if (brush is not null) context.DrawRectangle(brush, null, new Rect(BarLeft, r.Top, BarWidth, r.Height), 1.5, 1.5);
            }
        }
    }
}
