using System.Collections.Specialized;

namespace DbExplorer.App.ViewModels;

public sealed partial class MainViewModel
{
    private CompareService? _compare;

    /// <summary>The result picked for comparison, shared by every tab.</summary>
    public CompareService Compare => _compare ??= new CompareService(Status, (left, right) => OpenComparison(left, right));

    /// <summary>Opens the comparison of two results in a tab of its own.</summary>
    public CompareTabViewModel OpenComparison(CompareSnapshot left, CompareSnapshot right)
    {
        var tab = new CompareTabViewModel(left, right);
        Tabs.Add(tab);
        ActiveTab = tab;
        return tab;
    }

    // Every table and script tab, now and later, gets the shared service.
    private void WatchForCompare()
    {
        Tabs.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Move || e.NewItems is null) return;
            foreach (var tab in e.NewItems.OfType<TabViewModel>())
            {
                if (tab is TableTabViewModel table) table.Compare = Compare;
                else if (tab is ScriptTabViewModel script) script.Compare = Compare;
            }
        };
    }

    private IEnumerable<AppCommand> CompareCommands()
    {
        const string category = "Compare";
        var grid = ActiveTab is TableTabViewModel or ScriptTabViewModel ? FindActiveGrid?.Invoke() : null;
        var noGrid = grid is not null ? null
            : FindActiveGrid is null ? "Not available here" : "Show a table or a query result first";

        yield return Entry("compare.select", category, "Select this result for compare", "IconCompare",
            () => grid!.SelectForCompareAsync(), noGrid ?? grid!.SelectForCompareDisabledReason, keywords: "diff mark pick rows");
        yield return Entry("compare.with-selected", category, "Compare this result with the selected one", "IconCompare",
            () => grid!.CompareWithSelectedAsync(), noGrid ?? grid!.CompareWithSelectedDisabledReason, keywords: "diff differences rows",
            detail: Compare.Selected?.Name);
        yield return Entry("compare.clear", category, "Forget the result selected for compare", "IconClose", Do(Compare.Clear),
            Compare.Selected is null ? "No result is selected for compare" : null, keywords: "diff reset");

        var tab = ActiveTab as CompareTabViewModel;
        var none = tab is null ? "Open a comparison first" : null;
        yield return Entry("compare.next", category, "Next difference", "IconSortDown", Do(() => tab!.NextDifference()),
            none ?? (tab!.CanNavigate ? null : tab.NavigateTip), "F8", "diff");
        yield return Entry("compare.previous", category, "Previous difference", "IconSortUp", Do(() => tab!.PreviousDifference()),
            none ?? (tab!.CanNavigate ? null : tab.NavigateTip), "Shift+F8", "diff");
        yield return Entry("compare.only-differences", category, "Show only the differences", "IconFilter",
            Do(() => tab!.OnlyDifferences = !tab.OnlyDifferences), none, keywords: "diff filter toggle",
            detail: tab is null ? null : OnOff(tab.OnlyDifferences));
        yield return Entry("compare.swap", category, "Swap the two sides of the comparison", "IconCompare", Do(() => tab!.Swap()),
            none, keywords: "diff exchange left right");
    }
}
