using Avalonia.Controls;

namespace UiShots;

/// <summary>The command palette: as opened (recent commands, context), searched with a typo, and on a second step.</summary>
internal static partial class Scenes
{
    private static async Task<Window?> CommandPaletteScene(Demo d, string query, bool step = false)
    {
        await Open(d);
        await OpenTable(d, "customers");
        d.Main.Tree.AddRecentCommand("appearance.theme");
        d.Main.Tree.AddRecentCommand("go.table");
        d.Main.ShowCommandPalette();
        var palette = d.Main.CommandPalette!;
        await Settle();
        palette.Query = query;
        if (step) await palette.ConfirmAsync();
        await Settle();
        return null;
    }
}
