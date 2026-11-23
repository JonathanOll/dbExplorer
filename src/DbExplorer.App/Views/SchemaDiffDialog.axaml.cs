using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbExplorer.App.Views;

/// <summary>The schema-difference report of a group: one card per database. Informational; Close is the only button.</summary>
public partial class SchemaDiffDialog : Window
{
    public SchemaDiffDialog() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
