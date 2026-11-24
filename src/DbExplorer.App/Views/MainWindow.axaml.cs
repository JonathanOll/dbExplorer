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
        if (_bounds.Placement(_lastShownState == WindowState.Maximized) is { } placement) WindowStateStore.TrySave(file, placement);
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
        if (e.Key != Key.Enter || sender is not TreeView { SelectedItem: { } node }) return;
        e.Handled = true;
        await Safe(() => Activate(node));
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

    private void OnTreeDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(ConnectionFormat) && !IsInGroupsSection(e.Source) ? DragDropEffects.Move : DragDropEffects.None;

    /// <summary>True over the Groups section (header, group or member): a drop there must not move the connection to the root.</summary>
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
        if (IsInGroupsSection(e.Source))
        {
            e.Handled = true;
            return;
        }
        if (Tree is not { } tree || e.DataTransfer.TryGetValue(ConnectionFormat) is not { } text || !Guid.TryParse(text, out var id)) return;
        var connection = tree.Config.Connections.FirstOrDefault(c => c.Id == id);
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




