using System.ComponentModel;
using DbExplorer.Core.Querying;

namespace DbExplorer.App.ViewModels;

/// <summary>A column of a <c>ResultGrid</c>. <paramref name="LinkTarget"/> (e.g. "customers (id)") marks a foreign-key column.</summary>
public sealed record ResultColumn(
    string Name, string? TypeName = null, Type? ClrType = null, bool IsPrimaryKey = false, string? LinkTarget = null)
{
    private Type? Underlying => ClrType is null ? null : Nullable.GetUnderlyingType(ClrType) ?? ClrType;

    /// <summary>The declared type is a number: its header is right-aligned like its cells and its filter box hints at comparisons.</summary>
    public bool IsNumeric => Underlying is { } t
        && (t == typeof(sbyte) || t == typeof(byte) || t == typeof(short) || t == typeof(ushort) || t == typeof(int) || t == typeof(uint)
            || t == typeof(long) || t == typeof(ulong) || t == typeof(float) || t == typeof(double) || t == typeof(decimal));

    public bool IsBoolean => Underlying == typeof(bool);

    /// <summary>Dates and times (by CLR type, or by the declared name for engines that return them as text).</summary>
    public bool IsTemporal => Underlying is { } t && (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly) || t == typeof(TimeOnly))
        || (TypeName is { Length: > 0 } n && (n.Contains("DATE", StringComparison.OrdinalIgnoreCase) || n.Contains("TIME", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The CLR type is a GUID: the widest fixed-width value.</summary>
    public bool IsGuid => Underlying == typeof(Guid);
}

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

    // The members below have a default so that a minimal implementation (a test fake) keeps compiling.

    /// <summary>The filter text of <paramref name="column"/> that the rows on screen were loaded with (what the filter box is compared to).</summary>
    string GetAppliedFilter(string column) => GetFilter(column);

    /// <summary>Sorts by the column ascending (<c>false</c>), descending (<c>true</c>) or clears the sort (<c>null</c>).</summary>
    Task SortAsync(string column, bool? descending) => ToggleSortAsync(column);

    /// <summary>Filters out the rows that have the value of the cell (<c>!=value</c>, or <c>!null</c> for a NULL cell) and applies.</summary>
    Task ExcludeByCellAsync(int rowIndex, string column) => Task.CompletedTask;

    /// <summary>Clears the filter of one column and applies.</summary>
    Task ClearFilterAsync(string column) => Task.CompletedTask;
}
