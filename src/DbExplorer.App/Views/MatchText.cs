using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace DbExplorer.App.Views;

/// <summary>A text block that draws one range of its text in bold (the hit in a search result line).</summary>
public sealed class MatchText : TextBlock
{
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<MatchText, string?>(nameof(Source));
    public static readonly StyledProperty<int> MatchStartProperty = AvaloniaProperty.Register<MatchText, int>(nameof(MatchStart));
    public static readonly StyledProperty<int> MatchLengthProperty = AvaloniaProperty.Register<MatchText, int>(nameof(MatchLength));

    static MatchText()
    {
        SourceProperty.Changed.AddClassHandler<MatchText>((t, _) => t.Rebuild());
        MatchStartProperty.Changed.AddClassHandler<MatchText>((t, _) => t.Rebuild());
        MatchLengthProperty.Changed.AddClassHandler<MatchText>((t, _) => t.Rebuild());
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public string? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public int MatchStart { get => GetValue(MatchStartProperty); set => SetValue(MatchStartProperty, value); }
    public int MatchLength { get => GetValue(MatchLengthProperty); set => SetValue(MatchLengthProperty, value); }

    private void Rebuild()
    {
        var text = Source ?? "";
        Inlines?.Clear();
        var start = Math.Clamp(MatchStart, 0, text.Length);
        var end = Math.Clamp(start + Math.Max(0, MatchLength), start, text.Length);
        if (end == start)
        {
            Text = text;
            return;
        }

        Text = null;
        Inlines ??= new InlineCollection();
        if (start > 0) Inlines.Add(new Run(text[..start]));
        Inlines.Add(new Run(text[start..end]) { FontWeight = FontWeight.Bold });
        if (end < text.Length) Inlines.Add(new Run(text[end..]));
    }
}
