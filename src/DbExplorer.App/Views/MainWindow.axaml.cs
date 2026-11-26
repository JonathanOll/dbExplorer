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
    private static readonly DataFormat<string> ScriptFormat = DataFormat.CreateStringApplicationFormat("dbx-script-path");
    private string? _dragScriptPath;
    private static readonly DataFormat<string> FolderFormat = DataFormat.CreateStringApplicationFormat("dbx-folder-name");
    private string? _dragFolder;

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
            if (DataContext is MainViewModel main)
            {
                main.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.IsNavigatorVisible)) ShowNavigator(main.IsNavigatorVisible);
                };
                main.Tree.NodeRevealed += ScrollNodeIntoView;
            }
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
    // The row of a node just revealed does not exist until the tree has laid out the expanded branch: try now, then once more.
    private void ScrollNodeIntoView(TreeNodeViewModel node)
    {
        var tree = this.FindControl<TreeView>("ConnectionTree")!;
        void Scroll() => tree.TreeContainerFromItem(node)?.BringIntoView();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            tree.UpdateLayout();
            Scroll();
            Avalonia.Threading.Dispatcher.UIThread.Post(Scroll, Avalonia.Threading.DispatcherPriority.Background);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnExitMenuClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    // The Upgrade entries follow the same availability as the settings menu's.
    private void OnConnectionsMenuOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        (DataContext as MainViewModel)?.RefreshUpgradeAvailability();

    // File > Recent scripts: the last scripts opened or saved (name, the full path as a tooltip), then Clear. Built when the submenu
    // opens so the list is read (and the files that are gone dropped) only when it is looked at.
    private void OnRecentMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main || sender is not MenuItem menu || !ReferenceEquals(sender, e.Source)) return;
        main.RefreshRecentScripts();
        var items = new List<object>();
        foreach (var recent in main.RecentItems)
        {
            var item = new MenuItem
            {
                Header = new TextBlock { Text = recent.Name, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, MaxWidth = 420 },
                Command = main.OpenRecentScriptCommand,
                CommandParameter = recent.Path,
            };
            ToolTip.SetTip(item, recent.Path);
            items.Add(item);
        }
        if (items.Count == 0)
        {
            var none = new MenuItem { Header = "No recent scripts yet", IsEnabled = false };
            ToolTip.SetTip(none, "Scripts you open or save appear here.");
            ToolTip.SetShowOnDisabled(none, true);
            items.Add(none);
        }
        else
        {
            items.Add(new Separator());
            items.Add(new MenuItem { Header = "Clear recent scripts", Command = main.ClearRecentScriptsCommand });
        }
        menu.ItemsSource = items;
    }

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
            case ScriptFileNodeViewModel or ScriptFolderNodeViewModel: tree.RenameScriptNodeCommand.Execute(node); break;
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
            case ScriptFileNodeViewModel or ScriptFolderNodeViewModel: tree.DeleteScriptNodeCommand.Execute(node); break;
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
            case GroupNodeViewModel group:
                await tree.ConnectGroupAsync(group);
                break;
            case ScriptFileNodeViewModel script:
                await tree.OpenScriptAsync(script);   // reports failures to the status bar itself
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

    // ---- drag a connection onto a folder (or empty area / loose connection = root), or a folder before / after another ----

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var pressed = point.Properties.IsLeftButtonPressed ? NodeAt(e.Source) : null;
        _dragNode = pressed as ConnectionNodeViewModel;
        _dragScriptPath = pressed switch { ScriptFileNodeViewModel f => f.Path, ScriptFolderNodeViewModel d => d.Path, _ => null };
        _dragFolder = (pressed as FolderNodeViewModel)?.Name;
        _dragStart = _dragNode is null && _dragScriptPath is null && _dragFolder is null ? null : e.GetPosition(this);
    }

    private async void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _dragStart = null; return; }
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;

        _dragStart = null;
        await Safe(async () =>
        {
            var transfer = new DataTransfer();
            if (_dragNode is { } node) transfer.Add(DataTransferItem.Create(ConnectionFormat, node.Config.Id.ToString()));
            else if (_dragScriptPath is { } path) transfer.Add(DataTransferItem.Create(ScriptFormat, path));
            else transfer.Add(DataTransferItem.Create(FolderFormat, _dragFolder!));
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
            ClearDropTarget();   // a cancelled drag may end without a DragLeave
        });
    }

    private TreeViewItem? _dropTarget;

    // Over a folder (or a connection inside one): move into it. Over a group (or one of its members): add to it.
    // Over the Groups header: refused. Elsewhere: the root. The container that would receive the drop is outlined.
    private void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(FolderFormat) is { } folderName)
        {
            // A folder goes before or after another folder only (no nesting): a line shows where it would land.
            SetDropTarget(null);
            var spot = FolderSpotAt(e);
            var allowed = spot is { } s && Tree?.CanMoveFolder(folderName, s.Folder.Name, s.After) == true;
            e.DragEffects = allowed ? DragDropEffects.Move : DragDropEffects.None;
            ShowFolderDropLine(allowed ? spot : null);
            e.Handled = true;
            return;
        }
        if (e.DataTransfer.Contains(ScriptFormat))
        {
            // A script or script folder: only the Scripts section takes it (a folder, or the section for the top level).
            var scriptTarget = ScriptDropContainerAt(e.Source);
            e.DragEffects = scriptTarget is null ? DragDropEffects.None : DragDropEffects.Move;
            SetDropTarget(scriptTarget);
            return;
        }
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
            _ when IsInGroupsSection(e.Source) || ScriptDropContainerAt(e.Source) is not null => DragDropEffects.None,
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

    private void ClearDropTarget()
    {
        SetDropTarget(null);
        ShowFolderDropLine(null);
    }

    // The folder under the pointer and the side: the upper half of its row means before it; the lower half, or anything
    // inside it, means after it.
    private static (TreeViewItem Item, FolderNodeViewModel Folder, bool After)? FolderSpotAt(DragEventArgs e)
    {
        var under = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        for (var item = under; item is not null; item = item.FindAncestorOfType<TreeViewItem>())
        {
            if (item.DataContext is not FolderNodeViewModel folder) continue;
            var row = item.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "PART_LayoutRoot") ?? item;
            return (item, folder, !ReferenceEquals(item, under) || e.GetPosition(row).Y > row.Bounds.Height / 2);
        }
        return null;
    }

    // After an expanded folder the line sits under its last child: that is where the moved folder will appear.
    private void ShowFolderDropLine((TreeViewItem Item, FolderNodeViewModel Folder, bool After)? spot)
    {
        var line = this.FindControl<Border>("FolderDropLine")!;
        if (spot is not { } s || line.Parent is not Visual host
            || s.Item.TranslatePoint(new Point(0, s.After ? s.Item.Bounds.Height : 0), host) is not { } edge)
        {
            line.IsVisible = false;
            return;
        }
        var y = Math.Clamp(edge.Y - line.Height / 2, 0, Math.Max(0, host.Bounds.Height - line.Height));
        line.Margin = new Thickness(8, y, 8, 0);
        line.IsVisible = true;
    }

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

    // The folder (or the section header, = top level) of the library a drop at the visual would land in; null outside the section.
    private static TreeViewItem? ScriptDropContainerAt(object? source)
    {
        for (var item = (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
             item is not null;
             item = item.FindAncestorOfType<TreeViewItem>())
            if (item.DataContext is ScriptFolderNodeViewModel or ScriptsSectionNodeViewModel) return item;
        return null;
    }

    private void OnTreeDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(FolderFormat) is { } folderName)
        {
            var spot = FolderSpotAt(e);
            ClearDropTarget();
            e.Handled = true;
            if (spot is { } s) Tree?.MoveFolder(folderName, s.Folder.Name, s.After);
            return;
        }
        var scriptTarget = ScriptDropContainerAt(e.Source)?.DataContext;
        ClearDropTarget();
        if (e.DataTransfer.TryGetValue(ScriptFormat) is { } scriptPath)
        {
            e.Handled = true;
            if (Tree is { } scripts && scriptTarget is not null)
                scripts.MoveScriptNode(scriptPath, (scriptTarget as ScriptFolderNodeViewModel)?.Path);
            return;
        }
        if (scriptTarget is not null)
        {
            e.Handled = true;   // a connection dropped on the Scripts section: nothing to do
            return;
        }
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




