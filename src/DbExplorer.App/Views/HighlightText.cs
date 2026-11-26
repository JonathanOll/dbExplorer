using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace DbExplorer.App.Views;

/// <summary>
/// A text block that draws the characters matched by a search query in bold (a quick-open row). The query is an inherited
/// attached property set once on the list, so every row follows it without a binding per row. A query that is not a subsequence
/// of the text (typo-tolerant matches) leaves the text plain.
/// </summary>
public sealed class HighlightText : TextBlock
{
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<HighlightText, string?>(nameof(Source));

    public static readonly AttachedProperty<string?> QueryProperty =
        AvaloniaProperty.RegisterAttached<HighlightText, Control, string?>("Query", null, inherits: true);

    public static readonly StyledProperty<IReadOnlyList<int>?> PositionsProperty =
        AvaloniaProperty.Register<HighlightText, IReadOnlyList<int>?>(nameof(Positions));

    static HighlightText()
    {
        PositionsProperty.Changed.AddClassHandler<HighlightText>((t, _) => t.Rebuild());
        SourceProperty.Changed.AddClassHandler<HighlightText>((t, _) => t.Rebuild());
        QueryProperty.Changed.AddClassHandler<HighlightText>((t, _) => t.Rebuild());
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>The characters to mark, when the caller already knows them (command palette); null: matched against the query.</summary>
    public IReadOnlyList<int>? Positions
    {
        get => GetValue(PositionsProperty);
        set => SetValue(PositionsProperty, value);
    }

    public static string? GetQuery(Control control) => control.GetValue(QueryProperty);

    public static void SetQuery(Control control, string? value) => control.SetValue(QueryProperty, value);

    /// <summary>Indexes of <paramref name="text"/> that match the query as a case-insensitive subsequence, greedily from the left; empty when there is none.</summary>
    public static IReadOnlyList<int> Match(string text, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var found = new List<int>();
        var at = 0;
        foreach (var c in query)
        {
            if (char.IsWhiteSpace(c)) continue;
            var i = text.IndexOf(c.ToString(), at, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return [];
            found.Add(i);
            at = i + 1;
        }
        return found;
    }

    private void Rebuild()
    {
        var text = Source ?? "";
        Inlines?.Clear();
        var hits = Positions ?? Match(text, GetQuery(this));
        if (hits.Count == 0)
        {
            Text = text;
            return;
        }

        Text = null;
        Inlines ??= new InlineCollection();
        var marked = new HashSet<int>(hits);
        var start = 0;
        while (start < text.Length)
        {
            var bold = marked.Contains(start);
            var end = start;
            while (end < text.Length && marked.Contains(end) == bold) end++;
            Inlines.Add(new Run(text[start..end]) { FontWeight = bold ? FontWeight.Bold : FontWeight.Normal });
            start = end;
        }
    }
}
