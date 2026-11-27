using Avalonia.Input;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.Views;

/// <summary>
/// Brackets and quotes in the script editor: an opener surrounds the selection, or gets its closer; typing that closer steps
/// over it and Backspace in the still empty pair removes both. Driven by the typed text, never by key codes, so every
/// keyboard layout (AltGr characters included) works. Each change is one undo step. The rules are in <see cref="AutoPairs"/>.
/// </summary>
internal sealed class EditorPairing(TextArea area, Func<DbKind?> engine, Func<bool> autoClose)
{
    // The closers added here and still in the text: only those are stepped over or removed with their opener.
    private readonly List<TextAnchor> _closers = [];

    /// <summary>Handles one typed text; marks it handled when the editor must not insert it itself.</summary>
    public void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (e.Handled || e.Text is not { Length: 1 } text || engine() is not { } kind) return;
        var typed = text[0];
        if (!area.Selection.IsEmpty)
        {
            // A rectangular selection keeps the editor's own behaviour (the character replaces every line of it).
            if (area.Selection is not RectangleSelection && AutoPairs.CloserOf(typed) is { } around) e.Handled = Surround(typed, around);
            return;
        }
        if (!autoClose()) return;

        var document = area.Document;
        var caret = area.Caret.Offset;
        if (caret < document.TextLength && document.GetCharAt(caret) == typed && Find(caret) is { } skipped)
        {
            _closers.Remove(skipped);
            area.Caret.Offset = caret + 1;
        }
        else if (AutoPairs.CloserOf(typed) is { } closer && AutoPairs.ShouldClose(document.Text, caret, typed, kind))
        {
            document.Insert(caret, $"{typed}{closer}");
            area.Caret.Offset = caret + 1;
            var anchor = document.CreateAnchor(caret + 1);
            anchor.MovementType = AnchorMovementType.AfterInsertion;   // typing inside the pair pushes the closer along
            _closers.Add(anchor);
        }
        else return;

        area.Caret.BringCaretToView();
        e.Handled = true;
    }

    /// <summary>Backspace between an opener and the closer added for it removes both; false: the editor handles the key.</summary>
    public bool Backspace()
    {
        if (!autoClose() || !area.Selection.IsEmpty) return false;
        var document = area.Document;
        var caret = area.Caret.Offset;
        if (caret <= 0 || caret >= document.TextLength || Find(caret) is not { } anchor) return false;
        if (AutoPairs.CloserOf(document.GetCharAt(caret - 1)) != document.GetCharAt(caret)) return false;
        _closers.Remove(anchor);
        document.Remove(caret - 1, 2);
        area.Caret.BringCaretToView();
        return true;
    }

    private TextAnchor? Find(int offset)
    {
        _closers.RemoveAll(a => a.IsDeleted);
        return _closers.Find(a => a.Offset == offset);
    }

    // The text stays selected, in the same direction, so a second opener wraps it again.
    private bool Surround(char opener, char closer)
    {
        var segment = area.Selection.SurroundingSegment;
        if (segment is null || segment.Length == 0) return false;
        var (start, end) = (segment.Offset, segment.EndOffset);
        var backwards = area.Caret.Offset == start;
        var document = area.Document;
        using (document.RunUpdate())
        {
            document.Insert(end, closer.ToString());
            document.Insert(start, opener.ToString());
        }
        var (from, to) = backwards ? (end + 1, start + 1) : (start + 1, end + 1);
        area.Selection = Selection.Create(area, from, to);
        area.Caret.Offset = to;
        area.Caret.BringCaretToView();
        return true;
    }
}
