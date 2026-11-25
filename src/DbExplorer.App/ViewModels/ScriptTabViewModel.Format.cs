using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// One text change the editor applies as a single undoable edit: replace <see cref="Length"/> characters at <see cref="Start"/>
/// by <see cref="Text"/>, then put the caret at <see cref="Caret"/> (and select <see cref="SelectionLength"/> characters from
/// <see cref="SelectionStart"/> when the user had a selection).
/// </summary>
public sealed record ScriptTextEdit(int Start, int Length, string Text, int Caret, int SelectionStart, int SelectionLength);

public sealed partial class ScriptTabViewModel
{
    /// <summary>Raised by <see cref="FormatSql"/> when a view is attached: it applies the edit to the editor (one undo step).</summary>
    public event Action<ScriptTextEdit>? FormatRequested;

    /// <summary>The style the tab formats with: the one of the Options dialog.</summary>
    private SqlFormatOptions FormatStyle => _hooks.FormatOptions?.Invoke() ?? SqlFormatOptions.Default;

    /// <summary>
    /// The edit that formats the selection, or the whole script when nothing is selected; null when the text would not change.
    /// Pure: nothing is applied.
    /// </summary>
    public ScriptTextEdit? PrepareFormat()
    {
        var text = Text;
        var start = 0;
        var end = text.Length;
        var selected = false;
        if (Selection is { } sel)
        {
            var s = Math.Clamp(Math.Min(sel.Start, sel.End), 0, text.Length);
            var e = Math.Clamp(Math.Max(sel.Start, sel.End), 0, text.Length);
            if (e > s) { start = s; end = e; selected = true; }
        }

        var original = text[start..end];
        var formatted = SqlFormatter.Format(original, Connection.Kind, FormatStyle);
        if (formatted == original) return null;

        // Replace only what changed, so the scroll position and the undo entry stay small.
        var prefix = 0;
        var max = Math.Min(original.Length, formatted.Length);
        while (prefix < max && original[prefix] == formatted[prefix]) prefix++;
        var suffix = 0;
        while (suffix < max - prefix && original[^(suffix + 1)] == formatted[^(suffix + 1)]) suffix++;

        var newText = string.Concat(text.AsSpan(0, start), formatted, text.AsSpan(end));
        var caret = SqlFormatter.MapOffset(text, newText, CaretOffset);
        return new ScriptTextEdit(
            start + prefix, original.Length - prefix - suffix, formatted.Substring(prefix, formatted.Length - prefix - suffix), caret,
            selected ? start : caret, selected ? formatted.Length : 0);
    }

    /// <summary>Ctrl+Shift+F: formats the selection, or the whole script when nothing is selected. One undoable edit.</summary>
    [RelayCommand]
    public void FormatSql()
    {
        var edit = PrepareFormat();
        if (edit is null)
        {
            _status.Info(string.IsNullOrWhiteSpace(Text) ? "Nothing to format." : "Already formatted.");
            return;
        }
        if (FormatRequested is { } apply)
        {
            apply(edit);
            return;
        }

        // No editor attached (headless): change the text directly.
        Text = string.Concat(Text.AsSpan(0, edit.Start), edit.Text, Text.AsSpan(edit.Start + edit.Length));
        CaretOffset = edit.Caret;
    }
}
