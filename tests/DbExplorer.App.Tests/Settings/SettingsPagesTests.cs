using DbExplorer.App.Settings;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests.Settings;

/// <summary>View-model level tests of the Settings pages: values are written through the service (applied and persisted), reset, searched, clamped.</summary>
public class SettingsPagesTests
{
    private sealed class Rig
    {
        public SettingsService Service { get; } = new(new AppSettings());
        public List<AppSettings> Saved { get; } = [];
        public bool Auto = true, Folders = true, Groups = true, Scripts = true;
        public string? CopiedText;
        public string? AppliedPath = "unset";
        public SettingsViewModel Vm { get; }

        public Rig()
        {
            Service.Persist = s => { Saved.Add(s); return true; };
            var ctx = new SettingsContext(Service)
            {
                Upgrade = new OptionsViewModel(null, p => p.Contains("bad") ? "Folder not found." : null),
                ApplyDbUpdatePath = p => AppliedPath = p,
                AutoUpdateCheck = new(() => Auto, v => Auto = v),
                ShowFolders = new(() => Folders, v => Folders = v),
                ShowGroups = new(() => Groups, v => Groups = v),
                ShowScripts = new(() => Scripts, v => Scripts = v),
                CopyText = t => { CopiedText = t; return Task.CompletedTask; },
                MonospaceFonts = ["Consolas", "Cascadia Mono", "Courier New"],
            };
            Vm = new SettingsViewModel(ctx);
        }

        public T Page<T>() where T : SettingsPageViewModel => Vm.Pages.OfType<T>().Single();
        public static T Item<T>(SettingsPageViewModel page, string label) where T : SettingItemViewModel => page.Items.OfType<T>().Single(i => i.Label == label);
    }

    [Fact]
    public void TheDialogListsEveryPageInOrder_NoneEmpty()
    {
        var rig = new Rig();

        Assert.Equal(["General", "Appearance", "Editor", "Results", "Shortcuts", "Updates", "Database upgrade"], rig.Vm.Pages.Select(p => p.Title));
        Assert.All(rig.Vm.Pages.Where(p => p.IsGeneric), p => Assert.NotEmpty(p.Items));
        Assert.Equal("General", rig.Vm.SelectedPage!.Title);
    }

    [Fact]
    public void PagesWithoutTheirBackingFeature_AreLeftOut()
    {
        var vm = new SettingsViewModel(new SettingsContext(new SettingsService(new())));

        Assert.Equal(["Appearance", "Editor", "Results", "Shortcuts"], vm.Pages.Select(p => p.Title));
    }

    [Fact]
    public void InitialPage_IsSelectedById()
    {
        var rig = new Rig();
        Assert.Equal("Editor", new SettingsViewModel(new SettingsContext(rig.Service) { Upgrade = new(null, _ => null) }, "editor").SelectedPage!.Title);
    }

    // ---- Appearance ----

    [Fact]
    public void Theme_IsAppliedAndPersisted()
    {
        var rig = new Rig();
        var theme = Rig.Item<ChoiceSettingItem>(rig.Page<AppearanceSettingsPage>(), "Theme");

        Assert.Equal(ThemeChoice.System, theme.Selected.Value);
        theme.Selected = theme.Options.Single(o => o.Label == "Dark");

        Assert.Equal(ThemeChoice.Dark, rig.Service.Current.Appearance.Theme);
        Assert.Equal(ThemeChoice.Dark, rig.Saved.Last().Appearance.Theme);
    }

    [Fact]
    public void Accent_OffersSixPresetsWithSwatches()
    {
        var accent = Rig.Item<ChoiceSettingItem>(new Rig().Page<AppearanceSettingsPage>(), "Accent colour");

        Assert.Equal(["Blue", "Teal", "Green", "Orange", "Pink", "Purple"], accent.Options.Select(o => o.Label));
        Assert.All(accent.Options, o => Assert.StartsWith("#", o.Swatch));
    }

    [Theory]
    [InlineData(400, 150)]
    [InlineData(10, 90)]
    [InlineData(125, 125)]
    public void UiScale_IsClampedToTheRange(int typed, int stored)
    {
        var rig = new Rig();
        var scale = Rig.Item<NumberSettingItem>(rig.Page<AppearanceSettingsPage>(), "UI scale");

        scale.Value = typed;

        Assert.Equal(stored, rig.Service.Current.Appearance.UiScalePercent);
        Assert.Equal(stored, scale.Value);
    }

    [Fact]
    public void BlankNumberBox_KeepsTheStoredValue()
    {
        var rig = new Rig();
        var size = Rig.Item<NumberSettingItem>(rig.Page<EditorSettingsPage>(), "Font size");
        size.Value = 16;
        rig.Saved.Clear();

        size.Value = null;

        Assert.Equal(16, size.Value);
        Assert.Empty(rig.Saved);
    }

    [Fact]
    public void Density_PicksTheRowHeight()
    {
        var rig = new Rig();
        var density = Rig.Item<ChoiceSettingItem>(rig.Page<AppearanceSettingsPage>(), "Row density");

        density.Selected = density.Options.Single(o => o.Label == "Compact");

        Assert.Equal(24, rig.Service.Current.Appearance.RowHeight);
    }

    [Fact]
    public void ResetToDefaults_RestoresOnlyThePage_AndPersists()
    {
        var rig = new Rig();
        var appearance = rig.Page<AppearanceSettingsPage>();
        Rig.Item<NumberSettingItem>(appearance, "UI scale").Value = 140;
        Rig.Item<ChoiceSettingItem>(appearance, "Theme").Selected = Rig.Item<ChoiceSettingItem>(appearance, "Theme").Options[2];
        Rig.Item<NumberSettingItem>(rig.Page<EditorSettingsPage>(), "Font size").Value = 18;

        appearance.ResetToDefaults();

        Assert.Equal(new AppearanceSettings(), rig.Service.Current.Appearance);
        Assert.Equal(18, rig.Service.Current.Editor.FontSize);   // other pages are untouched
        Assert.Equal(new AppearanceSettings(), rig.Saved.Last().Appearance);
    }

    // ---- Editor ----

    [Fact]
    public void EditorSettings_AreWrittenThroughTheService()
    {
        var rig = new Rig();
        var editor = rig.Page<EditorSettingsPage>();

        Rig.Item<ToggleSettingItem>(editor, "Word wrap").Value = true;
        Rig.Item<ToggleSettingItem>(editor, "Line numbers").Value = false;
        Rig.Item<ToggleSettingItem>(editor, "Show whitespace").Value = true;
        Rig.Item<ToggleSettingItem>(editor, "Highlight the current line").Value = false;
        var tab = Rig.Item<ChoiceSettingItem>(editor, "Tab size");
        tab.Selected = tab.Options.Single(o => o.Label == "8");
        Rig.Item<NumberSettingItem>(editor, "Font size").Value = 99;

        var e = rig.Service.Current.Editor;
        Assert.True(e.WordWrap);
        Assert.False(e.LineNumbers);
        Assert.True(e.ShowWhitespace);
        Assert.False(e.HighlightCurrentLine);
        Assert.Equal(8, e.TabSize);
        Assert.Equal(24, e.FontSize);
    }

    [Fact]
    public void EditorFontList_HasTheDefaultFirst_ThenTheInstalledFamilies()
    {
        var font = Rig.Item<ChoiceSettingItem>(new Rig().Page<EditorSettingsPage>(), "Font family");

        Assert.Equal(["Default (Consolas)", "Cascadia Mono", "Consolas", "Courier New"], font.Options.Select(o => o.Label));
        Assert.True(font.UseCombo);
        Assert.Equal("", font.Selected.Value);
    }

    [Fact]
    public void FontNoLongerInstalled_FallsBackToTheDefaultInTheList()
    {
        var rig = new Rig();
        rig.Service.Update(s => s with { Editor = s.Editor with { FontFamily = "Gone Mono" } });

        Assert.Equal("", Rig.Item<ChoiceSettingItem>(rig.Page<EditorSettingsPage>(), "Font family").Selected.Value);
    }

    // ---- Results ----

    [Fact]
    public void ResultsSettings_AreWrittenThroughTheService()
    {
        var rig = new Rig();
        var results = rig.Page<ResultsSettingsPage>();

        Rig.Item<ToggleSettingItem>(results, "Alternate row shading").Value = false;
        Rig.Item<ToggleSettingItem>(results, "Thousands separator on numbers").Value = true;
        var nulls = Rig.Item<ChoiceSettingItem>(results, "NULL display");
        nulls.Selected = nulls.Options.Single(o => o.Label == "(null)");
        var dates = Rig.Item<ChoiceSettingItem>(results, "Date format");
        dates.Selected = dates.Options.Single(o => o.Label == "Locale");
        Rig.Item<NumberSettingItem>(results, "Default row limit of new scripts").Value = 5_000_000;
        Rig.Item<NumberSettingItem>(results, "Longest cell text").Value = 1;
        Rig.Item<NumberSettingItem>(results, "Grid font size").Value = 14;

        var r = rig.Service.Current.Results;
        Assert.False(r.Zebra);
        Assert.True(r.ThousandsSeparator);
        Assert.Equal("(null)", r.NullLabel);
        Assert.Equal(DateDisplay.Locale, r.DateFormat);
        Assert.Equal(100_000, r.DefaultRowLimit);
        Assert.Equal(20, r.MaxCellLength);
        Assert.Equal(14, r.GridFontSize);
        Assert.Equal(r, rig.Saved.Last().Results);
    }

    // ---- Search ----

    [Fact]
    public void Search_FiltersSettingsByLabel_AndHidesPagesWithoutMatch()
    {
        var rig = new Rig();

        rig.Vm.SearchText = "wrap";

        Assert.Equal(["Editor"], rig.Vm.VisiblePages.Select(p => p.Title));
        Assert.Equal("Editor", rig.Vm.SelectedPage!.Title);
        var shown = rig.Page<EditorSettingsPage>().VisibleGroups.SelectMany(g => g.Items).Select(i => i.Label);
        Assert.Equal(["Word wrap"], shown);
    }

    [Fact]
    public void Search_IsCaseInsensitive_AndFindsByKeyword()
    {
        var rig = new Rig();

        rig.Vm.SearchText = "DARK";

        Assert.Equal(["Appearance"], rig.Vm.VisiblePages.Select(p => p.Title));
        Assert.Equal(["Theme"], rig.Page<AppearanceSettingsPage>().VisibleGroups.SelectMany(g => g.Items).Select(i => i.Label));
    }

    [Fact]
    public void Search_NamingAPage_ShowsItAllAndSelectsIt()
    {
        var rig = new Rig();

        rig.Vm.SearchText = "results";

        Assert.Contains(rig.Page<ResultsSettingsPage>(), rig.Vm.VisiblePages);
        Assert.Equal(rig.Page<ResultsSettingsPage>().Items.Count(), rig.Page<ResultsSettingsPage>().VisibleGroups.Sum(g => g.Items.Count));
    }

    [Fact]
    public void Search_WithNoMatch_SaysSo_AndClearingRestoresEverything()
    {
        var rig = new Rig();

        rig.Vm.SearchText = "zzzz";
        Assert.True(rig.Vm.NoPageMatches);
        Assert.Empty(rig.Vm.VisiblePages);

        rig.Vm.ClearSearchCommand.Execute(null);

        Assert.False(rig.Vm.NoPageMatches);
        Assert.Equal(rig.Vm.Pages.Count, rig.Vm.VisiblePages.Count);
        Assert.NotNull(rig.Vm.SelectedPage);
    }

    // ---- Other pages ----

    [Fact]
    public void GeneralAndUpdatesToggles_WriteTheirConfigFlags()
    {
        var rig = new Rig();

        Rig.Item<ToggleSettingItem>(rig.Page<GeneralSettingsPage>(), "Show folders").Value = false;
        Rig.Item<ToggleSettingItem>(rig.Page<GeneralSettingsPage>(), "Show the Scripts section").Value = false;
        Rig.Item<ToggleSettingItem>(rig.Page<UpdatesSettingsPage>(), "Check for updates automatically").Value = false;

        Assert.False(rig.Folders);
        Assert.True(rig.Groups);
        Assert.False(rig.Scripts);
        Assert.False(rig.Auto);
    }

    [Fact]
    public void DatabaseUpgrade_ValidFolderIsAppliedAtOnce_InvalidOneIsNot()
    {
        var rig = new Rig();
        var page = rig.Page<DatabaseUpgradeSettingsPage>();

        page.Options.DbUpdatePath = @"C:\bad";
        Assert.False(page.Commit());
        Assert.Equal("unset", rig.AppliedPath);
        Assert.False(rig.Vm.TryCommitPending());
        Assert.Equal("Database upgrade", rig.Vm.SelectedPage!.Title);

        page.Options.DbUpdatePath = @"  C:\dbupdate ";
        Assert.True(page.Commit());
        Assert.Equal(@"C:\dbupdate", rig.AppliedPath);
        Assert.True(rig.Vm.TryCommitPending());
    }

    [Fact]
    public void DatabaseUpgrade_ResetSwitchesTheFeatureOff()
    {
        var rig = new Rig();
        var page = rig.Page<DatabaseUpgradeSettingsPage>();
        page.Options.DbUpdatePath = @"C:\dbupdate";
        page.Commit();

        page.ResetToDefaults();

        Assert.Null(rig.AppliedPath);
        Assert.Equal("", page.Options.DbUpdatePath);
    }

    [Fact]
    public void ChangesMadeElsewhere_RefreshTheItems()
    {
        var rig = new Rig();
        var wrap = Rig.Item<ToggleSettingItem>(rig.Page<EditorSettingsPage>(), "Word wrap");
        var changed = new List<string?>();
        wrap.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        rig.Service.Update(s => s with { Editor = s.Editor with { WordWrap = true } });

        Assert.Contains("Value", changed);
        Assert.True(wrap.Value);
    }

    [Fact]
    public void Dispose_StopsListening()
    {
        var rig = new Rig();
        var wrap = Rig.Item<ToggleSettingItem>(rig.Page<EditorSettingsPage>(), "Word wrap");
        var count = 0;
        wrap.PropertyChanged += (_, _) => count++;
        rig.Vm.Dispose();

        rig.Service.Update(s => s with { Editor = s.Editor with { WordWrap = true } });

        Assert.Equal(0, count);
    }

    [Fact]
    public void UnpersistedService_StillApplies()
    {
        var service = new SettingsService(new());
        var seen = 0;
        service.Changed += _ => seen++;

        Assert.True(service.Update(s => s with { Appearance = s.Appearance with { Theme = ThemeChoice.Light } }));
        Assert.True(service.Update(s => s));   // no change, no event

        Assert.Equal(1, seen);
    }

    [Fact]
    public void FailedPersist_IsReportedByTheService_ButTheChangeStaysApplied()
    {
        var service = new SettingsService(new()) { Persist = _ => false };

        var saved = service.Update(s => s with { Editor = s.Editor with { WordWrap = true } });

        Assert.False(saved);
        Assert.True(service.Current.Editor.WordWrap);
    }
}
