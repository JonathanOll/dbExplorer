using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Views;

/// <summary>
/// Name and ordered members of a group; OK validates (the rules live in the view model), Cancel closes. The two lists move
/// connections with the arrow buttons, Enter / Delete, a double click or a drag; the order of the members is the order of the group.
/// </summary>
public partial class GroupEditorDialog : Window
{
    private static readonly DataFormat<string> DragFormat = DataFormat.CreateStringApplicationFormat("dbx-group-editor");

    /// <summary>The second line of a row: where the connection points ("host:port / database" or the file name), then its folder.</summary>
    public static readonly IValueConverter Detail = new FuncValueConverter<ConnectionConfig?, string>(c =>
    {
        if (c is null) return "";
        var target = c.Kind == DbKind.Sqlite
            ? System.IO.Path.GetFileName(c.Database)
            : $"{c.Host}{(c.Port is { } p ? ":" + p : "")} / {c.Database}";
        return string.IsNullOrEmpty(c.Folder) ? target : $"{target} · {c.Folder}";
    });


    private ListBox? _dragList;
    private Point? _dragStart;
    private INotifyCollectionChanged? _watchedMembers;

    public GroupEditorDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        // The number in front of a member is its position: kept in the container (Tag), refreshed when the list reorders.
        MemberList.ContainerPrepared += (_, e) => ((Control)e.Container).Tag = e.Index + 1;
        MemberList.ContainerIndexChanged += (_, e) => ((Control)e.Container).Tag = e.NewIndex + 1;
        CandidateList.SelectionChanged += (_, _) => UpdateButtons();
        MemberList.SelectionChanged += (_, _) => UpdateButtons();
        DataContextChanged += (_, _) => WatchMembers();

        FilterBox.KeyDown += OnFilterKeyDown;
        CandidateList.KeyDown += OnCandidateKeyDown;
        MemberList.KeyDown += OnMemberKeyDown;
        foreach (var list in new[] { CandidateList, MemberList })
        {
            list.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
            list.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel);
            list.AddHandler(DragDrop.DragOverEvent, OnListDragOver);
        }
        MemberList.AddHandler(DragDrop.DropEvent, OnMemberDrop);
        CandidateList.AddHandler(DragDrop.DropEvent, OnCandidateDrop);
        UpdateButtons();
    }

    private GroupEditorViewModel? ViewModel => DataContext as GroupEditorViewModel;

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.TryConfirm() == true)
        {
            Close(true);
            return;
        }
        if (ViewModel is { HasNameError: true }) NameBox.Focus();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    // ---- moving between the lists ----

    private void OnAddClick(object? sender, RoutedEventArgs e) => AddSelected();

    private void OnRemoveClick(object? sender, RoutedEventArgs e) => RemoveSelected();

    private void OnMoveUpClick(object? sender, RoutedEventArgs e) => MoveSelected(-1);

    private void OnMoveDownClick(object? sender, RoutedEventArgs e) => MoveSelected(+1);

    // Double click moves a connection across, like the arrow buttons.
    private void OnCandidateDoubleTapped(object? sender, TappedEventArgs e) => AddSelected();

    private void OnMemberDoubleTapped(object? sender, TappedEventArgs e) => RemoveSelected();

    private void AddSelected()
    {
        if (ViewModel is not { } vm) return;
        vm.AddRange(CandidateList.SelectedItems?.OfType<ConnectionConfig>().ToList() ?? []);
        UpdateButtons();
    }

    private void RemoveSelected()
    {
        if (ViewModel is not { } vm) return;
        vm.RemoveRange(MemberList.SelectedItems?.OfType<ConnectionConfig>().ToList() ?? []);
        UpdateButtons();
    }

    private void MoveSelected(int delta)
    {
        if (ViewModel is not { } vm) return;
        var chosen = MemberList.SelectedItems?.OfType<ConnectionConfig>().ToList() ?? [];
        if (chosen.Count == 0) return;
        var ordered = chosen.OrderBy(c => vm.Selected.IndexOf(c)).ToList();
        if (delta > 0) ordered.Reverse();   // the one nearest to the edge moves first
        foreach (var c in ordered)
        {
            if (delta < 0) vm.MoveUp(c);
            else vm.MoveDown(c);
        }
        Reselect(chosen);
        MemberList.Focus();
    }

    private void Reselect(IReadOnlyList<ConnectionConfig> members)
    {
        MemberList.SelectedItems?.Clear();
        foreach (var m in members) MemberList.SelectedItems?.Add(m);
    }

    private void WatchMembers()
    {
        if (_watchedMembers is not null) _watchedMembers.CollectionChanged -= OnMembersChanged;
        _watchedMembers = ViewModel?.Selected;
        if (_watchedMembers is not null) _watchedMembers.CollectionChanged += OnMembersChanged;
        UpdateButtons();
    }

    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateButtons();

    /// <summary>Each arrow is live only when it has something to do; its tooltip says what it does, or why it cannot.</summary>
    private void UpdateButtons()
    {
        var vm = ViewModel;
        var candidates = CandidateList.SelectedItems?.Count ?? 0;
        var members = MemberList.SelectedItems?.OfType<ConnectionConfig>().ToList() ?? [];
        var indexes = vm is null ? [] : members.Select(m => vm.Selected.IndexOf(m)).Where(i => i >= 0).ToList();
        var count = vm?.Selected.Count ?? 0;

        Set(AddButton, candidates > 0, "Add the selected connections (Enter)", "Select connections on the left to add them");
        Set(RemoveButton, indexes.Count > 0, "Remove the selected members (Delete)", "Select members on the right to remove them");
        Set(UpButton, indexes.Count > 0 && indexes.Min() > 0, "Move up (Alt+Up)",
            indexes.Count == 0 ? "Select a member to move it" : "Already first");
        Set(DownButton, indexes.Count > 0 && indexes.Max() < count - 1, "Move down (Alt+Down)",
            indexes.Count == 0 ? "Select a member to move it" : "Already last");
    }

    private static void Set(Button button, bool enabled, string tip, string reason)
    {
        button.IsEnabled = enabled;
        ToolTip.SetTip(button, enabled ? tip : reason);
    }

    // ---- keyboard ----

    /// <summary>Enter in the filter adds the first match (type a few letters, Enter); Down goes to the list; Esc clears the filter first.</summary>
    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Enter:
                if (vm.Candidates.FirstOrDefault() is { } first)
                {
                    vm.Add(first);
                    vm.CandidateFilter = "";
                }
                e.Handled = true;
                break;
            case Key.Down when vm.Candidates.Count > 0:
                CandidateList.SelectedIndex = Math.Max(0, CandidateList.SelectedIndex);
                (CandidateList.ContainerFromIndex(CandidateList.SelectedIndex) as Control)?.Focus();
                e.Handled = true;
                break;
            case Key.Escape when vm.CandidateFilter.Length > 0:
                vm.CandidateFilter = "";
                e.Handled = true;
                break;
        }
    }

    private void OnCandidateKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Enter or Key.Right)) return;
        AddSelected();
        e.Handled = true;   // Enter adds here; it must not reach the default button and close the dialog
    }

    private void OnMemberKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Delete or Key.Left)
        {
            RemoveSelected();
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Up or Key.Down)
        {
            MoveSelected(e.Key == Key.Up ? -1 : +1);
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Enter)
        {
            e.Handled = true;   // Enter in the list never means "save the group"
        }
    }

    // ---- drag and drop (between the lists, and to reorder the members) ----

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var list = (ListBox)sender!;
        var onItem = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null;
        _dragList = onItem && e.GetCurrentPoint(list).Properties.IsLeftButtonPressed && e.KeyModifiers == KeyModifiers.None ? list : null;
        _dragStart = _dragList is null ? null : e.GetPosition(this);
    }

    private async void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || _dragList is not { } list) return;
        if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) { _dragStart = null; return; }
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;

        _dragStart = null;
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ConnectionConfig;
        var chosen = list.SelectedItems?.OfType<ConnectionConfig>().ToList() ?? [];
        if (item is not null && !chosen.Contains(item)) chosen = [item];
        if (chosen.Count == 0) return;
        try
        {
            var transfer = new DataTransfer();
            var from = list == MemberList ? "m" : "c";
            transfer.Add(DataTransferItem.Create(DragFormat, from + ":" + string.Join(';', chosen.Select(c => c.Id))));
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine("Group editor drag failed: " + ex.Message);
        }
    }

    private static void OnListDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;

    private (string From, List<ConnectionConfig> Items)? Dragged(DragEventArgs e)
    {
        if (ViewModel is not { } vm || e.DataTransfer.TryGetValue(DragFormat) is not { Length: > 2 } text) return null;
        var from = text[..1];
        var ids = text[2..].Split(';').Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).ToHashSet();
        var pool = from == "m" ? vm.Selected : vm.Candidates;
        return (from, pool.Where(c => ids.Contains(c.Id)).ToList());
    }

    private void OnMemberDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (ViewModel is not { } vm || Dragged(e) is not { } dragged || dragged.Items.Count == 0) return;
        var index = DropIndex(MemberList, e);
        if (dragged.From == "c")
        {
            var before = vm.Selected.Count;
            vm.AddRange(dragged.Items);
            var added = vm.Selected.Skip(before).ToList();   // what AddRange appended, in order
            for (var k = 0; k < added.Count; k++) vm.MoveTo(added[k], index + k);
        }
        else
        {
            var movedBefore = dragged.Items.Count(c => vm.Selected.IndexOf(c) < index);
            var start = index - movedBefore;
            for (var k = 0; k < dragged.Items.Count; k++) vm.MoveTo(dragged.Items[k], start + k);
            Reselect(dragged.Items);
        }
        UpdateButtons();
    }

    private void OnCandidateDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (ViewModel is not { } vm || Dragged(e) is not { From: "m" } dragged) return;
        vm.RemoveRange(dragged.Items);
        UpdateButtons();
    }

    /// <summary>The position a drop at the pointer inserts at: before the first row whose upper half holds the pointer.</summary>
    private static int DropIndex(ListBox list, DragEventArgs e)
    {
        for (var i = 0; i < list.ItemCount; i++)
        {
            if (list.ContainerFromIndex(i) is not { } container) continue;
            if (e.GetPosition(container).Y < container.Bounds.Height / 2) return i;
        }
        return list.ItemCount;
    }
}
