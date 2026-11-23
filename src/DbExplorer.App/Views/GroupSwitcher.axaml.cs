using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Views;

/// <summary>
/// The database selector of a tab (see <see cref="TabViewModel.GroupMembers"/>). Items and selection are pushed from the
/// view-model rather than bound two-way: choosing a member only ASKS the tab to switch (it may refuse, prompt about pending
/// edits or fail), and afterwards the selector shows the connection the tab is really on.
/// </summary>
public partial class GroupSwitcher : UserControl
{
    private TabViewModel? _vm;
    private bool _syncing;

    public GroupSwitcher()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Follow(DataContext as TabViewModel);
        AttachedToVisualTree += (_, _) => Follow(DataContext as TabViewModel);
        DetachedFromVisualTree += (_, _) => Follow(null);   // the tab outlives its view: do not stay subscribed
        MemberBox.SelectionChanged += OnMemberChanged;
        GroupBox.SelectionChanged += OnGroupChanged;
    }

    private void Follow(TabViewModel? next)
    {
        if (ReferenceEquals(_vm, next)) return;
        if (_vm is not null) _vm.PropertyChanged -= OnTabChanged;
        _vm = next;
        if (_vm is null) return;
        _vm.PropertyChanged += OnTabChanged;
        Sync();
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.Connection) or nameof(TabViewModel.Groups) or nameof(TabViewModel.ActiveGroup)
            or nameof(TabViewModel.GroupMembers) or nameof(TabViewModel.SelectedMember) or nameof(TabViewModel.HasGroupSwitch)
            or nameof(TabViewModel.HasGroupPicker) or nameof(TabViewModel.IsSwitching))
        {
            if (Dispatcher.UIThread.CheckAccess()) Sync();
            else Dispatcher.UIThread.Post(Sync);
        }
    }

    private void Sync()
    {
        if (_vm is not { } vm) return;
        _syncing = true;
        try
        {
            Root.IsVisible = vm.HasGroupSwitch;
            Root.IsEnabled = !vm.IsSwitching;
            if (!vm.HasGroupSwitch) return;
            GroupBox.IsVisible = vm.HasGroupPicker;
            GroupBox.ItemsSource = vm.Groups;
            GroupBox.SelectedItem = vm.ActiveGroup;
            MemberBox.ItemsSource = vm.GroupMembers;
            MemberBox.SelectedItem = vm.SelectedMember;
        }
        finally
        {
            _syncing = false;
        }
    }

    private async void OnMemberChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _vm is not { } vm || MemberBox.SelectedItem is not ConnectionConfig target || target.Id == vm.Connection.Id) return;
        try
        {
            await vm.SwitchConnectionAsync(target);   // never throws; says why itself when it refuses or fails
        }
        finally
        {
            Sync();   // back on the connection the tab is really on, whatever happened
        }
    }

    private void OnGroupChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _vm is not { } vm || GroupBox.SelectedItem is not ConnectionGroup group) return;
        vm.ActiveGroup = group;   // notifies: the member list follows
    }

    private async void OnPreviousClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is { } vm) await vm.SwitchRelativeAsync(-1);
    }

    private async void OnNextClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is { } vm) await vm.SwitchRelativeAsync(+1);
    }
}
