using Avalonia.Controls;
using Avalonia.Interactivity;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// Chooses the columns that identify a row of a table without a primary key. The rules (what "all columns" means,
/// which types are unusable) live in <see cref="VirtualKeyDialogViewModel"/>; this window only shows them.
/// Closes with the chosen columns (<c>["*"]</c> = all) or null.
/// </summary>
public partial class VirtualKeyDialog : Window
{
    private readonly VirtualKeyDialogViewModel? _viewModel;
    private readonly List<(CheckBox Box, VirtualKeyColumn Column)> _boxes = [];

    public VirtualKeyDialog() => InitializeComponent();

    public VirtualKeyDialog(VirtualKeyDialogViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        Heading.Text = $"Columns that identify a row of {viewModel.Table}";
        WarningText.Text = VirtualKeyDialogViewModel.Warning;
        if (viewModel.ExcludedNote is { } note)
        {
            ExcludedText.Text = note;
            ExcludedText.IsVisible = true;
        }
        AllColumns.IsChecked = viewModel.UseAllColumns;
        foreach (var column in viewModel.Columns)
        {
            var box = new CheckBox { Content = column.Label, IsChecked = column.IsSelected, IsEnabled = column.IsKeyable };
            _boxes.Add((box, column));
            ColumnList.Children.Add(box);
        }
        ColumnList.IsEnabled = !viewModel.UseAllColumns;
    }

    private void OnAllColumnsClick(object? sender, RoutedEventArgs e) => ColumnList.IsEnabled = AllColumns.IsChecked != true;

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } vm) return;
        vm.UseAllColumns = AllColumns.IsChecked == true;
        foreach (var (box, column) in _boxes) column.IsSelected = box.IsChecked == true;
        if (vm.Result is { } result)
        {
            Close(result);
            return;
        }
        ErrorText.Text = vm.Error;
        ErrorText.IsVisible = true;
        ErrorIcon.IsVisible = true;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
