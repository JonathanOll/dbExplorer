using Avalonia.Data.Converters;
using Avalonia.Media;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>Converters of the status bar and its history: severity to icon.</summary>
public static class StatusIcons
{
    public static readonly IValueConverter Severity = new FuncValueConverter<StatusSeverity, Geometry?>(s => Icons.Find(s switch
    {
        StatusSeverity.Error => "IconError",
        StatusSeverity.Warn => "IconWarning",
        StatusSeverity.Success => "IconCheck",
        _ => "IconInfo",
    }));
}
