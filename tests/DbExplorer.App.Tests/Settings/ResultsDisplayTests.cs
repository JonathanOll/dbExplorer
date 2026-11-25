using System.Globalization;
using DbExplorer.App.Settings;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests.Settings;

/// <summary>How the Results settings change what a grid cell shows, and what they must leave alone (filters, copy, edit text).</summary>
public class ResultsDisplayTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public ResultsDisplayTests() => CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

    public void Dispose() => CultureInfo.CurrentCulture = _culture;

    private static ResultsSettings R(Func<ResultsSettings, ResultsSettings>? with = null) => with?.Invoke(new ResultsSettings()) ?? new ResultsSettings();

    [Theory]
    [InlineData(NullDisplay.Null, "NULL")]
    [InlineData(NullDisplay.Parenthesized, "(null)")]
    [InlineData(NullDisplay.Empty, "")]
    public void NullText_FollowsTheSetting(NullDisplay mode, string text)
    {
        var cell = CellFormatter.FormatForDisplay(null, R(r => r with { NullText = mode }));

        Assert.Equal(text, cell.Text);
        Assert.True(cell.IsNull);
    }

    [Fact]
    public void Defaults_MatchThePlainFormatter()
    {
        foreach (var value in new object?[] { null, "text", 42, 3.5, true, new DateTime(2026, 3, 14, 9, 30, 0), new byte[] { 1, 2 }, 12345678L })
            Assert.Equal(CellFormatter.Format(value), CellFormatter.FormatForDisplay(value, R()));
    }

    [Theory]
    [InlineData(1234567L, "1,234,567")]
    [InlineData(-1234567L, "-1,234,567")]
    [InlineData(123L, "123")]
    [InlineData(1000L, "1,000")]
    public void ThousandsSeparator_GroupsIntegers_WhateverTheCulture(long value, string text) =>
        Assert.Equal(text, CellFormatter.FormatForDisplay(value, R(r => r with { ThousandsSeparator = true })).Text);

    [Fact]
    public void ThousandsSeparator_KeepsTheFraction_AndLeavesTextAndExponentsAlone()
    {
        var on = R(r => r with { ThousandsSeparator = true });

        Assert.Equal("1,234,567.891", CellFormatter.FormatForDisplay(1234567.891m, on).Text);
        Assert.Equal("1234567", CellFormatter.FormatForDisplay("1234567", on).Text);
        Assert.Equal("1E+20", CellFormatter.GroupThousands("1E+20"));
        Assert.Equal("true", CellFormatter.FormatForDisplay(true, on).Text);
        Assert.True(CellFormatter.FormatForDisplay(1234567L, on).IsNumber);
    }

    [Fact]
    public void DateFormat_Locale_UsesTheCurrentCulture_IsoStaysInvariant()
    {
        var date = new DateTime(2026, 3, 14, 9, 30, 5);

        Assert.Equal("2026-03-14 09:30:05", CellFormatter.FormatForDisplay(date, R()).Text);
        Assert.Equal(date.ToString("G", new CultureInfo("fr-FR")), CellFormatter.FormatForDisplay(date, R(r => r with { DateFormat = DateDisplay.Locale })).Text);
        Assert.Equal("14/03/2026", CellFormatter.FormatForDisplay(new DateOnly(2026, 3, 14), R(r => r with { DateFormat = DateDisplay.Locale })).Text);
    }

    [Fact]
    public void LongText_IsCutAtTheConfiguredLength_WithTheFullValueInTheTooltip()
    {
        var text = new string('x', 100);

        var cell = CellFormatter.FormatForDisplay(text, R(r => r with { MaxCellLength = 30 }));

        Assert.Equal(new string('x', 30) + "…", cell.Text);
        Assert.Equal(text, cell.Tooltip);
        Assert.Equal(text, CellFormatter.FormatForDisplay(text, R()).Text);   // 200 by default
    }

    [Fact]
    public void FiltersAndPlainFormat_IgnoreTheDisplaySettings()
    {
        var previous = SettingsService.Instance;
        try
        {
            SettingsService.Instance = new SettingsService(new AppSettings { Results = new() { ThousandsSeparator = true, NullText = NullDisplay.Empty } });

            Assert.Equal("=1234567", CellFormatter.FilterText(1234567L));
            Assert.Equal("1234567", CellFormatter.Format(1234567L).Text);
            Assert.Equal("NULL", CellFormatter.Format(null).Text);
            Assert.Equal("1,234,567", CellFormatter.FormatForDisplay(1234567L).Text);   // the one-argument form reads the live settings
        }
        finally
        {
            SettingsService.Instance = previous;
        }
    }

    [Fact]
    public void NewScriptTab_StartsWithTheConfiguredRowLimit()
    {
        var previous = SettingsService.Instance;
        try
        {
            SettingsService.Instance = new SettingsService(new AppSettings { Results = new() { DefaultRowLimit = 250 } });
            using var fixture = new ScriptTabFixture();
            var tab = new ScriptTabViewModel(fixture.Config, null, fixture.Hooks, 1);

            Assert.Equal(250, tab.RowLimit);
            Assert.True(tab.IsDefaultRowLimit);
            tab.RowLimit = 1000;
            Assert.False(tab.IsDefaultRowLimit);
        }
        finally
        {
            SettingsService.Instance = previous;
        }
    }
}
