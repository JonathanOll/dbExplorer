using Avalonia;
using Avalonia.Controls;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Views;

/// <summary>
/// The database icon of a connection, tinted per engine (PostgreSQL blue, SQL Server red, SQLite teal). The tint is bound to
/// the theme brush, so it follows a light/dark switch while the app runs.
/// </summary>
public sealed class EngineIcon : PathIcon
{
    public static readonly StyledProperty<DbKind?> KindProperty = AvaloniaProperty.Register<EngineIcon, DbKind?>(nameof(Kind));

    protected override Type StyleKeyOverride => typeof(PathIcon);

    public DbKind? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != KindProperty) return;
        Bind(DataProperty, this.GetResourceObservable("IconDatabase"));
        Bind(ForegroundProperty, this.GetResourceObservable(Kind switch
        {
            DbKind.Postgres => "DbxEnginePostgres",
            DbKind.SqlServer => "DbxEngineSqlServer",
            _ => "DbxEngineSqlite",
        }));
    }
}
