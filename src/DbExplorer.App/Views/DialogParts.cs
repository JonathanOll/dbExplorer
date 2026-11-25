using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace DbExplorer.App.Views;

/// <summary>
/// The header band shared by the form dialogs: an accent-soft icon tile, a title and a one-line subtitle, on the chrome tone with
/// a hairline below. Colours are theme tokens (docs/ui/README.md), bound so they follow a live light/dark switch.
/// </summary>
public sealed class DialogHeader : Border
{
    public static readonly StyledProperty<Geometry?> IconProperty = AvaloniaProperty.Register<DialogHeader, Geometry?>(nameof(Icon));
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<DialogHeader, string?>(nameof(Title));
    public static readonly StyledProperty<string?> SubtitleProperty = AvaloniaProperty.Register<DialogHeader, string?>(nameof(Subtitle));

    private readonly PathIcon _icon = new() { Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _subtitle = new() { TextWrapping = TextWrapping.Wrap };

    public DialogHeader()
    {
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(24, 16);
        Bind(BackgroundProperty, this.GetResourceObservable("DbxPanelHeaderBg"));
        Bind(BorderBrushProperty, this.GetResourceObservable("DbxBorderSubtle"));

        var tile = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0), Child = _icon,
        };
        tile.Bind(BackgroundProperty, tile.GetResourceObservable("DbxAccentSoft"));
        _icon.Bind(TemplatedControl.ForegroundProperty, _icon.GetResourceObservable("DbxAccent"));
        _subtitle.Classes.Add("dim");
        _subtitle.Classes.Add("small");

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = { _title, _subtitle } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(tile);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Child = grid;
        _subtitle.IsVisible = false;
    }

    protected override Type StyleKeyOverride => typeof(Border);

    public Geometry? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty) _icon.Data = Icon;
        else if (change.Property == TitleProperty) _title.Text = Title;
        else if (change.Property == SubtitleProperty)
        {
            _subtitle.Text = Subtitle;
            _subtitle.IsVisible = !string.IsNullOrEmpty(Subtitle);
        }
    }
}

/// <summary>The footer band shared by the form dialogs: chrome tone, hairline above; its content is the buttons (and a hint).</summary>
public sealed class DialogFooter : Border
{
    public DialogFooter()
    {
        BorderThickness = new Thickness(0, 1, 0, 0);
        Padding = new Thickness(24, 14);
        Bind(BackgroundProperty, this.GetResourceObservable("DbxWindowBg"));
        Bind(BorderBrushProperty, this.GetResourceObservable("DbxBorderSubtle"));
    }

    protected override Type StyleKeyOverride => typeof(Border);
}
