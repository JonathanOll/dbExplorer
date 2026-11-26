using DbExplorer.App.ViewModels;
using DbExplorer.Core.Export;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests;

internal sealed class FakeExportDialogs : IExportDialogs
{
    public string? Path { get; set; }
    public (string Name, string Extension)? Asked { get; private set; }
    public AdvancedCopyViewModel? Advanced { get; private set; }
    public ExportDialogViewModel? Export { get; private set; }

    public Task<string?> ShowAdvancedCopyAsync(AdvancedCopyViewModel viewModel)
    {
        Advanced = viewModel;
        return Task.FromResult(viewModel.Copy());
    }

    public Task ShowExportAsync(ExportDialogViewModel viewModel)
    {
        Export = viewModel;
        return Task.CompletedTask;
    }

    public Task<string?> PickFileAsync(string suggestedName, string extension)
    {
        Asked = (suggestedName, extension);
        return Task.FromResult(Path);
    }
}

/// <summary>Copy and export through the view models: selection slices, the copy texts, the dialogs and the script tab's rows.</summary>
public class ResultExportTests : IDisposable
{
    private readonly ScriptTabFixture _t = new();
    private readonly FakeExportDialogs _dialogs = new();

    public void Dispose() => _t.Dispose();

    private ExportServices Services => new(_t.Status, _dialogs);

    private static ExportContext Context(GridSlice? selection = null) => new()
    {
        Columns = [new("id"), new("name"), new("note")],
        Rows = [[1L, "a", null], [2L, "b\tc", "x"], [3L, "d", "y"]],
        Selection = selection,
        Table = new TableRef(null, "t"),
        KeyColumns = ["id"],
    };

    [Fact]
    public void Selection_BlockAndColumns_BecomeSlices()
    {
        var selection = new GridSelection();
        Assert.Null(selection.Slice(3, 3));

        selection.SetBlock((2, 2), (1, 0));
        Assert.Equal([1, 2], selection.Slice(3, 3)!.Rows);
        Assert.Equal([0, 1, 2], selection.Slice(3, 3)!.Columns);

        selection.ToggleColumn(2);
        selection.ToggleColumn(0);
        Assert.Equal([0, 1, 2], selection.Slice(3, 3)!.Rows);
        Assert.Equal([0, 2], selection.Slice(3, 3)!.Columns);

        selection.ToggleColumn(2);
        selection.ToggleColumn(0);
        Assert.False(selection.IsActive);
    }

    [Fact]
    public void PlainCopy_OfOneCell_IsTheValue_AndOtherwiseTabSeparated()
    {
        Assert.Equal("b\tc", GridCopy.Plain(Context(new([1], [1])), header: false).Text);
        Assert.Equal("", GridCopy.Plain(Context(new([0], [2])), header: false).Text);
        Assert.Equal("name\r\n\"b\tc\"", GridCopy.Plain(Context(new([1], [1])), header: true).Text);

        var all = GridCopy.Plain(Context(), header: false);
        Assert.Equal("1\ta\t\r\n2\t\"b\tc\"\tx\r\n3\td\ty", all.Text);
        Assert.Equal("Copied 3 rows.", all.Message);
    }

    [Fact]
    public void CopyAs_Update_AddsTheKeyColumn_AndSqlWithoutATableSaysSo()
    {
        var update = GridCopy.As(Context(new([1], [2])), ExportKind.SqlUpdate);
        Assert.Equal("UPDATE \"t\" SET \"note\" = 'x' WHERE \"id\" = 2;", update.Text);
        Assert.Equal("IN (1, 3)", GridCopy.As(Context(new([0, 2], [0])), ExportKind.InList).Text);

        var unnamed = new ExportContext { Columns = [new("a")], Rows = [[1]] };
        Assert.Contains("table_name", GridCopy.As(unnamed, ExportKind.SqlInsert).Message);
    }

    [Fact]
    public void AdvancedCopy_PreviewsAsOptionsChange_RemembersThem_AndRefusesBadInput()
    {
        var services = Services;
        var vm = new AdvancedCopyViewModel(Context(new([0, 1], [0, 1])), services);
        Assert.Equal("2 rows × 2 columns", vm.Subtitle);

        vm.Delimiter = vm.Delimiters.Single(d => d.Value == ";");
        vm.Header = false;
        vm.RowNumbers = true;
        Assert.Equal("1;1;a\r\n2;2;b\tc", vm.Preview);

        vm.Delimiter = vm.Delimiters[^1];
        Assert.True(vm.IsCustomDelimiter);
        Assert.False(vm.CanCopy);
        Assert.Null(vm.Copy());
        vm.CustomDelimiter = " ~ ";
        Assert.StartsWith("1 ~ 1 ~ a", vm.Copy());

        var again = new AdvancedCopyViewModel(Context(), services);
        Assert.Equal(" ~ ", again.CustomDelimiter);
        Assert.True(again.RowNumbers);
        Assert.False(again.Header);
    }

    [Fact]
    public async Task ExportDialog_DefaultsTheScope_WritesTheSelection_AndRemembersTheFormat()
    {
        var services = Services;
        Assert.Equal(ExportScope.Loaded, new ExportDialogViewModel(Context(new([0], [0, 1, 2])), services).Scope);
        var vm = new ExportDialogViewModel(Context(new([0, 2], [0, 1, 2])), services);
        Assert.Equal(ExportScope.Selection, vm.Scope);
        Assert.False(vm.CanAll);
        vm.Format = vm.Formats.Single(f => f.Value == ExportKind.SqlInsert);
        Assert.True(vm.ShowTableName);
        Assert.StartsWith("INSERT INTO \"t\"", vm.Preview);
        var closed = false;
        vm.CloseRequested += () => closed = true;
        _dialogs.Path = _t.TempFile("out.sql");

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(("result.sql", "sql"), _dialogs.Asked);
        Assert.Equal(2, File.ReadAllLines(_dialogs.Path).Length);
        Assert.True(closed);
        Assert.Contains("Exported 2 rows", _t.Status.Message);
        Assert.Equal(ExportKind.SqlInsert, services.Preferences.Format);
    }

    [Fact]
    public async Task ExportDialog_PickerCancelled_WritesNothing_AndAFailureStaysOnScreen()
    {
        var vm = new ExportDialogViewModel(Context(), Services);
        _dialogs.Path = null;
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.False(vm.HasError);
        Assert.False(vm.IsExporting);

        _dialogs.Path = _t.TempFile("no-such-folder/x.csv");
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Contains("Cannot export", vm.Error);
        Assert.True(_t.Status.IsError);
    }

    [Fact]
    public async Task ScriptResult_ExportsAllRowsByRunningTheReadQueryAgain()
    {
        var tab = await _t.NewTab("SELECT id FROM big");
        tab.RowLimit = 5;
        await tab.RunScriptAsync();
        var set = tab.Results[0].Sets[0];
        var context = tab.CreateExportContext(set.Columns, set.Rows, null);
        Assert.Equal(5, context.Rows.Count);
        var vm = new ExportDialogViewModel(context, Services);
        Assert.True(vm.CanAll);
        vm.Scope = ExportScope.All;
        Assert.Contains("read-only", vm.ScopeNote);
        _dialogs.Path = _t.TempFile("all.csv");

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Null(vm.Error);
        Assert.Equal(51, File.ReadAllLines(_dialogs.Path).Length);
        Assert.Equal("Script 1 result", context.BaseName);
    }

    [Fact]
    public async Task ScriptResult_OfAWrite_DoesNotOfferAllRows()
    {
        var tab = await _t.NewTab("UPDATE t SET name = 'z' WHERE id = 1 RETURNING id");
        await tab.RunScriptAsync();
        var set = tab.Results[0].Sets[0];

        var context = tab.CreateExportContext(set.Columns, set.Rows, null);

        Assert.Null(context.OpenAllRows);
        Assert.Contains("may write", context.AllRowsUnavailable);
    }
}
