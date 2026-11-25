using CommunityToolkit.Mvvm.Input;

namespace DbExplorer.App.ViewModels;

public partial class MainViewModel
{
    /// <summary>Navigator button and View menu: shows the navigator and expands it down to the active tab's item.</summary>
    [RelayCommand]
    private void RevealActiveTab()
    {
        IsNavigatorVisible = true;
        Tree.RevealTab(ActiveTab);
    }
}
