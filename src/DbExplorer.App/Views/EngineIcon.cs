using Avalonia;
using Avalonia.Controls;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Views;

/// <summary>
/// The database icon of a connection. Neutral on purpose: it is tinted with the theme's icon brush (<c>DbxIcon</c>) whatever the
/// engine, because the colour identity of a connection is its colour chip, not its engine. <see cref="Kind"/> is kept so views
/// and tooltips can still bind it. The brush is bound to the theme resource, so it follows a light/dark switch while the app runs.
/// </summary>
public sealed class EngineIcon : PathIcon
{
    public static readonly StyledProperty<DbKind?> KindProperty = AvaloniaProperty.Register<EngineIcon, DbKind?>(nameof(Kind));

    protected override Type StyleKeyOverride => typeof(PathIcon);

    public EngineIcon()
    {
        Bind(DataProperty, this.GetResourceObservable("IconDatabase"));
        Bind(ForegroundProperty, this.GetResourceObservable("DbxIcon"));
    }

    public DbKind? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }
}
