using CommunityToolkit.Mvvm.ComponentModel;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// One row of a table grid. <see cref="Values"/> is what the grid shows (and what later edits change);
/// <see cref="Original"/> is the snapshot taken from the database. Values are raw driver values (NULL = null).
/// </summary>
public sealed partial class RowViewModel : ObservableObject
{
    public RowViewModel(object?[] values, bool isNew = false)
    {
        Values = values;
        Original = (object?[])values.Clone();
        IsNew = isNew;
    }

    public object?[] Values { get; }

    public object?[] Original { get; }

    public bool IsNew { get; }

    [ObservableProperty] private bool _isDeleted;

    /// <summary>True when the row has anything to save: it is new, marked deleted, or a cell differs from what was loaded.</summary>
    public bool HasChanges
    {
        get
        {
            if (IsNew || IsDeleted) return true;
            for (var i = 0; i < Values.Length; i++)
                if (IsDirty(i)) return true;
            return false;
        }
    }

    /// <summary>Puts every cell back to the loaded value and clears the deleted mark.</summary>
    public void Revert()
    {
        Array.Copy(Original, Values, Values.Length);
        IsDeleted = false;
    }

    /// <summary>
    /// True when the cell differs from what was loaded. For a new row every non-NULL cell counts as dirty (there is
    /// nothing loaded to compare with).
    /// </summary>
    public bool IsDirty(int col) =>
        (uint)col < (uint)Values.Length && (IsNew ? Values[col] is not null : !SameValue(Values[col], Original[col]));   // out of range: a grid still showing these rows under the columns of another table

    private static bool SameValue(object? a, object? b) => a switch
    {
        byte[] x when b is byte[] y => x.AsSpan().SequenceEqual(y),
        _ => Equals(a, b),
    };
}
