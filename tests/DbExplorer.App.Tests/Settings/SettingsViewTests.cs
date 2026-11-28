using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.Appearance;
using DbExplorer.App.Settings;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests.Settings;

/// <summary>Headless tests of the Settings dialog and of the live effects of the settings (theme, accent, scale, density, zebra).</summary>
[Collection(GlobalSettingsCollection.Name)]
public class SettingsViewTests : IDisposable
{
    private readonly SettingsService _previous = SettingsService.Instance;

    public SettingsViewTests()
    {
        AppearanceApplier.ResetTracking();
        AppearanceApplier.Apply(new AppSettings());
    }

    public void Dispose()
    {
        AppearanceApplier.Apply(new AppSettings());
        AppearanceApplier.ResetTracking();
        SettingsService.Instance = _previous;
    }

    private static (SettingsService Service, SettingsViewModel Vm) NewService(string? page = null)
    {
        var service = new SettingsService(new AppSettings());
        SettingsService.Instance = service;
        service.Changed += s => AppearanceApplier.Apply(s);
        var ctx = new SettingsContext(service)
        {
            Upgrade = new OptionsViewModel(null, _ => null),
            AutoUpdateCheck = new(() => true, _ => { }),
            ShowFolders = new(() => true, _ => { }),
            MonospaceFonts = ["Consolas", "Cascadia Mono"],
        };
        return (service, new SettingsViewModel(ctx, page));
    }

    // The accent style paints the template presenter, not Button.Background.
    private static Color FillOf(Button button) =>
        ((ISolidColorBrush)button.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(p => p.Name == "PART_ContentPresenter").Background!).Color;

    private static T Brush<T>(string key, ThemeVariant variant) where T : class =>
        (T)(Application.Current!.TryGetResource(key, variant, out var v) ? v! : throw new InvalidOperationException(key));

    [AvaloniaFact]
    public void TheDialogOpensWithEveryPage_AndRendersEach()
    {
        var (_, vm) = NewService();
        var window = new SettingsDialog { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "PageList");
        Assert.Equal(vm.Pages.Count, list.ItemCount);

        foreach (var page in vm.Pages)
        {
            vm.SelectedPage = page;
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(window.GetVisualDescendants().OfType<ContentControl>().FirstOrDefault(c => ReferenceEquals(c.Content, page)));
            Assert.True(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == page.Title && t.IsEffectivelyVisible && t.Classes.Contains("h1")),
                $"title of {page.Title}");
        }

        window.Close();
    }

    [AvaloniaFact]
    public void ThePageControlsFollowTheKindOfSetting()
    {
        var (_, vm) = NewService("appearance");
        var window = new SettingsDialog { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(AccentPalette.Presets.Count + 3 + 3, window.GetVisualDescendants().OfType<ToggleButton>().Count(t => t.Classes.Contains("segment") && t.IsEffectivelyVisible));
        Assert.Single(window.GetVisualDescendants().OfType<NumericUpDown>());

        vm.Select("editor");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(5, window.GetVisualDescendants().OfType<CheckBox>().Count());
        Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(), c => c.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void ClickingAChip_ChangesTheSetting()
    {
        var (service, vm) = NewService("appearance");
        var window = new SettingsDialog { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var dark = window.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Classes.Contains("segment") && t.IsEffectivelyVisible && (t.Content as StackPanel)?.Children.OfType<TextBlock>().Any(x => x.Text == "Dark") == true);
        dark.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ThemeChoice.Dark, service.Current.Appearance.Theme);
        window.Close();
    }

    [AvaloniaFact]
    public void SearchBox_NarrowsTheCategoryList()
    {
        var (_, vm) = NewService();
        var window = new SettingsDialog { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SearchBox").Text = "tab size";
        Dispatcher.UIThread.RunJobs();

        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "PageList");
        Assert.Equal(1, list.ItemCount);
        Assert.Equal("Editor", vm.SelectedPage!.Title);
        window.Close();
    }

    [AvaloniaFact]
    public void Theme_Switch_RepaintsAControl()
    {
        var (service, _) = NewService();
        var box = new Border { Width = 40, Height = 40 };
        box.Bind(Border.BackgroundProperty, box.GetResourceObservable("DbxContentBg"));
        var window = new Window { Content = box, Width = 100, Height = 100 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        service.Update(s => s with { Appearance = s.Appearance with { Theme = ThemeChoice.Light } });
        Dispatcher.UIThread.RunJobs();
        var light = ((ISolidColorBrush)box.Background!).Color;
        service.Update(s => s with { Appearance = s.Appearance with { Theme = ThemeChoice.Dark } });
        Dispatcher.UIThread.RunJobs();
        var dark = ((ISolidColorBrush)box.Background!).Color;

        Assert.Equal(Colors.White, light);
        Assert.NotEqual(light, dark);
        Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);

        service.Update(s => s with { Appearance = s.Appearance with { Theme = ThemeChoice.System } });
        Assert.Equal(ThemeVariant.Default, Application.Current.RequestedThemeVariant);
        window.Close();
    }

    [AvaloniaFact]
    public void Accent_Switch_RepaintsThePrimaryButton_InBothThemes()
    {
        var (service, _) = NewService();
        var button = new Button { Content = "Go", Classes = { "accent" } };
        var window = new Window { Content = button, Width = 200, Height = 100 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var before = FillOf(button);

        service.Update(s => s with { Appearance = s.Appearance with { Accent = AccentPreset.Green } });
        Dispatcher.UIThread.RunJobs();
        var green = FillOf(button);

        Assert.NotEqual(before, green);
        Assert.Equal(Color.Parse(AccentPalette.For(AccentPreset.Green, dark: false).ButtonBg), green);
        Assert.Equal(Color.Parse(AccentPalette.For(AccentPreset.Green, dark: true).ButtonBg), Brush<ISolidColorBrush>("DbxAccentButtonBg", ThemeVariant.Dark).Color);

        service.Update(s => s with { Appearance = s.Appearance with { Accent = AccentPreset.Blue } });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, FillOf(button));   // Blue puts the original palette back
        window.Close();
    }

    [AvaloniaFact]
    public void Accent_AlsoTintsTheRowSelection_AndBlueRestoresIt()
    {
        var (service, _) = NewService();
        var original = Brush<ISolidColorBrush>("DbxRowSelected", ThemeVariant.Light).Color;

        service.Update(s => s with { Appearance = s.Appearance with { Accent = AccentPreset.Orange } });
        var orange = Brush<ISolidColorBrush>("DbxRowSelected", ThemeVariant.Light).Color;
        service.Update(s => s with { Appearance = s.Appearance with { Accent = AccentPreset.Blue } });

        Assert.NotEqual(original, orange);
        Assert.True(orange.R > orange.B, "an orange tint is warm");
        Assert.Equal(original, Brush<ISolidColorBrush>("DbxRowSelected", ThemeVariant.Light).Color);
    }

    [AvaloniaFact]
    public void Density_SetsTheGridAndTreeRowHeights()
    {
        var (service, _) = NewService();

        service.Update(s => s with { Appearance = s.Appearance with { Density = GridDensity.Compact } });
        Assert.Equal(24d, Application.Current!.Resources["DbxGridRowHeight"]);
        Assert.Equal(22d, Application.Current.Resources["TreeViewItemMinHeight"]);

        service.Update(s => s with { Appearance = s.Appearance with { Density = GridDensity.Spacious } });
        Assert.Equal(32d, Application.Current.Resources["DbxGridRowHeight"]);

        service.Update(s => s with { Appearance = s.Appearance with { Density = GridDensity.Comfortable } });
        Assert.Equal(28d, Application.Current.Resources["DbxGridRowHeight"]);
    }

    [AvaloniaFact]
    public void Zebra_Off_MakesTheStripeTransparent_OnRestoresIt()
    {
        var (service, _) = NewService();
        var stripe = Brush<ISolidColorBrush>("DbxGridZebra", ThemeVariant.Light).Color;

        service.Update(s => s with { Results = s.Results with { Zebra = false } });
        Assert.Equal(Colors.Transparent, Brush<ISolidColorBrush>("DbxGridZebra", ThemeVariant.Light).Color);
        Assert.Equal(Colors.Transparent, Brush<ISolidColorBrush>("DbxGridZebra", ThemeVariant.Dark).Color);

        service.Update(s => s with { Results = s.Results with { Zebra = true } });
        Assert.Equal(stripe, Brush<ISolidColorBrush>("DbxGridZebra", ThemeVariant.Light).Color);
    }

    [AvaloniaFact]
    public void FontsAndSizes_AreResources()
    {
        var (service, _) = NewService();

        service.Update(s => s with
        {
            Editor = s.Editor with { FontFamily = "Cascadia Mono", FontSize = 17 },
            Results = s.Results with { GridFontSize = 15 },
        });

        var resources = Application.Current!.Resources;
        Assert.Equal(17d, resources["DbxEditorFontSize"]);
        Assert.Equal(15d, resources["DbxGridFontSize"]);
        Assert.StartsWith("Cascadia Mono", ((FontFamily)resources["DbxEditorFont"]!).Name);

        service.Update(s => s with { Editor = s.Editor with { FontFamily = "" } });
        Assert.Equal("Consolas", ((FontFamily)resources["DbxEditorFont"]!).Name);
    }

    [AvaloniaFact]
    public void UiScale_ScalesTheWindowContent_WithoutClipping()
    {
        var (service, _) = NewService();
        var panel = new Border { Width = 200, Height = 100, Name = "Probe" };
        var window = new Window { Content = panel, Width = 800, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var before = panel.Bounds;

        service.Update(s => s with { Appearance = s.Appearance with { UiScalePercent = 150 } });
        Dispatcher.UIThread.RunJobs();

        var transformed = panel.TransformToVisual(window)!.Value;
        var corner = new Point(200, 100).Transform(transformed);
        Assert.Equal(300, corner.X - new Point(0, 0).Transform(transformed).X, 0.5);
        Assert.Equal(before.Size, panel.Bounds.Size);   // layout size is unchanged: the content is scaled as a whole
        Assert.Same(window, panel.GetVisualAncestors().OfType<Window>().First());
        Assert.Contains(panel.GetVisualAncestors(), a => a is LayoutTransformControl);

        service.Update(s => s with { Appearance = s.Appearance with { UiScalePercent = 100 } });
        window.Close();
    }

    [AvaloniaFact]
    public void ScriptEditor_FollowsTheEditorSettingsLive()
    {
        var (service, _) = NewService();
        var view = new ScriptTabView();
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var editor = view.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single();
        Assert.True(editor.ShowLineNumbers);
        Assert.False(editor.WordWrap);

        service.Update(s => s with { Editor = s.Editor with { LineNumbers = false, WordWrap = true, ShowWhitespace = true, TabSize = 2, HighlightCurrentLine = false } });
        Dispatcher.UIThread.RunJobs();

        Assert.False(editor.ShowLineNumbers);
        Assert.True(editor.WordWrap);
        Assert.True(editor.Options.ShowSpaces);
        Assert.True(editor.Options.ShowTabs);
        Assert.Equal(2, editor.Options.IndentationSize);
        Assert.False(editor.Options.HighlightCurrentLine);

        service.Update(s => s with { Editor = s.Editor with { FontSize = 20 } });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(20, editor.FontSize);
        window.Close();
    }

    [AvaloniaFact]
    public void ANewScriptEditor_StartsFromTheCurrentSettings()
    {
        var (service, _) = NewService();
        service.Update(s => s with { Editor = s.Editor with { LineNumbers = false, TabSize = 8 } });
        var view = new ScriptTabView();
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var editor = view.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single();

        Assert.False(editor.ShowLineNumbers);
        Assert.Equal(8, editor.Options.IndentationSize);
        window.Close();
    }
}
