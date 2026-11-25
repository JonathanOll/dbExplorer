using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DbExplorer.App.Views;

/// <summary>
/// Shows one complete cell value (a long text, a JSON document, a binary as hex) in a window of its own, selectable and copyable,
/// because a grid cell shows only its first line. JSON is indented for reading; Copy always puts the original text on the clipboard.
/// Code-built like the other small dialogs; colours are theme tokens.
/// </summary>
public sealed class ValueViewerDialog : Window
{
    public ValueViewerDialog(string column, string value, bool isBinary)
    {
        string? pretty = null;
        var isJson = !isBinary && TryIndentJson(value, out pretty);
        var shown = isJson ? pretty! : value;
        Title = column;
        Width = 680;
        Height = 460;
        MinWidth = 380;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var kind = isBinary ? $"Binary, {Math.Max(0, (value.Length - 2) / 2):N0} bytes (hex)"
            : isJson ? "JSON, indented for reading"
            : $"Text, {value.Length:N0} characters";
        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = column, FontSize = 15, FontWeight = FontWeight.SemiBold });
        heading.Children.Add(new TextBlock { Text = kind, Classes = { "dim" } });
        var header = new Border { Padding = new Thickness(20, 12), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading };
        header.Bind(Border.BackgroundProperty, header.GetResourceObservable("DbxPanelHeaderBg"));
        header.Bind(Border.BorderBrushProperty, header.GetResourceObservable("DbxBorderSubtle"));

        var box = new TextBox
        {
            Text = shown, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Classes = { "bare" },
            Margin = new Thickness(12, 8), Padding = new Thickness(8),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        if (Application.Current?.TryFindResource("DbxMonoFont", out var mono) == true && mono is FontFamily family) box.FontFamily = family;
        Avalonia.Automation.AutomationProperties.SetName(box, $"Value of {column}");
        var body = new Border { Child = box };
        body.Bind(Border.BackgroundProperty, body.GetResourceObservable("DbxContentBg"));

        var copy = new Button { Content = "Copy", Classes = { "accent" }, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        copy.Click += async (_, _) =>
        {
            if (Clipboard is not { } clipboard) return;
            await clipboard.SetTextAsync(value);
            copy.Content = "Copied";
        };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { copy, close } };
        var footer = new Border { Padding = new Thickness(20, 12), BorderThickness = new Thickness(0, 1, 0, 0), Child = buttons };
        footer.Bind(Border.BackgroundProperty, footer.GetResourceObservable("DbxWindowBg"));
        footer.Bind(Border.BorderBrushProperty, footer.GetResourceObservable("DbxBorder"));

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;
        Opened += (_, _) => box.Focus();
    }

    private static bool TryIndentJson(string text, out string? pretty)
    {
        pretty = null;
        var trimmed = text.AsSpan().Trim();
        if (trimmed.Length < 2 || !((trimmed[0] == '{' && trimmed[^1] == '}') || (trimmed[0] == '[' && trimmed[^1] == ']'))) return false;
        try
        {
            using var document = JsonDocument.Parse(text);
            pretty = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
