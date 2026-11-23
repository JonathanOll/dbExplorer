using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Views;

/// <summary>Name and ordered members of a group; OK validates (the rules live in the view model), Cancel closes.</summary>
public partial class GroupEditorDialog : Window
{
    public GroupEditorDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private GroupEditorViewModel? ViewModel => DataContext as GroupEditorViewModel;

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.TryConfirm() == true) Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    // Double click moves a connection across, like the arrow buttons.
    private void OnCandidateDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (CandidateList.SelectedItem is ConnectionConfig c) ViewModel?.Add(c);
    }

    private void OnMemberDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (MemberList.SelectedItem is ConnectionConfig c) ViewModel?.Remove(c);
    }
}
