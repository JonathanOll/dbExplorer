namespace DbExplorer.Core.Model;

public enum ThemeChoice { System, Light, Dark }

public enum AccentPreset { Blue, Teal, Green, Orange, Pink, Purple }

public enum GridDensity { Comfortable, Compact, Spacious }

public enum NullDisplay { Null, Parenthesized, Empty }

public enum DateDisplay { Iso, Locale }

/// <summary>Look of the application: theme, scale, accent colour, density of the grids and the tree.</summary>
public sealed record AppearanceSettings
{
    public const int MinScalePercent = 90, MaxScalePercent = 150;

    public ThemeChoice Theme { get; init; } = ThemeChoice.System;
    public int UiScalePercent { get; init; } = 100;
    public AccentPreset Accent { get; init; } = AccentPreset.Blue;
    public GridDensity Density { get; init; } = GridDensity.Comfortable;

    public AppearanceSettings Normalized() => this with
    {
        Theme = Enum.IsDefined(Theme) ? Theme : ThemeChoice.System,
        UiScalePercent = Math.Clamp(UiScalePercent, MinScalePercent, MaxScalePercent),
        Accent = Enum.IsDefined(Accent) ? Accent : AccentPreset.Blue,
        Density = Enum.IsDefined(Density) ? Density : GridDensity.Comfortable,
    };

    /// <summary>Row pitch in DIP of the data grid and the navigator tree.</summary>
    public double RowHeight => Density switch { GridDensity.Compact => 24, GridDensity.Spacious => 32, _ => 28 };
}

/// <summary>The SQL editor of script tabs. An empty <see cref="FontFamily"/> means the theme's mono font stack.</summary>
public sealed record EditorSettings
{
    public const double MinFontSize = 10, MaxFontSize = 24;

    public string FontFamily { get; init; } = "";
    public double FontSize { get; init; } = 13;
    public bool LineNumbers { get; init; } = true;
    public bool WordWrap { get; init; }
    public bool ShowWhitespace { get; init; }
    public bool HighlightCurrentLine { get; init; } = true;
    public int TabSize { get; init; } = 4;

    public EditorSettings Normalized() => this with
    {
        FontFamily = (FontFamily ?? "").Trim(),
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, MinFontSize, MaxFontSize) : 13,
        TabSize = TabSize is 2 or 4 or 8 ? TabSize : 4,
    };
}

/// <summary>Result grids and the display of values.</summary>
public sealed record ResultsSettings
{
    public const double MinGridFontSize = 9, MaxGridFontSize = 20;
    public const int MinRowLimit = 1, MaxRowLimit = 100_000, MinCellChars = 20, MaxCellChars = 2000;

    public double GridFontSize { get; init; } = 12.5;
    public bool Zebra { get; init; } = true;
    public int DefaultRowLimit { get; init; } = 1000;
    public NullDisplay NullText { get; init; } = NullDisplay.Null;
    public bool ThousandsSeparator { get; init; }
    public DateDisplay DateFormat { get; init; } = DateDisplay.Iso;
    public int MaxCellLength { get; init; } = 200;

    public ResultsSettings Normalized() => this with
    {
        GridFontSize = double.IsFinite(GridFontSize) ? Math.Clamp(GridFontSize, MinGridFontSize, MaxGridFontSize) : 12.5,
        DefaultRowLimit = Math.Clamp(DefaultRowLimit, MinRowLimit, MaxRowLimit),
        NullText = Enum.IsDefined(NullText) ? NullText : NullDisplay.Null,
        DateFormat = Enum.IsDefined(DateFormat) ? DateFormat : DateDisplay.Iso,
        MaxCellLength = Math.Clamp(MaxCellLength, MinCellChars, MaxCellChars),
    };

    /// <summary>The text shown for a NULL cell.</summary>
    public string NullLabel => NullText switch { NullDisplay.Parenthesized => "(null)", NullDisplay.Empty => "", _ => "NULL" };
}

/// <summary>
/// User settings of the Settings dialog. One section per page: another feature adds its own section property here (with a
/// default) and copies it in <see cref="Normalized"/>. Missing sections of an old config file load as the defaults.
/// </summary>
public sealed record AppSettings
{
    public AppearanceSettings Appearance { get; init; } = new();
    public EditorSettings Editor { get; init; } = new();
    public ResultsSettings Results { get; init; } = new();

    /// <summary>A copy that is safe to use: null sections (hand-edited file) become defaults and numbers are clamped.</summary>
    public AppSettings Normalized() => this with
    {
        Appearance = (Appearance ?? new()).Normalized(),
        Editor = (Editor ?? new()).Normalized(),
        Results = (Results ?? new()).Normalized(),
    };
}
