using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

public partial class MainWindow : Window
{
    private static readonly DataFormat<string> ConnectionFormat = DataFormat.CreateStringApplicationFormat("dbx-connection-id");
    private Point? _dragStart;
    private ConnectionNodeViewModel? _dragNode;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        TrackBounds();
        var tree = this.FindControl<TreeView>("ConnectionTree")!;
        tree.DoubleTapped += OnTreeDoubleTapped;
        tree.KeyDown += OnTreeKeyDown;
        tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        tree.PointerMoved += OnTreePointerMoved;
        tree.PointerReleased += (_, _) => _dragStart = null;
        tree.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver);
        tree.AddHandler(DragDrop.DropEvent, OnTreeDrop);
        tree.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropTarget());
        this.FindControl<TextBox>("FilterBox")!.KeyDown += OnFilterKeyDown;
        this.FindControl<GridSplitter>("NavSplitter")!.DoubleTapped += OnNavigatorSplitterDoubleTapped;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel main) main.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsNavigatorVisible)) ShowNavigator(main.IsNavigatorVisible);
            };
        };
        this.FindControl<TabControl>("DocTabs")!.AddHandler(PointerReleasedEvent, OnDocTabsPointerReleased, RoutingStrategies.Tunnel);
    }

    // Middle-click on a document tab header closes it, through the same path as the x button (unsaved work is asked about).
    // On release, like browsers, so a press that slides off the tab does nothing. The tab strip only: the sub-tabs of a table
    // tab (Data / Structure) are TabItems too and must not close anything.
    private void OnDocTabsPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Middle || sender is not TabControl tabs || DataContext is not MainViewModel main) return;
        if ((e.Source as Visual)?.FindAncestorOfType<TabItem>(includeSelf: true) is not { DataContext: TabViewModel tab } item) return;
        if (!ReferenceEquals(item.FindAncestorOfType<TabControl>(), tabs)) return;
        e.Handled = true;
        if (ReferenceEquals(tab, main.ActiveTab) && !CommitOpenCellEdit()) return;   // like Ctrl+W: a refused half-typed cell stops the close
        main.CloseTabCommand.Execute(tab);
    }

    private ConnectionTreeViewModel? Tree => (DataContext as MainViewModel)?.Tree;

    // ---- remembering size, position and maximised state ----

    private NormalBoundsTracker _bounds = new();
    private WindowState _lastShownState = WindowState.Normal;

    /// <summary>Where the window placement is saved when the window closes; null (tests) saves nothing.</summary>
    public string? StateFile { get; set; }

    /// <summary>
    /// Reads the placement of the last run from <paramref name="file"/> (one tiny file, before the window is shown), fits
    /// it onto the screens that exist now and applies it; the same file is written when the window closes. Never throws:
    /// without a usable file the default placement stays.
    /// </summary>
    public void RestorePlacement(string file)
    {
        StateFile = file;
        try
        {
            if (WindowStateStore.TryLoad(file) is not { } saved) return;
            var screens = Screens.All
                .OrderByDescending(s => s.IsPrimary)
                .Select(s => new ScreenArea(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height, s.Scaling))
                .ToList();
            ApplyPlacement(WindowStateStore.Fit(saved, screens, MinWidth, MinHeight));
        }
        catch (Exception)
        {
            // the default placement of the window stays
        }
    }

    /// <summary>Takes a placement already fitted to the screens (see <see cref="WindowStateStore.Fit"/>); call before showing.</summary>
    public void ApplyPlacement(WindowPlacement placement)
    {
        _bounds = new NormalBoundsTracker(placement);
        if (placement.NavWidth is { } saved) SetNavigatorWidth(saved);
        Width = placement.Width;
        Height = placement.Height;
        if (placement.X is { } x && placement.Y is { } y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }
        if (placement.Maximized) WindowState = WindowState.Maximized;
    }

    private void TrackBounds()
    {
        PositionChanged += (_, e) => _bounds.PositionChanged(e.Point.X, e.Point.Y, WindowState == WindowState.Normal);
        Resized += (_, e) => _bounds.SizeChanged(e.ClientSize.Width, e.ClientSize.Height, WindowState == WindowState.Normal);
    }

    // The only place besides Options-OK that looks at the dbupdate folder: on demand, never at launch.
    private void OnSettingsFlyoutOpening(object? sender, EventArgs e) =>
        (DataContext as MainViewModel)?.RefreshUpgradeAvailability();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty) return;
        var state = change.GetNewValue<WindowState>();
        if (state == WindowState.Maximized) _bounds.BecameMaximized(Position.X, Position.Y, ClientSize.Width, ClientSize.Height);
        if (state != WindowState.Minimized) _lastShownState = state;
    }

    private void SaveWindowState()
    {
        if (StateFile is not { } file) return;
        if (_bounds.Placement(_lastShownState == WindowState.Maximized) is { } placement)
            WindowStateStore.TrySave(file, placement with { NavWidth = CurrentNavigatorWidth() });
    }

    // Closing the window asks every tab (MainViewModel.ConfirmExitAsync). With nothing to ask the answer is immediate and
    // the close goes on untouched. When a tab needs a prompt the close is cancelled, the prompts run, and the window
    // closes again once all are resolved. What must run on exit (keeping the unsaved scripts, saving the window
    // placement) is in OnClosed, which is only reached once the close is confirmed, however many Closing events led there.
    private bool _exitConfirmed;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try
        {
            (DataContext as MainViewModel)?.PersistScripts();   // idempotent; skips the scripts the user discarded
        }
        catch (Exception)
        {
            // best effort: the autosave copies written while editing are still there
        }
        try
        {
            (DataContext as MainViewModel)?.PersistSession();   // after the scripts: the tabs that remain, minus the discarded ones
        }
        catch (Exception)
        {
            // best effort: the session written while working is still there
        }
        SaveWindowState();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_exitConfirmed || e.Cancel || DataContext is not MainViewModel main) return;

        var asking = main.ConfirmExitAsync();
        if (asking.IsCompletedSuccessfully)
        {
            if (!asking.Result) e.Cancel = true;
            return;
        }
        e.Cancel = true;
        _ = FinishExitAsync(asking);
    }

    /// <summary>Closes the window whose exit prompts were already answered (the update flow asked them): no second round of questions.</summary>
    public void CloseConfirmed()
    {
        _exitConfirmed = true;
        Close();
    }

    private async Task FinishExitAsync(Task<bool> asking)
    {
        bool ok;
        try
        {
            ok = await asking;
        }
        catch (Exception ex)   // ConfirmExitAsync reports its own failures; this is the last line of defence against a hung close
        {
            (DataContext as MainViewModel)?.Status.Error($"Cannot close: {ex.Message}");
            return;
        }
        if (!ok) return;
        _exitConfirmed = true;
        Close();
    }

    // Window-level shortcuts (see MainShortcuts): they run in the tunnel phase, before any editor or text box sees the key.
    // Their commands catch their own exceptions, so nothing can escape from here.
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // F5 with the navigator focused refreshes the schema; elsewhere it belongs to the table tab (MainShortcuts)
        if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None
            && this.FindControl<TreeView>("ConnectionTree") is { IsKeyboardFocusWithin: true } && Tree is { } navigator)
        {
            navigator.RefreshSelectedCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (DataContext is MainViewModel main && MainShortcuts.Handle(main, e.Key, e.KeyModifiers, CommitOpenCellEdit)) e.Handled = true;
    }

    /// <summary>
    /// Ends a cell edit in progress in the grid on screen (the text typed so far becomes a pending edit). False when the
    /// text is refused; the chord is then ignored and the editor stays open with its message. Walks the tree only when a
    /// chord that leaves or reloads the tab is pressed.
    /// </summary>
    private bool CommitOpenCellEdit()
    {
        foreach (var grid in this.GetVisualDescendants().OfType<ResultGrid>())
            if (!grid.CommitPendingEdit()) return false;
        return true;
    }

    // async void handlers must never let an exception escape: it would crash the process.
    private async void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is TreeView { SelectedItem: { } node }) await Safe(() => Activate(node));
    }

    private async void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TreeView { SelectedItem: { } node }) return;
        switch (e.Key, e.KeyModifiers)
        {
            case (Key.Enter, KeyModifiers.None):
                e.Handled = true;
                await Safe(() => Activate(node));
                break;
            case (Key.F2, KeyModifiers.None):
                e.Handled = true;
                Edit(node);
                break;
            case (Key.Delete, KeyModifiers.None):
                e.Handled = true;
                Delete(node);
                break;
            case (Key.F, KeyModifiers.Control):
                e.Handled = true;
                this.FindControl<TextBox>("FilterBox")?.Focus();
                break;
        }
    }

    // F2 / Delete on the selected node: the same commands (and confirmations) as the context menu.
    private void Edit(object node)
    {
        if (Tree is not { } tree) return;
        switch (node)
        {
            case ConnectionNodeViewModel c: tree.EditConnectionCommand.Execute(c); break;
            case FolderNodeViewModel f: tree.RenameFolderCommand.Execute(f); break;
            case GroupNodeViewModel g: tree.RenameGroupCommand.Execute(g.Group); break;
        }
    }

    private void Delete(object node)
    {
        if (Tree is not { } tree) return;
        switch (node)
        {
            case ConnectionNodeViewModel c: tree.DeleteConnectionCommand.Execute(c); break;
            case FolderNodeViewModel f: tree.DeleteFolderCommand.Execute(f); break;
            case GroupNodeViewModel g: tree.DeleteGroupCommand.Execute(g.Group); break;
            case GroupMemberNodeViewModel m: tree.RemoveMemberCommand.Execute(m); break;
        }
    }

    // In the filter box: Escape clears it, Down goes into the tree.
    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Tree is { HasFilter: true } tree)
        {
            tree.ClearFilter();
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            var list = this.FindControl<TreeView>("ConnectionTree")!;
            if (list.SelectedItem is null && Tree?.Nodes.FirstOrDefault(n => n.IsShown) is { } first) list.SelectedItem = first;
            list.Focus();
            e.Handled = true;
        }
    }

    private const double DefaultNavigatorWidth = 304;
    private double _navWidth = DefaultNavigatorWidth;   // the width to come back to when the navigator is shown again

    private ColumnDefinition NavColumn => ((Grid)this.FindControl<GridSplitter>("NavSplitter")!.Parent!).ColumnDefinitions[0];

    private double CurrentNavigatorWidth() =>
        DataContext is MainViewModel { IsNavigatorVisible: false } ? _navWidth : Math.Round(NavColumn.ActualWidth > 0 ? NavColumn.ActualWidth : _navWidth);

    private void SetNavigatorWidth(double width)
    {
        _navWidth = Math.Clamp(width, NavColumn.MinWidth, NavColumn.MaxWidth);
        NavColumn.Width = new GridLength(_navWidth);
    }

    // Double-click on the splitter: back to the default width.
    private void OnNavigatorSplitterDoubleTapped(object? sender, TappedEventArgs e) => SetNavigatorWidth(DefaultNavigatorWidth);

    // Ctrl+B: hides the navigator (the editor gets its width) or brings it back at its last width.
    private void ShowNavigator(bool visible)
    {
        var grid = (Grid)this.FindControl<GridSplitter>("NavSplitter")!.Parent!;
        var nav = grid.ColumnDefinitions[0];
        var split = grid.ColumnDefinitions[1];
        if (visible)
        {
            nav.MinWidth = 220;
            nav.Width = new GridLength(_navWidth);
            split.Width = new GridLength(5);
        }
        else
        {
            if (nav.ActualWidth > 0) _navWidth = Math.Round(nav.ActualWidth);
            nav.MinWidth = 0;
            nav.Width = new GridLength(0);
            split.Width = new GridLength(0);
        }
        grid.Children.OfType<Border>().First().IsVisible = visible;
        this.FindControl<GridSplitter>("NavSplitter")!.IsVisible = visible;
        if (!visible) this.FindControl<TabControl>("DocTabs")?.Focus();
    }

    private void OnShortcutsScrimPressed(object? sender, PointerPressedEventArgs e) =>
        (DataContext as MainViewModel)?.HideShortcutsCommand.Execute(null);

    // a click inside the card must not reach the scrim
    private void OnShortcutsCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private async void OnOpenTableNode(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TableNodeViewModel table) await Safe(() => Activate(table));
    }

    // "Copy name" of a connection or a table.
    private async void OnCopyNodeName(object? sender, RoutedEventArgs e)
    {
        var text = (sender as Control)?.DataContext switch
        {
            ConnectionNodeViewModel c => c.Title,
            TableNodeViewModel t => t.Table.Ref.ToString(),
            _ => null,
        };
        if (text is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await Safe(() => clipboard.SetTextAsync(text));
        (DataContext as MainViewModel)?.Status.Info($"Copied {text}.");
    }

    private async Task Activate(object node)
    {
        if (Tree is not { } tree) return;
        switch (node)
        {
            case TableNodeViewModel table:
                await tree.OpenTableAsync(table); // reports failures to the status bar itself
                break;
            case ConnectionNodeViewModel connection:
                await tree.ConnectAsync(connection);
                break;
            case GroupMemberNodeViewModel member:
                await tree.OpenGroupMemberAsync(member);
                break;
        }
    }

    private async Task Safe(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e)
        {
            (DataContext as MainViewModel)?.Status.Error($"Unexpected error: {e.Message}");
        }
    }

    // ---- drag a connection onto a folder (or empty area / loose connection = root) ----

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        _dragNode = point.Properties.IsLeftButtonPressed ? NodeAt(e.Source) as ConnectionNodeViewModel : null;
        _dragStart = _dragNode is null ? null : e.GetPosition(this);
    }

    private async void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || _dragNode is not { } node) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _dragStart = null; return; }
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;

        _dragStart = null;
        await Safe(async () =>
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(ConnectionFormat, node.Config.Id.ToString()));
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        });
    }

    private TreeViewItem? _dropTarget;

    // Over a folder (or a connection inside one): move into it. Over a group (or one of its members): add to it.
    // Over the Groups header: refused. Elsewhere: the root. The container that would receive the drop is outlined.
    private void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(ConnectionFormat))
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropTarget();
            return;
        }

        var target = DropContainerAt(e.Source);
        e.DragEffects = target?.DataContext switch
        {
            FolderNodeViewModel => DragDropEffects.Move,
            GroupNodeViewModel => DragDropEffects.Link,
            _ when IsInGroupsSection(e.Source) => DragDropEffects.None,
            _ => DragDropEffects.Move,
        };
        SetDropTarget(e.DragEffects == DragDropEffects.None ? null : target);
    }

    private void SetDropTarget(TreeViewItem? item)
    {
        if (ReferenceEquals(item, _dropTarget)) return;
        _dropTarget?.Classes.Remove("drop");
        _dropTarget = item;
        _dropTarget?.Classes.Add("drop");
    }

    private void ClearDropTarget() => SetDropTarget(null);

    /// <summary>The folder or group container a drop at the visual would land in; null for the root.</summary>
    private static TreeViewItem? DropContainerAt(object? source)
    {
        for (var item = (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
             item is not null;
             item = item.FindAncestorOfType<TreeViewItem>())
            if (item.DataContext is FolderNodeViewModel or GroupNodeViewModel) return item;
        return null;
    }

    /// <summary>True over the Groups section (header, group or member).</summary>
    private static bool IsInGroupsSection(object? source)
    {
        for (var item = (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
             item is not null;
             item = item.FindAncestorOfType<TreeViewItem>())
            if (item.DataContext is GroupsSectionNodeViewModel or GroupNodeViewModel or GroupMemberNodeViewModel) return true;
        return false;
    }

    private void OnTreeDrop(object? sender, DragEventArgs e)
    {
        ClearDropTarget();
        if (Tree is not { } tree || e.DataTransfer.TryGetValue(ConnectionFormat) is not { } text || !Guid.TryParse(text, out var id)) return;
        var connection = tree.Config.Connections.FirstOrDefault(c => c.Id == id);

        if (DropContainerAt(e.Source)?.DataContext is GroupNodeViewModel group)
        {
            e.Handled = true;
            if (connection is not null && tree.AddToGroup(connection, group.Group))
                (DataContext as MainViewModel)?.Status.Success($"Added {connection.Name} to group {group.Group.Name}.");
            return;
        }
        if (IsInGroupsSection(e.Source))
        {
            e.Handled = true;
            return;
        }
        if (connection is null) return;

        var folder = DropTargetAt(e.Source) switch
        {
            FolderNodeViewModel f => f.Name,
            ConnectionNodeViewModel c => c.Config.Folder, // dropped on a sibling: same folder as that sibling
            _ => null,
        };
        if (!string.Equals(folder, connection.Folder, StringComparison.OrdinalIgnoreCase)) tree.MoveToFolder(connection, folder);
        e.Handled = true;
    }

    /// <summary>Nearest folder or connection node at or above the visual (tables and schemas belong to their connection).</summary>
    private static object? DropTargetAt(object? source)
    {
        for (var item = (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
             item is not null;
             item = item.FindAncestorOfType<TreeViewItem>())
            if (item.DataContext is FolderNodeViewModel or ConnectionNodeViewModel) return item.DataContext;
        return null;
    }

    private static object? NodeAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext;
}




