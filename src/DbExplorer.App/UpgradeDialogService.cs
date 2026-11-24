using Avalonia.Controls;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App;

/// <summary>Real <see cref="IUpgradeDialogs"/>: modal windows owned by the main window.</summary>
public sealed class UpgradeDialogService(Window owner) : IUpgradeDialogs
{
    public Task<bool> ShowOptionsAsync(OptionsViewModel viewModel) =>
        new OptionsDialog { DataContext = viewModel }.ShowDialog<bool>(owner);

    public Task ShowUpgradeAsync(UpgradeViewModel viewModel) =>
        new UpgradeDialog { DataContext = viewModel }.ShowDialog(owner);
}
