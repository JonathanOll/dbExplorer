using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App.Tests;

/// <summary>Selection, copy keys and the export entry points of the grid, on a real table tab.</summary>
public class GridExportViewTests : IDisposable
{
    private readonly TableTabFixture _t = new();
    private readonly FakeExportDialogs _dialogs = new();

    public void Dispose() => _t.Dispose();

    private static async Task Pump(int rounds = 20)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    private async Task<(TableTabViewModel Tab, ResultGrid Grid, Window Window)> Show()
    {
        var tab = await _t.OpenTab("customers");
        tab.ExportServices = new ExportServices(new StatusModel(), _dialogs);
        var view = new TableTabView { DataContext = tab };
        var window = new Window { Content = view, Width = 1200, Height = 600 };
        window.Show();
        await Pump();
        return (tab, view.GetVisualDescendants().OfType<ResultGrid>().Single(), window);
    }

    private static int Lit(ResultGrid grid) => grid.GetVisualDescendants().OfType<DataGridCell>().Count(c => c.Classes.Contains("picked"));

    [AvaloniaFact]
    public async Task CtrlSpace_SelectsTheColumn_AndCtrlCCopiesIt()
    {
        var (tab, grid, window) = await Show();
        grid.Grid.SelectedIndex = 0;
        grid.Grid.CurrentColumn = grid.Grid.Columns[1];
        grid.Grid.Focus();

        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
        await Pump();

        Assert.Equal([1], grid.CurrentSlice()!.Columns);
        Assert.Equal(tab.Rows.Count, grid.CurrentSlice()!.Rows.Count);
        Assert.Equal(tab.Rows.Count, Lit(grid));
        window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
        await Pump();
        Assert.Equal(string.Join("\r\n", tab.Rows.Select(r => (string?)r.Values[1])), await window.Clipboard!.GetTextAsync());

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await Pump();
        Assert.Equal(0, Lit(grid));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ShiftArrows_GrowABlock_AndSelectingRowsDropsIt()
    {
        var (tab, grid, window) = await Show();
        grid.Grid.SelectedIndex = 0;
        grid.Grid.CurrentColumn = grid.Grid.Columns[0];
        grid.Grid.Focus();

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Shift);
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Shift);
        await Pump();

        Assert.Equal([0, 1], grid.CurrentSlice()!.Rows);
        Assert.Equal([0, 1], grid.CurrentSlice()!.Columns);
        Assert.Equal(4, Lit(grid));
        Assert.Equal(2, grid.SelectedRowCount);

        grid.Grid.SelectedItems.Add(tab.Rows[2]);
        await Pump();
        Assert.False(grid.CellSelection.IsActive);
        Assert.Equal(0, Lit(grid));
        window.Close();
    }

    [AvaloniaFact]
    public async Task CtrlE_OpensExport_OnTheSelection_AndAllRowsFollowTheAppliedFilter()
    {
        var (tab, grid, window) = await Show();
        tab.SetFilter("id", ">1");
        await tab.ApplyFiltersAsync();
        await Pump();
        grid.Grid.SelectedItems.Add(tab.Rows[0]);
        grid.Grid.SelectedItems.Add(tab.Rows[1]);
        grid.Grid.Focus();

        window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.Control);
        await Pump();

        var vm = _dialogs.Export!;
        Assert.Equal(ExportScope.Selection, vm.Scope);
        Assert.True(vm.CanAll);
        vm.Scope = ExportScope.All;
        _dialogs.Path = Path.Combine(Path.GetTempPath(), $"dbx-{Guid.NewGuid():N}.csv");
        try
        {
            await vm.ExportCommand.ExecuteAsync(null);
            Assert.Equal(tab.Rows.Count + 1, File.ReadAllLines(_dialogs.Path).Length);
        }
        finally
        {
            File.Delete(_dialogs.Path);
        }
        window.Close();
    }

    private static Avalonia.Point Centre(Avalonia.Visual visual, Window window) =>
        Avalonia.VisualExtensions.TranslatePoint(visual, new Avalonia.Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), window)!.Value;

    private static GridCell Cell(ResultGrid grid, object row, int column) =>
        grid.GetVisualDescendants().OfType<GridCell>().First(c => ReferenceEquals(c.Row, row) && c.Index == column);

    [AvaloniaFact]
    public async Task DraggingAcrossCells_SelectsABlock()
    {
        var (tab, grid, window) = await Show();
        var from = Centre(Cell(grid, tab.Rows[0], 0), window);
        var to = Centre(Cell(grid, tab.Rows[2], 1), window);

        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
        await Pump();

        Assert.Equal([0, 1, 2], grid.CurrentSlice()!.Rows);
        Assert.Equal([0, 1], grid.CurrentSlice()!.Columns);
        Assert.Equal(6, Lit(grid));
        window.Close();
    }

    [AvaloniaFact]
    public async Task CtrlClickOnAHeader_SelectsTheColumn_WithoutSorting()
    {
        var (tab, grid, window) = await Show();
        var name = grid.GetVisualDescendants().OfType<Border>()
            .First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Sort by name");
        var at = Centre(name, window);

        window.MouseDown(at, MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(at, MouseButton.Left, RawInputModifiers.Control);
        await Pump();

        Assert.Equal([1], grid.CurrentSlice()!.Columns);
        Assert.Null(tab.Sort);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Delete_OnACellSelection_StagesTheTouchedRows()
    {
        var (tab, grid, window) = await Show();
        grid.Grid.Focus();
        grid.SelectBlock((1, 0), (2, 1));
        await Pump();

        Assert.Equal(2, grid.SelectedRows.Count);
        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        await Pump();

        Assert.Equal([false, true, true, false], tab.Rows.Select(r => r.IsDeleted));
        Assert.Equal("2 pending changes", tab.PendingText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CtrlZero_OnACellSelection_StagesNullInTheSelectedCells_AndSaysWhatWasRefused()
    {
        var (tab, grid, window) = await Show();
        grid.Grid.Focus();
        grid.SelectBlock((1, 1), (2, 2));   // name does not allow NULL, boss_id does
        await Pump();

        window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.Control);
        await Pump();

        Assert.Null(tab.Rows[1].Values[2]);
        Assert.Null(tab.Rows[2].Values[2]);
        Assert.NotNull(tab.Rows[0 + 1].Values[1]);
        Assert.NotNull(tab.Rows[3].Values[2]);
        Assert.Contains("'name' does not allow NULL", tab.CellError);
        Assert.Equal("2 pending changes", tab.PendingText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Delete_OnAColumnSelection_TouchesEveryLoadedRow_AndRevertUndoesIt()
    {
        var (tab, grid, window) = await Show();
        grid.Grid.Focus();
        grid.ToggleColumnSelection(1);
        await Pump();

        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        await Pump();

        Assert.All(tab.Rows, r => Assert.True(r.IsDeleted));
        tab.Revert();
        Assert.False(tab.HasPendingEdits);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ThePalette_ActsOnTheGridOfTheActiveTab()
    {
        var (tab, grid, window) = await Show();
        IGridCommands commands = grid;
        Assert.False(commands.HasCurrentCellOrSelection);
        Assert.True(commands.HasExportDialogs);
        Assert.True(commands.HasKeyColumns);

        await commands.CopyAsAsync(DbExplorer.Core.Export.ExportKind.ColumnNames);
        Assert.Contains("boss_id", await window.Clipboard!.GetTextAsync());
        await commands.ExportAsync();
        Assert.NotNull(_dialogs.Export);

        grid.SelectBlock((0, 1), (0, 1));
        Assert.True(commands.HasCurrentCellOrSelection);
        await commands.CopyAsync(header: false);
        Assert.Equal("Alice", await window.Clipboard!.GetTextAsync());
        window.Close();
    }

    [AvaloniaFact]
    public void TheDialogs_Load_AndBind()
    {
        var context = new ExportContext { Columns = [new("a")], Rows = [[1]] };
        var services = new ExportServices(new StatusModel(), _dialogs);
        var export = new ExportDialog { DataContext = new ExportDialogViewModel(context, services) };
        var copy = new AdvancedCopyDialog { DataContext = new AdvancedCopyViewModel(context, services) };
        export.Show();
        copy.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(export.GetVisualDescendants().OfType<TextBox>(), t => t.Text == "a\r\n1" || t.Text == "a\n1");
        Assert.Contains(copy.GetVisualDescendants().OfType<TextBox>(), t => t.Text?.StartsWith('a') == true);
        export.Close();
        copy.Close();
    }
}
