using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using Microsoft.Data.Sqlite;

namespace DbExplorer.App.Tests;

/// <summary>The "+ Row" button: who may insert, what the user sees when it is disabled, where editing starts, and the save notice.</summary>
public class AddRowTests : IDisposable
{
    private readonly TableTabFixture _t = new();
    public void Dispose() => _t.Dispose();

    private void Exec(string sql)
    {
        using var c = new SqliteConnection($"Data Source={_t.DbFile};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

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

    private static Button Button(Control view, string content) =>
        view.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == content);

    private static void Click(Window window, Control control)
    {
        var p = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
    }

    // ---- H1: who may insert ----

    [Fact]
    public async Task KeylessTable_AcceptsInserts_ButLoadedRowsStayReadOnly()
    {
        Exec("INSERT INTO nopk VALUES (1, 'a'), (2, 'b')");
        var tab = await _t.OpenTab("nopk");
        Assert.False(tab.CanEdit);   // UPDATE / DELETE need a key
        Assert.True(tab.CanInsert);  // INSERT does not

        Assert.False(tab.SetCell(0, 1, "changed"));
        tab.DeleteRow(0);
        Assert.False(tab.Rows[0].IsDeleted);
        Assert.False(tab.HasPendingEdits);

        var added = tab.AddRow();
        Assert.Equal(2, added);
        Assert.True(tab.SetCell(added, tab.FindColumn("y")!.Value, "new"));
        Assert.True(tab.SetCell(added, tab.FindColumn("x")!.Value, "7"));
        Assert.Single(tab.BuildPreview());

        await tab.SaveAsync();

        Assert.Null(tab.ErrorMessage);
        Assert.False(tab.HasPendingEdits);
        Assert.Equal(1, _t.Scalar("SELECT COUNT(*) FROM nopk WHERE x = 7 AND y = 'new'"));
        Assert.Equal(3, _t.Scalar("SELECT COUNT(*) FROM nopk"));
        Assert.Equal(3, tab.Rows.Count);
    }

    [Fact]
    public async Task KeylessTable_AddedRowCanBeDropped_AndStillRefusesLoadedRowChanges()
    {
        Exec("INSERT INTO nopk VALUES (1, 'a')");
        var tab = await _t.OpenTab("nopk");
        var added = tab.AddRow();
        tab.DeleteRow(added);   // a new row is simply dropped
        Assert.Single(tab.Rows);
        Assert.False(tab.HasPendingEdits);
        tab.DeleteRow(0);       // a loaded row is not: no key to find it again
        Assert.False(tab.Rows[0].IsDeleted);
        Assert.Contains("primary key", tab.CellError);
    }

    [Fact]
    public async Task Views_AndReadOnlyConnections_StillRefuseInserts_AndSayWhy()
    {
        var view = await _t.OpenTab("v_big");
        Assert.False(view.CanInsert);
        Assert.Equal(-1, view.AddRow());
        Assert.Contains("view", view.AddRowTip);

        using var ro = new TableTabFixture(readOnly: true);
        var tab = await ro.OpenTab("customers");
        Assert.False(tab.CanInsert);
        Assert.Equal(-1, tab.AddRow());
        Assert.Contains("read-only", tab.AddRowTip);
    }

    [Fact]
    public async Task ToolbarTips_ExplainWhyAButtonIsDisabled()
    {
        var keyed = await _t.OpenTab("customers");
        Assert.Equal("Add an empty row (inserted on save)", keyed.AddRowTip);
        Assert.Equal("Mark the selected rows for deletion", keyed.DeleteRowTip);

        var keyless = await _t.OpenTab("nopk");
        Assert.Equal("Add an empty row (inserted on save)", keyless.AddRowTip);
        Assert.Contains("Define key", keyless.DeleteRowTip);
        Assert.Contains("insert", keyless.EditBadge);   // the badge no longer says "read-only" for a table that accepts inserts
    }

    [Fact]
    public async Task DefiningAKey_UpdatesTheTips()
    {
        var tab = await _t.OpenTab("nopk");
        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        tab.SetVirtualKey(["x"]);
        Assert.Contains(nameof(TableTabViewModel.DeleteRowTip), changed);
        Assert.Equal("Mark the selected rows for deletion", tab.DeleteRowTip);
    }

    // ---- H5: the saved row is not necessarily on the page that comes back ----

    [Fact]
    public async Task SavingANewRow_OnAFullPage_SaysItMayBeOnAnotherPage()
    {
        var tab = await _t.OpenTab("big");
        var i = tab.AddRow();
        tab.SetCell(i, tab.FindColumn("label")!.Value, "added");
        await tab.SaveAsync();
        Assert.Contains("Saved 1 statement", tab.Notice);
        Assert.Contains("another page", tab.Notice);

        var small = await _t.OpenTab("customers");   // everything fits on the page: nothing to explain
        var j = small.AddRow();
        small.SetCell(j, small.FindColumn("name")!.Value, "Zed");
        await small.SaveAsync();
        Assert.Equal("Saved 1 statement.", small.Notice);
    }

    // ---- H3 / H1 in the real grid ----

    [AvaloniaFact]
    public async Task ClickingAddRow_StartsEditingTheFirstEditableCell_OfTheNewRow()
    {
        var tab = await _t.OpenTab("customers");   // id INTEGER PRIMARY KEY (generated), name NOT NULL
        var view = new TableTabView { DataContext = tab };
        var window = new Window { Content = view, Width = 1100, Height = 500 };
        window.Show();
        await Pump();

        Click(window, Button(view, "+ Row"));
        await Pump();

        var grid = InnerGrid(view);
        Assert.Equal(tab.Rows.Count - 1, grid.SelectedIndex);
        var editor = Assert.Single(grid.GetVisualDescendants().OfType<CellEditor>());
        Assert.True(editor.IsFocused, "the editor should have the focus: the user can type right away");
        Assert.Equal(tab.FindColumn("name")!.Value, ((GridCellColumn)grid.CurrentColumn!).Index);   // not the generated key

        editor.Text = "Eve";
        grid.CommitEdit();
        Assert.Equal("Eve", tab.Rows[^1].Values[tab.FindColumn("name")!.Value]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task KeylessTable_ButtonIsEnabled_NewRowIsEditable_LoadedRowsAreNot()
    {
        Exec("INSERT INTO nopk VALUES (1, 'a')");
        var tab = await _t.OpenTab("nopk");
        var view = new TableTabView { DataContext = tab };
        var window = new Window { Content = view, Width = 1100, Height = 500 };
        window.Show();
        await Pump();
        var add = Button(view, "+ Row");
        Assert.True(add.IsEnabled);
        Assert.False(Button(view, "Delete row").IsEnabled);
        Assert.True(ToolTip.GetShowOnDisabled(Button(view, "Delete row")));

        var grid = InnerGrid(view);
        grid.SelectedIndex = 0;
        grid.CurrentColumn = grid.Columns[1];
        Assert.False(grid.BeginEdit());   // a loaded row of a keyless table
        Assert.Empty(grid.GetVisualDescendants().OfType<CellEditor>());

        Click(window, add);
        await Pump();

        Assert.Single(grid.GetVisualDescendants().OfType<CellEditor>());   // editing starts on the new row (first column: x is not a key)
        Assert.Equal(1, tab.Rows.Count(r => r.IsNew));
        window.Close();
    }

    [AvaloniaFact]
    public async Task DisabledAddRowButton_OnAView_CarriesTheReasonAsATooltip()
    {
        var tab = await _t.OpenTab("v_big");
        var view = new TableTabView { DataContext = tab };
        var window = new Window { Content = view, Width = 1100, Height = 500 };
        window.Show();
        await Pump();
        var add = Button(view, "+ Row");
        Assert.False(add.IsEnabled);
        Assert.Contains("view", ToolTip.GetTip(add) as string);
        Assert.True(ToolTip.GetShowOnDisabled(add));
        window.Close();
    }
}
