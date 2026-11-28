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

    public static readonly StyledProperty<bool> WholeWordsProperty =
        AvaloniaProperty.Register<HighlightText, bool>(nameof(WholeWords));

    public static readonly StyledProperty<string?> ElsewhereProperty =
        AvaloniaProperty.Register<HighlightText, string?>(nameof(Elsewhere));

    public static readonly StyledProperty<bool> ShowMarksProperty =
        AvaloniaProperty.Register<HighlightText, bool>(nameof(ShowMarks), true);

    static HighlightText()
    {
        ShowMarksProperty.Changed.AddClassHandler<HighlightText>((t, _) => t.Rebuild());
        WholeWordsProperty.Changed.AddClassHandler<HighlightText>((t, _) => t.Rebuild());
        ElsewhereProperty.Changed.AddClassHandler<HighlightText>((t, _) => t.Rebuild());
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

    /// <summary>Marks only words the text contains as typed (a context chip, where scattered letters would be noise).</summary>
    public bool WholeWords
    {
        get => GetValue(WholeWordsProperty);
        set => SetValue(WholeWordsProperty, value);
    }

    /// <summary>Another text of the row: a word read there is not marked letter by letter here.</summary>
    public string? Elsewhere
    {
        get => GetValue(ElsewhereProperty);
        set => SetValue(ElsewhereProperty, value);
    }

    /// <summary>False draws the text plain: a disabled row must not shout its matched letters.</summary>
    public bool ShowMarks
    {
        get => GetValue(ShowMarksProperty);
        set => SetValue(ShowMarksProperty, value);
    }

    public static string? GetQuery(Control control) => control.GetValue(QueryProperty);

    public static void SetQuery(Control control, string? value) => control.SetValue(QueryProperty, value);

    /// <summary>
    /// Indexes of <paramref name="text"/> to mark for the query. Each word (split on spaces and dots) is marked on its own, in
    /// any order: where the text contains it (at the start of a part first), else, unless <paramref name="wholeOnly"/> or the
    /// word is read in <paramref name="elsewhere"/>, as a case-insensitive subsequence from the left. A word that matches
    /// neither way marks nothing.
    /// </summary>
    public static IReadOnlyList<int> Match(string text, string? query, bool wholeOnly = false, string? elsewhere = null)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var found = new SortedSet<int>();
        foreach (var word in query.Split([' ', '\t', '.'], StringSplitOptions.RemoveEmptyEntries))
        {
            var at = Find(text, word);
            if (at >= 0)
            {
                for (var i = 0; i < word.Length; i++) found.Add(at + i);
                continue;
            }

            if (wholeOnly || elsewhere?.Contains(word, StringComparison.OrdinalIgnoreCase) == true) continue;
            var letters = new List<int>(word.Length);
            var from = 0;
            foreach (var c in word)
            {
                var i = text.IndexOf(c.ToString(), from, StringComparison.OrdinalIgnoreCase);
                if (i < 0) break;
                letters.Add(i);
                from = i + 1;
            }

            if (letters.Count == word.Length) found.UnionWith(letters);
        }

        return [.. found];
    }

    private static int Find(string text, string word)
    {
        var first = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        for (var at = first; at >= 0; at = text.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
            if (at == 0 || !char.IsLetterOrDigit(text[at - 1])) return at;
        return first;
    }

    private void Rebuild()
    {
        var text = Source ?? "";
        Inlines?.Clear();
        var hits = !ShowMarks ? [] : Positions ?? Match(text, GetQuery(this), WholeWords, Elsewhere);
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
