namespace DbExplorer.Core.Scripting;

public enum KeywordCase
{
    Upper,
    Lower,

    /// <summary>Keywords keep the case they were typed in.</summary>
    Keep,
}

public enum CommaPosition
{
    /// <summary>"a," at the end of the line.</summary>
    Trailing,

    /// <summary>", b" at the start of the line.</summary>
    Leading,
}

/// <summary>Style of <see cref="SqlFormatter"/>. Persisted in the app config (<c>AppConfig.Format</c>); every property has a default so old files load.</summary>
public sealed record SqlFormatOptions
{
    public KeywordCase KeywordCase { get; set; } = KeywordCase.Upper;

    /// <summary>Spaces per indent level (1 to 8). Also measures a tab when <see cref="UseTabs"/> is on.</summary>
    public int IndentSize { get; set; } = 4;

    public bool UseTabs { get; set; }

    public CommaPosition CommaPosition { get; set; } = CommaPosition.Trailing;

    /// <summary>Lists (IN (...), boolean groups, argument lists) wrap past this width.</summary>
    public int MaxLineWidth { get; set; } = 100;

    public static SqlFormatOptions Default { get; } = new();

    /// <summary>The same options with out-of-range values clamped (a hand-edited config must not break the formatter).</summary>
    public SqlFormatOptions Normalized() => this with
    {
        IndentSize = Math.Clamp(IndentSize, 1, 8),
        MaxLineWidth = Math.Clamp(MaxLineWidth, 40, 400),
        KeywordCase = Enum.IsDefined(KeywordCase) ? KeywordCase : KeywordCase.Upper,
        CommaPosition = Enum.IsDefined(CommaPosition) ? CommaPosition : CommaPosition.Trailing,
    };
}
