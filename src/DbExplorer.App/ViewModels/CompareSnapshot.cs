using DbExplorer.Core.Comparing;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// The loaded rows of a result as they were when it was picked: a later run or reload of its tab does not change them.
/// </summary>
/// <param name="Name">Short name for the tab title ("orders", "Script 1 · 2").</param>
/// <param name="Detail">What it is, with its connection.</param>
/// <param name="Partial">Why these are not all the rows of the query or table, or null when they are.</param>
public sealed record CompareSnapshot(
    string Name, string Detail, ConnectionConfig Connection, ComparedData Data, string? Partial = null);

/// <summary>A tab whose grids can be compared: it knows what their rows are and whether they are all of them.</summary>
public interface ICompareHost
{
    /// <summary>Set by the shell; null while comparing is not available.</summary>
    CompareService? Compare { get; }

    CompareSnapshot CreateCompareSnapshot(IReadOnlyList<ResultColumn> columns, IReadOnlyList<object?[]> rows);
}

/// <summary>"Select for compare" then "Compare with selected", shared by every tab.</summary>
public sealed class CompareService(StatusModel status, Action<CompareSnapshot, CompareSnapshot> open)
{
    public const string NothingSelectedReason = "Use \"Select for compare\" on a result first";

    /// <summary>The result picked as the left side; it stays picked, so several results can be compared with it.</summary>
    public CompareSnapshot? Selected { get; private set; }

    public void Select(CompareSnapshot snapshot)
    {
        Selected = snapshot;
        status.Info($"Selected for compare: {snapshot.Detail}. Now choose \"Compare with selected\" on another result.");
    }

    public void Clear() => Selected = null;

    /// <summary>Opens the comparison of the picked result (left) with this one (right).</summary>
    public void CompareWith(CompareSnapshot snapshot)
    {
        if (Selected is { } left) open(left, snapshot);
        else status.Info(NothingSelectedReason + ".");
    }
}
