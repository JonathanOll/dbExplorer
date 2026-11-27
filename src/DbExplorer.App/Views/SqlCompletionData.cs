using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.Views;

/// <summary>One line of the completion list: a monochrome icon by kind, the label, a dim detail on the right.</summary>
internal sealed class SqlCompletionData(CompletionItem item) : ICompletionData
{
    // Two shapes Icons.axaml has no equivalent of (Material "code" and "functions"), parsed on first use.
    private static readonly Lazy<Geometry> KeywordIcon = new(() => StreamGeometry.Parse(
        "M9.4 16.6L4.8 12l4.6-4.6L8 6l-6 6 6 6 1.4-1.4zm5.2 0l4.6-4.6-4.6-4.6L16 6l6 6-6 6-1.4-1.4z"));

    private static readonly Lazy<Geometry> FunctionIcon = new(() => StreamGeometry.Parse(
        "M18 4H6v2l6.5 6L6 18v2h12v-3h-7l5-5-5-5h7z"));

    private Control? _content;

    public CompletionItem Item => item;

    public Avalonia.Media.IImage? Image => null;

    /// <summary>What the typed prefix is filtered against (the list narrows on it).</summary>
    public string Text => item.Filter;

    public object Content => _content ??= BuildContent();

    public object? Description => null;

    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        // The list asks for an insertion when a non-identifier character is typed (space, comma, parenthesis...): that
        // only closes the list. The character is typed as usual and nothing is accepted. Enter and Tab accept.
        if (insertionRequestEventArgs is TextInputEventArgs) return;

        var document = textArea.Document;
        var start = completionSegment.Offset;
        // The window filters on what follows the opening quote the user typed; that quote is replaced too. Only then does the item
        // start before the segment: a quote that closes the previous name stays.
        if (item.ReplaceStart == start - 1 && document.GetCharAt(start - 1) is '"' or '[' or '`') start--;
        var end = Math.Max(start, completionSegment.EndOffset);
        // The quote or bracket already closed after the caret (added by the editor) is replaced too: the item brings its own.
        if (start == item.ReplaceStart && start < end && end < document.TextLength
            && document.GetCharAt(start) is var opener and ('"' or '[' or '`') && document.GetCharAt(end) == (opener == '[' ? ']' : opener))
            end++;
        document.Replace(start, end - start, item.Text);
        textArea.Caret.Offset = start + item.Text.Length;
    }

    private Control BuildContent()
    {
        var icon = new PathIcon { Data = item.Kind switch
        {
            CompletionKind.Table => IconGeometry("IconTable"),
            CompletionKind.View => IconGeometry("IconView"),
            CompletionKind.Column => IconGeometry("IconColumn"),
            CompletionKind.Schema => IconGeometry("IconSchema"),
            CompletionKind.Alias => IconGeometry("IconLink"),
            CompletionKind.Function => FunctionIcon.Value,
            _ => KeywordIcon.Value,
        } };
        icon.Classes.Add("cmpIcon");

        var label = new TextBlock { Text = item.Label };
        label.Classes.Add("cmpLabel");
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };
        grid.Classes.Add("cmpRow");
        grid.Children.Add(icon);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (!string.IsNullOrEmpty(item.Detail))
        {
            var detail = new TextBlock { Text = item.Detail };
            detail.Classes.Add("cmpDetail");
            Grid.SetColumn(detail, 2);
            grid.Children.Add(detail);
        }
        return grid;
    }

    private static Geometry? IconGeometry(string key) =>
        Avalonia.Application.Current?.TryGetResource(key, null, out var value) == true ? value as Geometry : null;
}
