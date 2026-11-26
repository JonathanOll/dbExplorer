using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Export;

namespace DbExplorer.App.Views;

// Copy, "Copy as", "Advanced copy" and "Export": the text and the files are made by view models and Core; the grid says what
// is selected and writes the clipboard.
public partial class ResultGrid : IGridCommands
{
    bool IGridCommands.HasCurrentCellOrSelection => CopySlice(withHeader: false) is not null;

    bool IGridCommands.HasExportDialogs => ExportHost?.ExportServices?.Dialogs is not null;

    bool IGridCommands.HasKeyColumns => ContextFor(null).KeyColumns.Count > 0;

    Task IGridCommands.CopyAsync(bool header) => header ? CopyWithHeadersAsync() : CopySelectionAsync(withHeader: false);

    // With nothing selected the format takes every loaded row, as "Export" does.
    Task IGridCommands.CopyAsAsync(ExportKind kind) =>
        CopyAsAsync(kind, CurrentSlice() ?? AllCells(), (Grid.CurrentColumn as GridCellColumn)?.Index);

    Task IGridCommands.AdvancedCopyAsync() => ShowAdvancedCopyAsync();

    Task IGridCommands.ExportAsync() => ShowExportAsync();

    private GridSlice AllCells() =>
        new(Enumerable.Range(0, ShownRows.Count).ToList(), Enumerable.Range(0, Columns?.Count ?? 0).ToList());

    /// <summary>The tab this grid belongs to, which knows where the rows come from; null for a free-standing grid.</summary>
    private IGridExportHost? ExportHost =>
        this.GetVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<IGridExportHost>().FirstOrDefault();

    private ExportContext ContextFor(GridSlice? slice)
    {
        var columns = Columns ?? [];
        var rows = ShownRows.Select(r => GridCellColumn.ValuesOf(r) ?? []).ToList();
        return ExportHost?.CreateExportContext(columns, rows, slice) ?? new ExportContext
        {
            Columns = columns.Select(c => new ExportColumn(c.Name, c.TypeName, c.ClrType)).ToList(),
            Rows = rows,
            Selection = slice,
        };
    }

    // What Ctrl+C takes: the cell range or columns, several rows (or any with the header), else the current cell.
    private GridSlice? CopySlice(bool withHeader)
    {
        if (_selection.IsActive || Grid.SelectedItems.Count > 1 || withHeader) return CurrentSlice();
        return CurrentPosition() is { } cell ? new GridSlice([cell.Row], [cell.Column]) : CurrentSlice();
    }

    // What the context menu is about: the selection when the clicked cell is in it, else that cell.
    private GridSlice? MenuSlice()
    {
        if (PositionOf(_contextCell) is not { } cell) return CurrentSlice();
        if (IsPicked(_contextCell!.Row, cell.Column)) return CurrentSlice();
        if (!_selection.IsActive && Grid.SelectedItems.Contains(_contextCell.Row) && CurrentSlice() is { } rows) return rows;
        return new GridSlice([cell.Row], [cell.Column]);
    }

    /// <summary>The "Copy" button of a result: the selection with the column names, or every row when nothing is selected.</summary>
    public Task CopyWithHeadersAsync() => PutAsync(() => GridCopy.Plain(ContextFor(CurrentSlice()), header: true));

    private Task CopySelectionAsync(bool withHeader)
    {
        var slice = CopySlice(withHeader);
        return slice is null ? Task.CompletedTask : PutAsync(() => GridCopy.Plain(ContextFor(slice), withHeader));
    }

    private Task CopyAsAsync(ExportKind kind, GridSlice? slice, int? column)
    {
        if (slice is null) return Task.CompletedTask;
        // An IN list is about one column: the clicked or current one when whole rows are selected.
        if (kind == ExportKind.InList && column is { } c && slice.Columns.Contains(c)) slice = slice with { Columns = [c] };
        return PutAsync(() => GridCopy.As(ContextFor(slice), kind));
    }

    private async Task PutAsync(Func<CopyResult> make)
    {
        var status = ExportHost?.ExportServices?.Status;
        try
        {
            var result = make();
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            await clipboard.SetTextAsync(result.Text);
            status?.Info(result.Message);
        }
        catch (ExportException e)
        {
            status?.Error("Cannot copy: " + e.Message);
        }
    }

    /// <summary>Opens "Advanced copy" on the selection (all rows when there is none) and copies what it returns.</summary>
    public async Task ShowAdvancedCopyAsync(GridSlice? slice = null)
    {
        if (ExportHost?.ExportServices is not { Dialogs: { } dialogs } services) return;
        var text = await dialogs.ShowAdvancedCopyAsync(new AdvancedCopyViewModel(ContextFor(slice ?? CurrentSlice()), services));
        if (text is not null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    /// <summary>Opens the "Export" dialog.</summary>
    public async Task ShowExportAsync(GridSlice? slice = null)
    {
        if (ExportHost?.ExportServices is not { Dialogs: { } dialogs } services) return;
        await dialogs.ShowExportAsync(new ExportDialogViewModel(ContextFor(slice ?? CurrentSlice()), services));
    }

    private bool HandleCopyKey(KeyEventArgs e)
    {
        switch (e.Key, e.KeyModifiers)
        {
            case (Key.C, KeyModifiers.Control):
                _ = RunSafe(() => CopySelectionAsync(withHeader: false));
                return true;
            case (Key.C, KeyModifiers.Control | KeyModifiers.Shift):
                _ = RunSafe(() => CopySelectionAsync(withHeader: true));
                return true;
            case (Key.C, KeyModifiers.Control | KeyModifiers.Alt):
                _ = RunSafe(() => ShowAdvancedCopyAsync());
                return true;
            case (Key.E, KeyModifiers.Control):
                _ = RunSafe(() => ShowExportAsync());
                return true;
            default:
                return false;
        }
    }

    /// <summary>The copy and export entries of the cell menu, and what to refresh each time it opens.</summary>
    private (IReadOnlyList<Control> Items, Action Opening) BuildCopyMenu()
    {
        MenuItem Item(string header, Func<Task> action, KeyGesture? gesture = null)
        {
            var item = new MenuItem { Header = header, InputGesture = gesture };
            item.Click += (_, _) => _ = RunSafe(action);
            return item;
        }

        var copy = Item("Copy", () =>
        {
            // One selected row is just the row the right click landed in: "Copy" then means the clicked cell.
            var slice = MenuSlice();
            if (slice is not null && !_selection.IsActive && slice.Rows.Count == 1 && PositionOf(_contextCell) is { } cell)
                slice = new GridSlice([cell.Row], [cell.Column]);
            return slice is null ? Task.CompletedTask : PutAsync(() => GridCopy.Plain(ContextFor(slice), header: false));
        }, new KeyGesture(Key.C, KeyModifiers.Control));
        var withHeaders = Item("Copy with headers", () => MenuSlice() is { } s ? PutAsync(() => GridCopy.Plain(ContextFor(s), header: true)) : Task.CompletedTask,
            new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift));
        var advanced = Item("Advanced copy…", () => ShowAdvancedCopyAsync(MenuSlice()), new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Alt));
        var copyAs = new MenuItem { Header = "Copy as" };
        var update = (MenuItem?)null;
        foreach (var kind in ResultFormats.CopyAs)
        {
            var name = GridCopy.Name(kind);
            var item = Item(char.ToUpperInvariant(name[0]) + name[1..], () => CopyAsAsync(kind, MenuSlice(), _contextCell?.Index));
            if (kind == ExportKind.SqlUpdate) update = item;
            copyAs.Items.Add(item);
        }
        var export = Item("Export…", () => ShowExportAsync(MenuSlice()), new KeyGesture(Key.E, KeyModifiers.Control));
        var column = Item("Select column", () =>
        {
            if (_contextCell is { } c) ToggleColumnSelection(c.Index);
            return Task.CompletedTask;
        }, new KeyGesture(Key.Space, KeyModifiers.Control));

        return ([copy, withHeaders, advanced, copyAs, export, column], () =>
        {
            var services = ExportHost?.ExportServices;
            foreach (var item in new[] { advanced, export })
            {
                item.IsEnabled = services?.Dialogs is not null;
                ToolTip.SetShowOnDisabled(item, true);
                ToolTip.SetTip(item, item.IsEnabled ? null : "Not available for this grid");
            }
            if (update is null) return;
            var keyed = ContextFor(null).KeyColumns.Count > 0;
            update.IsEnabled = keyed;
            ToolTip.SetShowOnDisabled(update, true);
            ToolTip.SetTip(update, keyed ? null : ResultFormats.NoKeyReason);
            column.Header = _selection.HasColumns && _contextCell is { } c && _selection.ContainsColumn(c.Index) ? "Unselect column" : "Select column";
        });
    }
}
