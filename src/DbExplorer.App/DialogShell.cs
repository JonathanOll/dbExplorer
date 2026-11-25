using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App;

/// <summary>
/// The look shared by the small code-built dialogs (prompt, confirmation, save / discard): an icon and a body on the content
/// tone, a footer band with the buttons. Colours are theme tokens (see docs/ui/README.md), bound so they follow the theme.
/// </summary>
internal static class DialogShell
{
    public static Window Create(string title, string iconKey, Control body, IReadOnlyList<Button> buttons, double minWidth = 380, double maxWidth = 520,
        DialogTone tone = DialogTone.Info)
    {
        var footerButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in buttons) footerButtons.Children.Add(button);
        var footer = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 12),
            Child = footerButtons,
        };
        footer.Bind(Border.BackgroundProperty, footer.GetResourceObservable("DbxWindowBg"));
        footer.Bind(Border.BorderBrushProperty, footer.GetResourceObservable("DbxBorder"));

        var icon = new PathIcon { Width = 26, Height = 26, Data = Icons.Find(iconKey), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 14, 0) };
        icon.Bind(TemplatedControl.ForegroundProperty, icon.GetResourceObservable(tone switch
        {
            DialogTone.Danger => "DbxError",
            DialogTone.Warning => "DbxWarn",
            _ => "DbxAccent",
        }));
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        content.Children.Add(icon);
        Grid.SetColumn(body, 1);
        content.Children.Add(body);
        var bodyPanel = new Border { Padding = new Thickness(22, 20, 22, 22), Child = content };
        bodyPanel.Bind(Border.BackgroundProperty, bodyPanel.GetResourceObservable("DbxContentBg"));

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(bodyPanel);
        return new Window
        {
            Title = title,
            Content = root,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = minWidth,
            MaxWidth = maxWidth,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
    }

    public static Button Button(string text, bool isDefault = false, bool isCancel = false, bool primary = false, bool danger = false)
    {
        var button = new Button
        {
            Content = text, IsDefault = isDefault, IsCancel = isCancel, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        if (danger) button.Classes.Add("danger");
        else if (primary) button.Classes.Add("accent");
        return button;
    }

    /// <summary>The message of a dialog: selectable (an error text can be copied), with an optional dim detail line under it.</summary>
    public static Control Message(string text, string? detail = null)
    {
        var message = new SelectableTextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = detail is null ? FontWeight.Normal : FontWeight.SemiBold,
        };
        if (detail is null) return message;
        var info = new SelectableTextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(0, 6, 0, 0) };
        info.Classes.Add("dim");
        return new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { message, info } };
    }
}
