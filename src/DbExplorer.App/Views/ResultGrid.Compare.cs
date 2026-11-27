using Avalonia.Controls;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

// "Select for compare" and "Compare with selected": the grid says which rows it shows, the tab knows what they are.
public partial class ResultGrid
{
    private const string NoCompareReason = "Not available for this grid";

    private ICompareHost? CompareHost =>
        this.GetVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<ICompareHost>().FirstOrDefault();

    string? IGridCommands.SelectForCompareDisabledReason =>
        CompareHost?.Compare is not null && Columns is { Count: > 0 } ? null : NoCompareReason;

    string? IGridCommands.CompareWithSelectedDisabledReason =>
        ((IGridCommands)this).SelectForCompareDisabledReason
        ?? (CompareHost!.Compare!.Selected is null ? CompareService.NothingSelectedReason : null);

    Task IGridCommands.SelectForCompareAsync()
    {
        if (CompareHost is { Compare: { } compare } host) compare.Select(Snapshot(host));
        return Task.CompletedTask;
    }

    Task IGridCommands.CompareWithSelectedAsync()
    {
        if (CompareHost is { Compare: { } compare } host) compare.CompareWith(Snapshot(host));
        return Task.CompletedTask;
    }

    private CompareSnapshot Snapshot(ICompareHost host) =>
        host.CreateCompareSnapshot(Columns ?? [], ShownRows.Select(r => GridCellColumn.ValuesOf(r) ?? []).ToList());

    /// <summary>The compare entries of the cell menu, and what to refresh each time it opens.</summary>
    private (IReadOnlyList<Control> Items, Action Opening) BuildCompareMenu()
    {
        IGridCommands commands = this;
        MenuItem Item(string header, Func<Task> action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => _ = RunSafe(action);
            ToolTip.SetShowOnDisabled(item, true);
            return item;
        }

        var separator = new Separator();
        var select = Item("Select for compare", commands.SelectForCompareAsync);
        var with = Item("Compare with selected", commands.CompareWithSelectedAsync);
        return ([separator, select, with], () =>
        {
            var shown = CompareHost?.Compare is not null;
            separator.IsVisible = select.IsVisible = with.IsVisible = shown;
            if (!shown) return;
            var why = commands.CompareWithSelectedDisabledReason;
            with.IsEnabled = why is null;
            ToolTip.SetTip(with, why ?? CompareHost!.Compare!.Selected!.Detail);
        });
    }

    /// <summary>The row the grid is on, as shown; -1 when none.</summary>
    public int CurrentRowIndex => Grid.SelectedIndex;
}
