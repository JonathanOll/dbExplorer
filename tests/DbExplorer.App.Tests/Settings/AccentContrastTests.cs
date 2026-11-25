using Avalonia.Media;
using DbExplorer.App.Appearance;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests.Settings;

/// <summary>Accessibility of the accent presets: text on a primary button, the accent as text, and the focus ring must stay readable.</summary>
public class AccentContrastTests
{
    public static IEnumerable<object[]> Cases() =>
        from preset in Enum.GetValues<AccentPreset>()
        from dark in new[] { false, true }
        select new object[] { preset, dark };

    private static Color C(string hex) => Color.Parse(hex);

    [Theory]
    [MemberData(nameof(Cases))]
    public void OnAccent_OnTheButtonFill_IsAtLeast4_5(AccentPreset preset, bool dark)
    {
        var colors = AccentPalette.For(preset, dark);

        Assert.True(AccentPalette.Contrast(C(colors.OnAccent), C(colors.ButtonBg)) >= 4.5, $"{preset} {(dark ? "dark" : "light")} button");
        Assert.True(AccentPalette.Contrast(C(colors.OnAccent), C(colors.ButtonHover)) >= 4.5, $"{preset} {(dark ? "dark" : "light")} hover");
        Assert.True(AccentPalette.Contrast(C(colors.OnAccent), C(colors.Strong)) >= 4.5 || dark, $"{preset} pressed fill (light)");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AccentAndLink_AreReadableOnTheContentBackground(AccentPreset preset, bool dark)
    {
        var colors = AccentPalette.For(preset, dark);
        var background = C(dark ? "#1F2228" : "#FFFFFF");

        Assert.True(AccentPalette.Contrast(C(colors.Accent), background) >= 3.0, $"{preset} accent icon contrast");
        Assert.True(AccentPalette.Contrast(C(colors.Link), background) >= 4.5, $"{preset} link text contrast");
        Assert.True(AccentPalette.Contrast(C(colors.Ring), background) >= 3.0, $"{preset} focus ring contrast");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SelectedChipText_IsReadableOnTheSoftFill(AccentPreset preset, bool dark)
    {
        var colors = AccentPalette.For(preset, dark);

        Assert.True(AccentPalette.Contrast(C(colors.Strong), C(colors.Soft)) >= 4.5, $"{preset} strong on soft");
        Assert.True(AccentPalette.Contrast(C(colors.Strong), C(colors.SoftHover)) >= 4.5, $"{preset} strong on soft hover");
    }

    [Fact]
    public void Contrast_KnownValues()
    {
        Assert.Equal(21, AccentPalette.Contrast(Colors.Black, Colors.White), 3);
        Assert.Equal(1, AccentPalette.Contrast(Colors.White, Colors.White), 3);
    }

    [Fact]
    public void Blue_IsTheOriginalThemePalette()
    {
        var light = AccentPalette.For(AccentPreset.Blue, dark: false);
        var dark = AccentPalette.For(AccentPreset.Blue, dark: true);

        Assert.Equal(("#3E63DD", "#FFFFFF"), (light.ButtonBg, light.OnAccent));
        Assert.Equal(("#4565E0", "#7C97F5"), (dark.ButtonBg, dark.Accent));
    }

    [Fact]
    public void EveryPresetHasADistinctSwatch() =>
        Assert.Equal(6, AccentPalette.Presets.Select(AccentPalette.Swatch).Distinct().Count());
}
