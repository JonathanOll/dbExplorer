using DbExplorer.App.Settings;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests.Settings;

public class ShortcutsPageTests
{
    private static (ShortcutsSettingsPage Page, Func<string?> Copied) Make()
    {
        string? copied = null;
        var ctx = new SettingsContext(new SettingsService(new())) { CopyText = t => { copied = t; return Task.CompletedTask; } };
        return (new ShortcutsSettingsPage(ctx), () => copied);
    }

    [Fact]
    public void ListsEveryShortcutOfTheCatalog()
    {
        var (page, _) = Make();

        var shown = page.VisibleSections.SelectMany(s => s.Items).ToList();

        Assert.Equal(ShortcutCatalog.Sections.Sum(s => s.Items.Count), shown.Count);
        Assert.Equal(ShortcutCatalog.Sections.Select(s => s.Title), page.VisibleSections.Select(s => s.Title));
    }

    [Fact]
    public void TheSettingsShortcutIsListed() =>
        Assert.Contains(ShortcutCatalog.Sections.SelectMany(s => s.Items), i => i.Keys == "Ctrl+," && i.Text.Contains("settings"));

    [Fact]
    public void SearchFiltersByKeyAndByText()
    {
        var (page, _) = Make();

        page.SearchText = "ctrl+shift+t";
        Assert.All(page.VisibleSections.SelectMany(s => s.Items), i => Assert.Contains("Ctrl+Shift+T", i.Keys, StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(page.VisibleSections);

        page.SearchText = "estimated";
        Assert.Contains(page.VisibleSections.SelectMany(s => s.Items), i => i.Text.Contains("estimated execution plan"));

        page.SearchText = "nothing like this";
        Assert.True(page.HasNoShortcuts);
    }

    [Fact]
    public void SearchNamingASection_KeepsItsShortcuts()
    {
        var (page, _) = Make();

        page.SearchText = "linked";

        Assert.Equal(["Linked databases"], page.VisibleSections.Select(s => s.Title));
    }

    [Fact]
    public void DialogSearch_NarrowsThePageToo()
    {
        var (page, _) = Make();

        page.Filter("pagedown");

        Assert.Equal(["Ctrl+Alt+PageDown"], page.VisibleSections.SelectMany(s => s.Items).Select(i => i.Keys));
        Assert.True(page.MatchesQuery("pagedown"));
        Assert.False(page.MatchesQuery("zzz"));
        Assert.True(page.MatchesQuery("keyboard"));
    }

    [Fact]
    public async Task CopyAsMarkdown_PutsTheWholeListOnTheClipboard_WhateverTheSearch()
    {
        var (page, copied) = Make();
        page.SearchText = "ctrl+t";

        await page.CopyMarkdownCommand.ExecuteAsync(null);

        var text = copied()!;
        Assert.StartsWith("# dbExplorer keyboard shortcuts", text);
        Assert.Contains("## Script", text);
        Assert.Contains("| `Ctrl+Enter` | Run the statement |", text);
        foreach (var item in ShortcutCatalog.Sections.SelectMany(s => s.Items)) Assert.Contains($"`{item.Keys}`", text);
    }
}
