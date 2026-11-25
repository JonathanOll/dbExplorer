using Avalonia.Controls;
using Avalonia.Interactivity;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// Chooses the columns that identify a row of a table without a primary key. The rules (what "all columns" means,
/// which types are unusable) live in <see cref="VirtualKeyDialogViewModel"/>; this window only shows them.
/// "Use this key" stays disabled until a choice is made. Closes with the chosen columns (<c>["*"]</c> = all) or null.
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
        UniqueText.IsVisible = viewModel.HasUniqueIndexColumns;
        AllColumns.IsChecked = viewModel.UseAllColumns;
        foreach (var column in viewModel.Columns)
        {
            var box = new CheckBox { Content = column.Label, IsChecked = column.IsSelected, IsEnabled = column.IsKeyable };
            box.Click += (_, _) => UpdateState();
            _boxes.Add((box, column));
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { box } };
            if (column.InUniqueIndex) row.Children.Add(new TextBlock { Text = "unique index", Classes = { "faint", "small" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            ColumnList.Children.Add(row);
        }
        ColumnList.IsEnabled = !viewModel.UseAllColumns;
        UpdateState();
    }

    private void OnAllColumnsClick(object? sender, RoutedEventArgs e)
    {
        ColumnList.IsEnabled = AllColumns.IsChecked != true;
        UpdateState();
    }

    // Reads the boxes into the view-model, then enables the OK button and says what is chosen (or what is missing).
    private void UpdateState()
    {
        if (_viewModel is not { } vm) return;
        vm.UseAllColumns = AllColumns.IsChecked == true;
        foreach (var (box, column) in _boxes) column.IsSelected = box.IsChecked == true;
        OkButton.IsEnabled = vm.Error is null;
        var chosen = vm.Result;
        Counter.Text = vm.Error is { } error ? error
            : vm.UseAllColumns ? "All columns"
            : chosen is { Length: 1 } ? "1 column selected"
            : $"{chosen?.Length ?? 0} columns selected";
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } vm) return;
        UpdateState();
        if (vm.Result is { } result) Close(result);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
