using Avalonia.Controls;
using Avalonia.Interactivity;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>The schema-difference report of a group: one card per database that differs. Informational; Close is the only verdict.</summary>
public partial class SchemaDiffDialog : Window
{
    public SchemaDiffDialog() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SchemaDiffViewModel vm || Clipboard is not { } clipboard) return;
        try
        {
            await clipboard.SetTextAsync(vm.ReportText);
            CopyButton.Content = "Copied";
            await Task.Delay(1500);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine("Copy report failed: " + ex.Message);
        }
        finally
        {
            CopyButton.Content = "Copy report";
        }
    }
}
