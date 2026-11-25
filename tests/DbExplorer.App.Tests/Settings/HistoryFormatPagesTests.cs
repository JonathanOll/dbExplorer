using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.Settings;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.Tests.Settings;

/// <summary>The "Query history" and "SQL formatting" pages of the Settings dialog (they replaced the groups of the old Options dialog).</summary>
public sealed class HistoryFormatPagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-histpages-" + Guid.NewGuid().ToString("N"));
    private bool _record = true;
    private SqlFormatOptions _format = new();
    private readonly List<SqlFormatOptions> _formatCalls = [];

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private QueryHistory NewHistory(int entries)
    {
        var history = new QueryHistory(Path.Combine(_dir, "history.jsonl"));
        for (var i = 0; i < entries; i++)
            history.Append(new HistoryEntry(Guid.NewGuid(), DateTime.UtcNow, Guid.NewGuid(), "c", DbKind.Sqlite, "d", "SELECT " + i, 1, 1, null));
        return history;
    }

    private SettingsViewModel Vm(QueryHistory? history = null, string? page = null) =>
        new(new SettingsContext(new SettingsService(new AppSettings()))
        {
            RecordHistory = new(() => _record, v => _record = v),
            History = history,
            GetFormat = () => _format,
            SetFormat = f => { _format = f.Normalized(); _formatCalls.Add(f); },
        }, page);

    private static T Item<T>(SettingsPageViewModel page, string label) where T : SettingItemViewModel => page.Items.OfType<T>().Single(i => i.Label == label);

    // ---- registration and search ----

    [Fact]
    public void BothPages_AreListed_AndLeftOutWithoutTheirFeature()
    {
        var vm = Vm();
        Assert.Contains("Query history", vm.Pages.Select(p => p.Title));
        Assert.Contains("SQL formatting", vm.Pages.Select(p => p.Title));

        var bare = new SettingsViewModel(new SettingsContext(new SettingsService(new AppSettings())));
        Assert.DoesNotContain(bare.Pages, p => p.Id is "history" or "format");
    }

    [Theory]
    [InlineData("history", "history")]
    [InlineData("record", "history")]
    [InlineData("format", "format")]
    [InlineData("comma", "format")]
    [InlineData("indent", "format")]
    [InlineData("uppercase", "format")]
    public void TheSettingsSearch_FindsThePages(string query, string pageId)
    {
        var vm = Vm(NewHistory(0));
        vm.SearchText = query;

        Assert.Contains(vm.VisiblePages, p => p.Id == pageId);
    }

    // ---- query history ----

    [Fact]
    public void RecordToggle_WritesTheConfigFlag()
    {
        var page = Vm(NewHistory(0)).Pages.Single(p => p.Id == "history");
        var toggle = Item<ToggleSettingItem>(page, "Record query history");
        Assert.True(toggle.Value);

        toggle.Value = false;
        Assert.False(_record);

        page.ResetToDefaults();
        Assert.True(_record);
    }

    [Fact]
    public void ClearHistory_NeedsASecondClick_AndSaysHowMuchWasStored()
    {
        var history = NewHistory(3);
        var page = Vm(history).Pages.Single(p => p.Id == "history");
        var clear = Item<ClearHistoryItem>(page, "Clear history");
        Assert.StartsWith("3 entries, ", clear.Info);

        clear.RunCommand.Execute(null);
        Assert.Equal(3, history.Count);
        Assert.Contains("again", clear.ButtonText);

        clear.RunCommand.Execute(null);
        Assert.Equal(0, history.Count);
        Assert.StartsWith("History cleared. 0 entries", clear.Info);
        Assert.Equal("Clear history…", clear.ButtonText);
    }

    [Fact]
    public void ResettingThePage_DoesNotClearTheHistory()
    {
        var history = NewHistory(2);
        var page = Vm(history).Pages.Single(p => p.Id == "history");

        page.ResetToDefaults();

        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void WithoutAHistoryStore_TheClearRowIsHidden()
    {
        var page = Vm().Pages.Single(p => p.Id == "history");
        Assert.DoesNotContain(page.Items, i => i is ClearHistoryItem);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(3 * 1024 * 1024, "3 MB")]
    public void FileSizes_AreReadable(long bytes, string expected) => Assert.Equal(expected, ClearHistoryItem.FormatSize(bytes));

    // ---- SQL formatting ----

    [Fact]
    public void Defaults_ShowAnUppercaseFourSpacePreview()
    {
        var page = (FormatSettingsPage)Vm().Pages.Single(p => p.Id == "format");

        Assert.Equal(KeywordCase.Upper, Item<ChoiceSettingItem>(page, "Keyword case").Selected.Value);
        Assert.Equal(4, Item<ChoiceSettingItem>(page, "Indentation").Selected.Value);
        Assert.Equal(CommaPosition.Trailing, Item<ChoiceSettingItem>(page, "Comma position").Selected.Value);
        var preview = page.Preview.Text.ReplaceLineEndings("\n");
        Assert.StartsWith("SELECT\n    o.id,\n    c.name,", preview);
        Assert.Contains("\nWHERE o.status IN ('new', 'paid')\n    AND o.created", preview);
    }

    [Fact]
    public void TheStoredOptions_FillTheChoices()
    {
        _format = new SqlFormatOptions { KeywordCase = KeywordCase.Keep, IndentSize = 8, CommaPosition = CommaPosition.Leading, MaxLineWidth = 60 };
        var page = Vm().Pages.Single(p => p.Id == "format");

        Assert.Equal(KeywordCase.Keep, Item<ChoiceSettingItem>(page, "Keyword case").Selected.Value);
        Assert.Equal(8, Item<ChoiceSettingItem>(page, "Indentation").Selected.Value);
        Assert.Equal(CommaPosition.Leading, Item<ChoiceSettingItem>(page, "Comma position").Selected.Value);
        Assert.Equal(60m, Item<NumberSettingItem>(page, "Maximum line width").Value);

        _format = new SqlFormatOptions { UseTabs = true };
        page.Refresh();
        Assert.Equal("Tab", Item<ChoiceSettingItem>(page, "Indentation").Selected.Label);
    }

    [Fact]
    public void ChangingAChoice_AppliesAtOnce_AndUpdatesThePreview()
    {
        var page = (FormatSettingsPage)Vm().Pages.Single(p => p.Id == "format");
        var chips = (ChoiceSettingItem i, int n) => i.Options[n];

        var keywords = Item<ChoiceSettingItem>(page, "Keyword case");
        keywords.Selected = chips(keywords, 1);
        var comma = Item<ChoiceSettingItem>(page, "Comma position");
        comma.Selected = chips(comma, 1);
        var indent = Item<ChoiceSettingItem>(page, "Indentation");
        indent.Selected = chips(indent, 0);

        Assert.StartsWith("select\n  o.id\n  , c.name", page.Preview.Text.ReplaceLineEndings("\n"));
        Assert.Equal(new SqlFormatOptions { KeywordCase = KeywordCase.Lower, CommaPosition = CommaPosition.Leading, IndentSize = 2 }, _format);
    }

    [Fact]
    public void Tabs_ArePreviewedAsSpaces_ButStoredAsTabs()
    {
        var page = (FormatSettingsPage)Vm().Pages.Single(p => p.Id == "format");
        var indent = Item<ChoiceSettingItem>(page, "Indentation");

        indent.Selected = indent.Options.Single(o => o.Label == "Tab");

        Assert.DoesNotContain('\t', page.Preview.Text);
        Assert.True(_format.UseTabs);

        indent.Selected = indent.Options[0];
        Assert.False(_format.UseTabs);
        Assert.Equal(2, _format.IndentSize);
    }

    [Fact]
    public void MaxWidth_IsClamped_AndKeepsTheOtherOptions()
    {
        _format = new SqlFormatOptions { KeywordCase = KeywordCase.Lower };
        var page = Vm().Pages.Single(p => p.Id == "format");
        var width = Item<NumberSettingItem>(page, "Maximum line width");

        width.Value = 5;
        Assert.Equal(40, _format.MaxLineWidth);
        Assert.Equal(KeywordCase.Lower, _format.KeywordCase);

        width.Value = 120;
        Assert.Equal(120, _format.MaxLineWidth);
    }

    [Fact]
    public void ResetToDefaults_RestoresTheDefaultStyle()
    {
        _format = new SqlFormatOptions { KeywordCase = KeywordCase.Keep, UseTabs = true, CommaPosition = CommaPosition.Leading, MaxLineWidth = 60 };
        var page = (FormatSettingsPage)Vm().Pages.Single(p => p.Id == "format");

        page.ResetToDefaults();

        Assert.Equal(new SqlFormatOptions(), _format);
        Assert.StartsWith("SELECT\n    o.id,", page.Preview.Text.ReplaceLineEndings("\n"));
    }

    // ---- the dialog ----

    [AvaloniaFact]
    public void TheFormatPage_ShowsFourChoicesAndFollowsThePreview()
    {
        var vm = Vm(null, "format");
        var window = new SettingsDialog { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var preview = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PreviewText");
        var page = (FormatSettingsPage)vm.SelectedPage!;
        Assert.Equal(page.Preview.Text, preview.Text);

        Item<ChoiceSettingItem>(page, "Keyword case").Selected = Item<ChoiceSettingItem>(page, "Keyword case").Options[1];
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("select", preview.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void TheHistoryPage_ClearButtonClearsOnSecondClick()
    {
        var history = NewHistory(4);
        var vm = Vm(history, "history");
        var window = new SettingsDialog { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Clear history…");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("4 entries") == true);

        button.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, history.Count);
        button.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, history.Count);
        window.Close();
    }
}
