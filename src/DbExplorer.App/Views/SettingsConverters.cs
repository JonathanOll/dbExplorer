using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DbExplorer.App.Views;

/// <summary>Converters of the Settings dialog.</summary>
public static class SettingsConverters
{
    /// <summary>
    /// "#RRGGBB" to a brush: only for the accent swatches, whose colours are the data of <c>AccentPalette</c> (not theme colours).
    /// </summary>
    public static readonly IValueConverter HexToBrush = new FuncValueConverter<string?, IBrush?>(
        hex => hex is not null && Color.TryParse(hex, out var color) ? new SolidColorBrush(color) : null);
}
