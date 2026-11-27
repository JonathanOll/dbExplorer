using DbExplorer.Core.Comparing;

namespace DbExplorer.App.ViewModels;

public sealed partial class TableTabViewModel : ICompareHost
{
    /// <summary>"Select for compare" of the data grid; the shell sets it.</summary>
    public CompareService? Compare { get; set; }

    public CompareSnapshot CreateCompareSnapshot(IReadOnlyList<ResultColumn> columns, IReadOnlyList<object?[]> rows)
    {
        var key = KeyColumns?.Where(k => k != "*").ToList() ?? [];
        var compared = columns.Select(c => new ComparedColumn(c.Name, c.TypeName, c.ClrType,
            key.Count > 0 ? key.Contains(c.Name, StringComparer.OrdinalIgnoreCase) : c.IsPrimaryKey)).ToList();
        // Copies: editing a cell changes the array of its row.
        var data = new ComparedData(compared, rows.Select(r => (object?[])r.Clone()).ToArray());

        var notes = new List<string>();
        if (HasNextPage || PageIndex > 0)
        {
            var from = PageIndex * PageSize;
            notes.Add($"only one page of the table is loaded (rows {from + 1:N0} to {from + data.Rows.Count:N0}).");
        }
        if (HasPendingEdits) notes.Add("pending changes that are not saved are included.");
        var filtered = HasActiveFilters ? ", filtered" : "";
        return new CompareSnapshot(Table.Name, $"{Table}{filtered} ({Connection.Name})", Connection, data,
            notes.Count == 0 ? null : string.Join(" ", notes));
    }
}
