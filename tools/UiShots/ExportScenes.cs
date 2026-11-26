using Avalonia.Controls;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Export;

namespace UiShots;

internal sealed class NoExportDialogs : IExportDialogs
{
    public Task<string?> ShowAdvancedCopyAsync(AdvancedCopyViewModel viewModel) => Task.FromResult<string?>(null);
    public Task ShowExportAsync(ExportDialogViewModel viewModel) => Task.CompletedTask;
    public Task<string?> PickFileAsync(string suggestedName, string extension) => Task.FromResult<string?>(null);
}

internal static partial class Scenes
{
    private static async Task<(TableTabViewModel Tab, ResultGrid Grid)> SelectedBlock(Demo d)
    {
        await Open(d);
        d.Main.UseExport(new NoExportDialogs());
        var tab = await OpenTable(d, "orders");
        var grid = d.Window.GetVisualDescendants().OfType<ResultGrid>().Single();
        grid.SelectBlock((1, 1), (6, 3));
        await Settle();
        return (tab, grid);
    }

    // A rectangle of cells selected in a table grid.
    private static async Task<Window?> GridSelectionScene(Demo d)
    {
        await SelectedBlock(d);
        return null;
    }

    // The Export dialog over a table: SQL of the selection or CSV with all rows on offer.
    private static async Task<Window?> ExportDialogScene(Demo d, ExportKind format)
    {
        var (tab, grid) = await SelectedBlock(d);
        var rows = tab.Rows.Select(r => r.Values).ToList();
        var vm = new ExportDialogViewModel(tab.CreateExportContext(tab.GridColumns, rows, grid.CurrentSlice()), tab.ExportServices!);
        vm.Format = vm.Formats.First(f => f.Value == format);
        if (format == ExportKind.Csv) vm.Scope = ExportScope.All;
        var dialog = new ExportDialog { DataContext = vm };
        dialog.Show(d.Window);
        await Settle();
        return dialog;
    }

    private static async Task<Window?> AdvancedCopyScene(Demo d)
    {
        var (tab, grid) = await SelectedBlock(d);
        var rows = tab.Rows.Select(r => r.Values).ToList();
        var vm = new AdvancedCopyViewModel(tab.CreateExportContext(tab.GridColumns, rows, grid.CurrentSlice()), tab.ExportServices!)
        {
            RowNumbers = true,
        };
        var dialog = new AdvancedCopyDialog { DataContext = vm };
        dialog.Show(d.Window);
        await Settle();
        return dialog;
    }
}
