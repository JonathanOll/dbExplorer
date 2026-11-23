using System.ComponentModel;
using DbExplorer.Core.Querying;

namespace DbExplorer.App.ViewModels;

/// <summary>A column of a <c>ResultGrid</c>. <paramref name="LinkTarget"/> (e.g. "customers (id)") marks a foreign-key column.</summary>
public sealed record ResultColumn(
    string Name, string? TypeName = null, Type? ClrType = null, bool IsPrimaryKey = false, string? LinkTarget = null);

/// <summary>
/// The opt-in table features of a <c>ResultGrid</c>: the filter row under the headers, server-side sort on header
/// click, foreign-key navigation and the cell context menu. A plain result-set grid leaves this null.
/// Property changes (<see cref="Sort"/>, <c>ColumnFilters</c>) are announced through <see cref="INotifyPropertyChanged"/>.
/// </summary>
public interface ITableGridActions : INotifyPropertyChanged
{
    SortSpec? Sort { get; }

    string GetFilter(string column);

    /// <summary>Stores the box text; does not query.</summary>
    void SetFilter(string column, string text);

    Task ApplyFiltersAsync();

    Task ToggleSortAsync(string column);

    /// <summary>Opens the row referenced by the foreign key that <paramref name="column"/> belongs to.</summary>
    Task FollowForeignKeyAsync(int rowIndex, string column);

    Task FilterByCellAsync(int rowIndex, string column);
}
