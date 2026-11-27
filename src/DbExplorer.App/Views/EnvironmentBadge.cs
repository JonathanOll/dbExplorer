using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Views;

/// <summary>
/// The environment of a connection as a pill: always a word, tinted by the theme's status colours (production red). Hidden
/// for <see cref="DbEnvironment.None"/>.
/// </summary>
public sealed class EnvironmentBadge : Border
{
    public static readonly StyledProperty<DbEnvironment> EnvironmentProperty =
        AvaloniaProperty.Register<EnvironmentBadge, DbEnvironment>(nameof(Environment));

    private readonly TextBlock _text = new() { FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };

    public EnvironmentBadge()
    {
        Classes.Add("badge");
        VerticalAlignment = VerticalAlignment.Center;
        Child = _text;
        IsVisible = false;
    }

    protected override Type StyleKeyOverride => typeof(Border);

    public DbEnvironment Environment { get => GetValue(EnvironmentProperty); set => SetValue(EnvironmentProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EnvironmentProperty) return;
        var value = Environment;
        IsVisible = value != DbEnvironment.None;
        _text.Text = value.ShortLabel();
        Classes.Set("success", value == DbEnvironment.Development);
        Classes.Set("info", value == DbEnvironment.Test);
        Classes.Set("warn", value == DbEnvironment.PreProduction);
        Classes.Set("error", value == DbEnvironment.Production);
        ToolTip.SetTip(this, $"Environment: {value.Label()}");
        AutomationProperties.SetName(this, $"Environment: {value.Label()}");
    }
}
