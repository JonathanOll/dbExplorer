using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>The SQL editor of script tabs. Open editors follow every change at once.</summary>
public sealed class EditorSettingsPage : SettingsPageViewModel
{
    public EditorSettingsPage(SettingsContext ctx)
        : base("editor", "Editor", "IconScript", "Font and behaviour of the SQL editor.")
    {
        var d = new EditorSettings();
        var fonts = ctx.MonospaceFonts ?? Appearance.MonospaceFonts.Installed();
        var options = new List<ChoiceOption> { new("Default (Consolas)", "") };
        options.AddRange(fonts.Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => new ChoiceOption(f, f)));
        AddGroup("Font",
            Make.Choice(ctx, "Font family", "Monospace fonts installed on this computer.", options,
                s => s.Editor.FontFamily, (s, v) => s with { Editor = s.Editor with { FontFamily = v } }, d.FontFamily, chips: false, keywords: "typeface mono"),
            Make.Number(ctx, "Font size", null,
                s => s.Editor.FontSize, (s, v) => s with { Editor = s.Editor with { FontSize = v } },
                EditorSettings.MinFontSize, EditorSettings.MaxFontSize, 1, d.FontSize, "px"));
        AddGroup("Display",
            Make.Toggle(ctx, "Line numbers", null, s => s.Editor.LineNumbers, (s, v) => s with { Editor = s.Editor with { LineNumbers = v } }, d.LineNumbers, "gutter"),
            Make.Toggle(ctx, "Word wrap", "Long lines continue on the next row instead of scrolling sideways.",
                s => s.Editor.WordWrap, (s, v) => s with { Editor = s.Editor with { WordWrap = v } }, d.WordWrap, "wrap"),
            Make.Toggle(ctx, "Show whitespace", "Dots for spaces and arrows for tabs.",
                s => s.Editor.ShowWhitespace, (s, v) => s with { Editor = s.Editor with { ShowWhitespace = v } }, d.ShowWhitespace, "spaces tabs invisible"),
            Make.Toggle(ctx, "Highlight the current line", null,
                s => s.Editor.HighlightCurrentLine, (s, v) => s with { Editor = s.Editor with { HighlightCurrentLine = v } }, d.HighlightCurrentLine, "caret"));
        AddGroup("Indentation",
            Make.Choice(ctx, "Tab size", "Width of a tab character, in spaces.",
                [new("2", 2), new("4", 4), new("8", 8)],
                s => s.Editor.TabSize, (s, v) => s with { Editor = s.Editor with { TabSize = v } }, d.TabSize, keywords: "indent"));
    }
}
