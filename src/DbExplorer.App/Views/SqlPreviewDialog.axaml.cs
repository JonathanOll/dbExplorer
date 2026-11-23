using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbExplorer.App.Views;

/// <summary>Lists the statements a save would run (display only) with their count; Apply or Cancel.</summary>
public partial class SqlPreviewDialog : Window
{
    // Cancel is the default and focused button: a stray Enter or Space must never execute writes. Apply is a deliberate click or Tab.
    public SqlPreviewDialog()
    {
        InitializeComponent();
        Opened += (_, _) => CancelButton.Focus();
    }

    public SqlPreviewDialog(IReadOnlyList<string> statements, string target) : this()
    {
        Title = $"Review changes · {target}";
        Target.Text = target;
        Summary.Text = (statements.Count == 1 ? "1 statement will run" : $"{statements.Count} statements will run") + $" on {target}";
        Statements.ItemsSource = statements;
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
