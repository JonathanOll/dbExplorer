using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App.Tests;

/// <summary>Keyboard and pointer behaviour of the table grid: Delete / Insert, the filter boxes, the cell tooltips.</summary>
public class TableGridInteractionTests : IDisposable
{
    private readonly TableTabFixture _t = new();
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

    private static DataGrid InnerGrid(Control root) => root.GetVisualDescendants().OfType<DataGrid>().First(g => g.Columns.Count > 0);

    private async Task<(TableTabViewModel Tab, TableTabView View, Window Window, DataGrid Grid)> Show(string table)
    {
        var tab = await _t.OpenTab(table);
        var view = new TableTabView { DataContext = tab };
        var window = new Window { Content = view, Width = 1200, Height = 600 };
        window.Show();
        await Pump();
        return (tab, view, window, InnerGrid(view));
    }

    private static object? Tip(GridCell cell) => ToolTip.GetTip(cell);

    private static string TipText(GridCell cell) => Tip(cell) switch { string s => s, TextBlock t => t.Text ?? "", _ => "" };

    [AvaloniaFact]
    public async Task DeleteKey_MarksTheSelectedRowsForDeletion()
    {
        var (tab, _, window, grid) = await Show("customers");
        grid.SelectedItems.Add(tab.Rows[1]);
        grid.SelectedItems.Add(tab.Rows[2]);
        grid.Focus();

        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        await Pump();

        Assert.True(tab.Rows[1].IsDeleted);
        Assert.True(tab.Rows[2].IsDeleted);
        Assert.False(tab.Rows[0].IsDeleted);
        Assert.Equal(2, tab.PendingDeletedCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task InsertKey_AddsARow_AndStartsEditingIt()
    {
        var (tab, _, window, grid) = await Show("customers");
        grid.SelectedIndex = 0;
        grid.Focus();

        window.KeyPressQwerty(PhysicalKey.Insert, RawInputModifiers.None);
        await Pump();

        Assert.Equal(5, tab.Rows.Count);
        Assert.True(tab.Rows[4].IsNew);
        Assert.Equal(1, tab.PendingAddedCount);
        Assert.Single(grid.GetVisualDescendants().OfType<CellEditor>());
        window.Close();
    }

    [AvaloniaFact]
    public async Task DeleteKey_InAnEditor_StaysATextKey()
    {
        var (tab, _, window, grid) = await Show("customers");
        grid.SelectedIndex = 0;
        grid.CurrentColumn = grid.Columns[1];
        grid.Focus();
        grid.BeginEdit();
        await Pump();
        var editor = Assert.Single(grid.GetVisualDescendants().OfType<CellEditor>());
        editor.Focus();

        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        await Pump();

        Assert.False(tab.Rows[0].IsDeleted);   // the key went to the text box
        window.Close();
    }

    [AvaloniaFact]
    public async Task DeleteButton_CountsTheSelectedRows_AndAsksForASelectionWhenThereIsNone()
    {
        var (tab, view, window, grid) = await Show("customers");
        var button = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "DeleteButton");
        Assert.Equal("Delete row", button.Content);

        grid.SelectedItems.Add(tab.Rows[0]);
        grid.SelectedItems.Add(tab.Rows[1]);
        grid.SelectedItems.Add(tab.Rows[2]);
        await Pump();
        Assert.Equal("Delete 3 rows", button.Content);
        Assert.Contains("3 rows selected", view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "SelectionText").Text);

        grid.SelectedItems.Clear();
        await Pump();
        Assert.Equal("Delete row", button.Content);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Select the rows to delete first.", tab.Notice);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FilterBox_ShowsApplied_AndNotAppliedState_AndItsClearButtonClearsAndApplies()
    {
        var (tab, view, window, _) = await Show("customers");
        var box = view.GetVisualDescendants().OfType<TextBox>().First(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Filter name");
        Assert.Equal("contains…", box.Watermark);
        Assert.Equal(">10", view.GetVisualDescendants().OfType<TextBox>().First(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Filter id").Watermark);   // a number column hints at a comparison

        box.Text = "a";
        await Pump();
        Assert.Contains("dirty", box.Classes);   // typed, not applied
        Assert.DoesNotContain("active", box.Classes);

        await tab.ApplyFiltersAsync();
        await Pump();
        Assert.Contains("active", box.Classes);
        Assert.DoesNotContain("dirty", box.Classes);

        var clear = box.GetVisualDescendants().OfType<Button>().Single();
        Assert.True(clear.IsVisible);
        clear.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Pump(30);

        Assert.Equal("", box.Text);
        Assert.False(tab.HasActiveFilters);
        Assert.Equal(4, tab.Rows.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task EscapeInAFilterBox_ClearsIt()
    {
        var (tab, view, window, _) = await Show("customers");
        tab.SetFilter("name", "a");
        await tab.ApplyFiltersAsync();
        await Pump();
        var box = view.GetVisualDescendants().OfType<TextBox>().First(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Filter name");
        Assert.Equal("a", box.Text);

        box.Focus();
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await Pump(30);

        Assert.Equal("", box.Text);
        Assert.False(tab.HasActiveFilters);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CellTooltips_SayWhatIsDifferent()
    {
        var (tab, _, window, grid) = await Show("orders");
        var note = tab.FindColumn("note")!.Value;
        tab.SetCell(0, note, "changed");
        tab.AddRow();
        await Pump();

        var cells = grid.GetVisualDescendants().OfType<GridCell>().ToList();
        var edited = cells.Single(c => ReferenceEquals(c.Row, tab.Rows[0]) && c.Index == note);
        Assert.Equal("Was: first", TipText(edited));
        Assert.Equal(Avalonia.Media.FontWeight.SemiBold, edited.FontWeight);

        var fresh = cells.First(c => c.Row is RowViewModel { IsNew: true });
        Assert.Equal("DEFAULT", fresh.Text);
        Assert.Contains("column default", TipText(fresh));

        var link = cells.First(c => c.IsLink && ReferenceEquals(c.Row, tab.Rows[1]));
        Assert.Contains("Ctrl+click or double-click to open", TipText(link));
        Assert.DoesNotContain("↗", link.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task NumericColumns_AreRightAligned_UnderARightAlignedName_AndNullIsFaint()
    {
        var (tab, _, window, grid) = await Show("orders");
        var cells = grid.GetVisualDescendants().OfType<GridCell>().ToList();
        var total = tab.FindColumn("total")!.Value;
        Assert.All(cells.Where(c => c.Index == total && c.Row is RowViewModel), c => Assert.Equal(Avalonia.Media.TextAlignment.Right, c.TextAlignment));

        var payload = tab.FindColumn("payload")!.Value;
        var nullCell = cells.First(c => c.Index == payload && c.Text == "NULL");
        Assert.Equal(Avalonia.Media.FontStyle.Italic, nullCell.FontStyle);
        Assert.Equal(1.0, nullCell.Opacity);   // faint through its colour, not through a transparency that fails contrast
        window.Close();
    }
    [AvaloniaFact]
    public async Task EditedNumber_StaysRightAligned()
    {
        var (tab, _, window, grid) = await Show("orders");
        var total = tab.FindColumn("total")!.Value;
        tab.SetCell(0, total, "26.1");   // staged as text until saved
        await Pump();

        var edited = grid.GetVisualDescendants().OfType<GridCell>().Single(c => ReferenceEquals(c.Row, tab.Rows[0]) && c.Index == total);
        Assert.Equal(Avalonia.Media.TextAlignment.Right, edited.TextAlignment);
        window.Close();
    }

    [AvaloniaFact]
    public async Task UntypedColumnOfNumbers_IsRightAligned_NameIncluded()
    {
        // A script result carries no column types: the first rows decide.
        var rows = new List<object?[]> { new object?[] { 1L, "a" }, new object?[] { null, "b" }, new object?[] { 2.5, "c" } };
        var grid = new ResultGrid { Columns = [new ResultColumn("credit"), new ResultColumn("name")], ItemsSource = rows };
        var window = new Window { Content = grid, Width = 600, Height = 300 };
        window.Show();
        await Pump();

        var inner = grid.GetVisualDescendants().OfType<DataGrid>().First();
        Assert.True(((GridCellColumn)inner.Columns[0]).IsNumber);
        Assert.False(((GridCellColumn)inner.Columns[1]).IsNumber);
        var title = grid.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "credit" && t.Classes.Contains("colTitle"));
        Assert.Equal(6, ((Grid)title.Parent!).ColumnDefinitions.Count);   // the right-aligned header layout
        window.Close();
    }
}
