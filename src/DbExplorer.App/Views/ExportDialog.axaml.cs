using Avalonia.Controls;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

public partial class ExportDialog : Window
{
    private ExportDialogViewModel? _vm;

    public ExportDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.CloseRequested -= Close;
            _vm = DataContext as ExportDialogViewModel;
            if (_vm is not null) _vm.CloseRequested += Close;
        };
    }

    // Closing the window while a file is being written stops the export (its partial file is deleted).
    protected override void OnClosed(EventArgs e)
    {
        if (_vm is { IsExporting: true } vm) vm.Cancel();
        base.OnClosed(e);
    }
}
