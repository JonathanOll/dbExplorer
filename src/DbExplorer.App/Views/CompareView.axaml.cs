using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// The comparison tab. All state lives in <see cref="CompareTabViewModel"/>; this class only scrolls the grid to a
/// difference, tells which row it is on, and handles F8 / Shift+F8.
/// </summary>
public partial class CompareView : UserControl
{
    private CompareTabViewModel? _tab;
    private bool _revealing;

    public CompareView()
    {
        InitializeComponent();
        DiffGrid.SelectedRowsChanged += (_, _) =>
        {
            if (!_revealing && _tab is not null && DiffGrid.CurrentRowIndex >= 0) _tab.CurrentRow = DiffGrid.CurrentRowIndex;
        };
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Watch(DataContext as CompareTabViewModel);
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Watch(DataContext as CompareTabViewModel);
        Focusable = true;
    }

    // The view model outlives the view: subscribe only while on screen.
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        Watch(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void Watch(CompareTabViewModel? tab)
    {
        if (_tab is not null) _tab.RevealRequested -= Reveal;
        _tab = tab;
        if (_tab is not null) _tab.RevealRequested += Reveal;
    }

    private void Reveal(int row, int column)
    {
        _revealing = true;
        try
        {
            DiffGrid.SelectRow(row);
        }
        finally
        {
            _revealing = false;
        }
        // After the scroll's layout pass, so the column of the first changed cell is brought in from the side.
        Dispatcher.UIThread.Post(() => DiffGrid.ScrollToColumn(column), DispatcherPriority.Loaded);
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F8 || _tab is null || (e.KeyModifiers & ~KeyModifiers.Shift) != 0) return;
        e.Handled = true;
        if (e.KeyModifiers == KeyModifiers.Shift) _tab.PreviousDifference();
        else _tab.NextDifference();
    }

    // The two segments act as radio buttons: a click on the checked one must not leave both unchecked.
    private void OnByKeyClick(object? sender, RoutedEventArgs e) => Choose(byKey: true);

    private void OnByPositionClick(object? sender, RoutedEventArgs e) => Choose(byKey: false);

    private void Choose(bool byKey)
    {
        if (_tab is null) return;
        _tab.IsByKey = byKey;
        ByKeyButton.IsChecked = byKey;
        ByPositionButton.IsChecked = !byKey;
    }
}
