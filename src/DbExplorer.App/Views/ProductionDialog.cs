using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Views;

/// <summary>"This writes on production": the databases, the statements, and a Cancel button that has the focus.</summary>
public static class ProductionDialog
{
    public static Window Create(ProductionWriteRequest request)
    {
        var body = new StackPanel { Spacing = 12, Width = 460 };
        body.Children.Add(new TextBlock { Text = request.Action, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });

        var targets = new StackPanel { Spacing = 8 };
        foreach (var target in request.Targets)
        {
            var where = new TextBlock { Text = $"{target.Server} · database {target.Database}", TextWrapping = TextWrapping.Wrap };
            where.Classes.Add("dim");
            where.Classes.Add("small");
            var name = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    new TextBlock { Text = target.Connection, VerticalAlignment = VerticalAlignment.Center },
                    new EnvironmentBadge { Environment = DbEnvironment.Production },
                },
            };
            targets.Children.Add(new StackPanel { Spacing = 2, Children = { name, where } });
        }
        body.Children.Add(new ScrollViewer { MaxHeight = 160, Content = targets });

        var lines = new SelectableTextBlock { Text = string.Join("\n", request.Summary), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        lines.Bind(SelectableTextBlock.FontFamilyProperty, lines.GetResourceObservable("DbxMonoFont"));
        var box = new Border { Child = new ScrollViewer { MaxHeight = 180, Content = lines }, Padding = new Avalonia.Thickness(10, 8) };
        box.Classes.Add("surface");
        body.Children.Add(box);

        var snooze = new CheckBox { Content = ProductionWriteRequest.SnoozeText, IsVisible = request.CanSnooze };
        body.Children.Add(snooze);

        // Cancel is the default and the Escape button: Enter on a reflex writes nothing.
        var confirm = DialogShell.Button("Write on production", danger: true);
        var cancel = DialogShell.Button("Cancel", isDefault: true, isCancel: true);
        var window = DialogShell.Create("Confirm write on production", "IconWarning", body, [confirm, cancel],
            maxWidth: 580, tone: DialogTone.Danger);
        confirm.Click += (_, _) => window.Close(new ProductionWriteAnswer(true, snooze.IsChecked == true));
        cancel.Click += (_, _) => window.Close(new ProductionWriteAnswer(false));
        window.Opened += (_, _) => cancel.Focus();
        return window;
    }
}
