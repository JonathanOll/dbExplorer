using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>Result grids: font, stripes, the row limit of new scripts and how values are written. Display only: filters, sorting and copy keep the plain values.</summary>
public sealed class ResultsSettingsPage : SettingsPageViewModel
{
    public ResultsSettingsPage(SettingsContext ctx)
        : base("results", "Results", "IconTable", "How result grids look and how values are shown.")
    {
        var d = new ResultsSettings();
        AddGroup("Grid",
            Make.Number(ctx, "Grid font size", null,
                s => s.Results.GridFontSize, (s, v) => s with { Results = s.Results with { GridFontSize = v } },
                ResultsSettings.MinGridFontSize, ResultsSettings.MaxGridFontSize, 0.5, d.GridFontSize, "px"),
            Make.Toggle(ctx, "Alternate row shading", "Every second row gets a faint background.",
                s => s.Results.Zebra, (s, v) => s with { Results = s.Results with { Zebra = v } }, d.Zebra, "zebra stripes"),
            Make.Number(ctx, "Default row limit of new scripts", "Open scripts keep their own limit.",
                s => s.Results.DefaultRowLimit, (s, v) => s with { Results = s.Results with { DefaultRowLimit = (int)Math.Round(v) } },
                ResultsSettings.MinRowLimit, ResultsSettings.MaxRowLimit, 100, d.DefaultRowLimit, "rows", keywords: "max top"));
        AddGroup("Values",
            Make.Choice(ctx, "NULL display", "Shown in italic for a NULL cell. Applies to results loaded from now on.",
                [new("NULL", NullDisplay.Null), new("(null)", NullDisplay.Parenthesized), new("Empty", NullDisplay.Empty)],
                s => s.Results.NullText, (s, v) => s with { Results = s.Results with { NullText = v } }, d.NullText),
            Make.Toggle(ctx, "Thousands separator on numbers", "1234567 is shown as 1,234,567.",
                s => s.Results.ThousandsSeparator, (s, v) => s with { Results = s.Results with { ThousandsSeparator = v } }, d.ThousandsSeparator, "number format digits"),
            Make.Choice(ctx, "Date format", "ISO is 2026-03-14 09:30:00. Locale follows the Windows regional settings.",
                [new("ISO", DateDisplay.Iso), new("Locale", DateDisplay.Locale)],
                s => s.Results.DateFormat, (s, v) => s with { Results = s.Results with { DateFormat = v } }, d.DateFormat, keywords: "time"),
            Make.Number(ctx, "Longest cell text", "Longer text is cut with an ellipsis. The tooltip has the full value.",
                s => s.Results.MaxCellLength, (s, v) => s with { Results = s.Results with { MaxCellLength = (int)Math.Round(v) } },
                ResultsSettings.MinCellChars, ResultsSettings.MaxCellChars, 10, d.MaxCellLength, "characters", keywords: "truncate"));
    }
}
