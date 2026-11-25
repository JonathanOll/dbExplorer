using Avalonia.Controls;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App;

/// <summary>Real <see cref="IHistoryDialogs"/>: a modal window owned by the main window.</summary>
public sealed class HistoryDialogService(Window owner) : IHistoryDialogs
{
    public Task ShowHistoryAsync(HistoryViewModel viewModel) => new HistoryDialog { DataContext = viewModel }.ShowDialog(owner);
}
