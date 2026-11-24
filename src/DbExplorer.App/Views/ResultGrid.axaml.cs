using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// Read-only grid for tabular data: a list of <see cref="ResultColumn"/> and rows that are <c>object?[]</c> or
/// <see cref="RowViewModel"/>. Cells are drawn by <see cref="GridCell"/> (one TextBlock, no template) and formatted by
/// <see cref="CellFormatter"/> only when a row is realised. Setting <see cref="Features"/> switches on the table-tab
/// extras: the filter row in the headers, header-click sort, foreign-key links and the cell context menu.
/// </summary>
public partial class ResultGrid : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<ResultColumn>?> ColumnsProperty =
        AvaloniaProperty.Register<ResultGrid, IReadOnlyList<ResultColumn>?>(nameof(Columns));

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<ResultGrid, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<ITableGridActions?> FeaturesProperty =
        AvaloniaProperty.Register<ResultGrid, ITableGridActions?>(nameof(Features));

    /// <summary>Opt-in cell editing (table tabs only); a plain result grid leaves this null and stays read-only.</summary>
    public static readonly StyledProperty<ITableEditActions?> EditingProperty =
        AvaloniaProperty.Register<ResultGrid, ITableEditActions?>(nameof(Editing));

    /// <summary>
    /// Opt-in header-click sort done in memory on the rows the grid shows (numbers by value, text ignoring case, NULL last):
    /// ascending, then descending, then the original order. For small computed tables such as the Statistics tab; the table
    /// tabs sort on the server through <see cref="Features"/> and leave this off.
    /// </summary>
    public static readonly StyledProperty<bool> ClientSortProperty =
        AvaloniaProperty.Register<ResultGrid, bool>(nameof(ClientSort));

    private readonly List<HeaderParts> _headers = [];
    private int _clientSortColumn = -1;
    private bool _clientSortDescending;
    private GridCell? _contextCell;
    private ITableGridActions? _subscribed;
    private ITableEditActions? _editing;
    private bool _attached;
    private MirrorList? _mirror;
    private INotifyCollectionChanged? _watching;
    private bool _syncingFilters;
    private int? _pendingColumn;   // ScrollToColumn asked while detached: applied on attach

    public ResultGrid()
    {
        InitializeComponent();
        Grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
        Grid.AddHandler(PointerPressedEvent, OnPanPressed, RoutingStrategies.Tunnel);
        Grid.AddHandler(PointerMovedEvent, OnPanMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Grid.AddHandler(PointerReleasedEvent, OnPanReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Grid.DoubleTapped += OnGridDoubleTapped;
        Grid.LoadingRow += (_, e) => e.Row.Classes.Set("odd", (e.Row.Index & 1) == 1);
        // Link cells carry a theme colour chosen when they are drawn: repaint them when the theme switches while the app runs.
        ActualThemeVariantChanged += (_, _) =>
        {
            foreach (var cell in Grid.GetVisualDescendants().OfType<GridCell>()) cell.Refresh();
        };
        Grid.BeginningEdit += OnBeginningEdit;
        Grid.CellEditEnding += OnCellEditEnding;
        Grid.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);
    }

    public IReadOnlyList<ResultColumn>? Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public bool ClientSort
    {
        get => GetValue(ClientSortProperty);
        set => SetValue(ClientSortProperty, value);
    }

    public ITableGridActions? Features
    {
        get => GetValue(FeaturesProperty);
        set => SetValue(FeaturesProperty, value);
    }

    public ITableEditActions? Editing
    {
        get => GetValue(EditingProperty);
        set => SetValue(EditingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EditingProperty) AttachEditing();
        if (change.Property == ColumnsProperty || change.Property == FeaturesProperty || change.Property == ClientSortProperty) RebuildColumns();
        else if (change.Property == ItemsSourceProperty) BindItems();
    }

    /// <summary>
    /// Scrolls to the column and selects it: its filter box gets the focus when the filter row is on, otherwise the
    /// column becomes the current one. Works on an empty grid (nothing to scroll then).
    /// </summary>
    public void ScrollToColumn(int index)
    {
        if (index < 0 || index >= Grid.Columns.Count) return;
        if (!_attached)
        {
            // Not on screen (e.g. the Structure sub-tab is selected): scrolling a detached grid does nothing useful.
            // Remember the request and apply it when the grid is shown again.
            _pendingColumn = index;
            return;
        }
        _pendingColumn = null;   // asked while on screen: an older request made while detached is stale
        var column = Grid.Columns[index];
        var item = Grid.SelectedItem ?? (ItemsSource as IList)?.Cast<object?>().FirstOrDefault();
        if (item is not null) Grid.ScrollIntoView(item, column);
        if (index < _headers.Count && _headers[index].Filter is { } filter) filter.Focus();
        else if (item is not null) Grid.CurrentColumn = column;
        FlashColumn(index);
    }

    // ---- column flash (Ctrl+L) ----

    private static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(1600);
    private DispatcherTimer? _flashTimer;
    private int _flashIndex = -1;

    /// <summary>Lights the header and the realised cells of the column for a moment, so the eye finds it after a jump.</summary>
    private void FlashColumn(int index)
    {
        ClearFlash();
        _flashIndex = index;
        ApplyFlash();
        Dispatcher.UIThread.Post(ApplyFlash, DispatcherPriority.Loaded);   // cells realised by the scroll's layout pass
        _flashTimer = new DispatcherTimer { Interval = FlashDuration };
        _flashTimer.Tick += (_, _) => ClearFlash();
        _flashTimer.Start();
    }

    private void ApplyFlash()
    {
        if (!_attached || _flashIndex < 0) return;
        foreach (var cell in Grid.GetVisualDescendants().OfType<DataGridCell>())
            cell.Classes.Set("flash", cell.Content is GridCell { } content && content.Index == _flashIndex);
        if (_flashIndex < _headers.Count) _headers[_flashIndex].Root.Classes.Set("flashHeader", true);
    }

    private void ClearFlash()
    {
        _flashTimer?.Stop();
        _flashTimer = null;
        _flashIndex = -1;
        foreach (var cell in Grid.GetVisualDescendants().OfType<DataGridCell>()) cell.Classes.Set("flash", false);
        foreach (var header in _headers) header.Root.Classes.Set("flashHeader", false);
    }

    // ---- middle-button drag: pans the grid (content follows the pointer) ----

    private Point? _panLast;

    // The DataGrid exposes no scroll API and ignores its scroll bars when they are set from code, so the pan is fed to it as
    // what it does understand: mouse-wheel input (one notch = this many pixels in DataGrid.OnPointerWheelChanged).
    private const double WheelNotchPixels = 50;

    private void OnPanPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(Grid).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonPressed) return;
        if (e.Source is Visual source && source.FindAncestorOfType<CellEditor>(includeSelf: true) is not null) return;   // text box: leave it alone
        _panLast = e.GetPosition(Grid);
        e.Pointer.Capture(Grid);
        Grid.Cursor = PanCursor ??= new Cursor(StandardCursorType.SizeAll);
        e.Handled = true;
    }

    // Created on first use (the platform's cursor factory may not exist yet when the type loads) and shared by all grids.
    private static Cursor? PanCursor;

    private void OnPanMoved(object? sender, PointerEventArgs e)
    {
        if (_panLast is not { } last || TopLevel.GetTopLevel(Grid) is not { } top) return;
        if (!e.GetCurrentPoint(Grid).Properties.IsMiddleButtonPressed) { EndPan(e.Pointer); return; }
        var position = e.GetPosition(Grid);
        var moved = position - last;
        _panLast = position;
        if (moved.X == 0 && moved.Y == 0) return;
        // Content follows the pointer: dragging down is wheeling up.
        var wheel = new PointerWheelEventArgs(Grid, e.Pointer, top, e.GetPosition(top), (ulong)Environment.TickCount64,
            e.GetCurrentPoint(Grid).Properties, KeyModifiers.None, new Vector(moved.X, moved.Y) / WheelNotchPixels)
        {
            RoutedEvent = PointerWheelChangedEvent,
        };
        Grid.RaiseEvent(wheel);
        e.Handled = true;
    }

    private void OnPanReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_panLast is null || e.InitialPressMouseButton != MouseButton.Middle) return;
        EndPan(e.Pointer);
        e.Handled = true;
    }

    private void EndPan(IPointer pointer)
    {
        _panLast = null;
        Grid.ClearValue(CursorProperty);
        if (ReferenceEquals(pointer.Captured, Grid)) pointer.Capture(null);
    }

    // ---- columns and headers ----

    private void RebuildColumns()
    {
        SubscribeToFeatures();

        _headers.Clear();
        Grid.Columns.Clear();
        _clientSortColumn = -1;
        if (Columns is not { } columns) return;

        for (var i = 0; i < columns.Count; i++)
        {
            var parts = BuildHeader(columns[i]);
            _headers.Add(parts);
            Grid.Columns.Add(new GridCellColumn(i, columns[i], Features is not null && columns[i].LinkTarget is not null)
            {
                Header = parts.Root,
                Width = new DataGridLength(Math.Clamp(columns[i].Name.Length * 9 + 48, 120, 260)),
                IsReadOnly = columns[i].ClrType == typeof(byte[]),   // binary values cannot be typed in
                CanUserSort = false,
            });
        }
        RefreshHeaderState();
    }

    /// <summary>
    /// Features (the tab view-model) outlives this view: the TabControl recreates the view on every re-selection. So the
    /// PropertyChanged subscription exists only while the grid is attached to the visual tree; otherwise every detached
    /// grid would stay rooted by the view-model's event list and keep repainting its headers.
    /// </summary>
    private void SubscribeToFeatures()
    {
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnFeaturesChanged;
        _subscribed = _attached ? Features : null;
        if (_subscribed is not null) _subscribed.PropertyChanged += OnFeaturesChanged;
    }

    /// <summary>
    /// The DataGrid never releases its CollectionChanged subscription on the collection it was given (even after
    /// ItemsSource = null), so it is handed a private mirror owned by this control instead of the view-model's
    /// long-lived collection. The mirror follows the source only while attached.
    /// </summary>
    private void BindItems()
    {
        if (_watching is not null) _watching.CollectionChanged -= OnSourceChanged;
        _watching = null;
        _mirror = null;
        Grid.ItemsSource = null;
        if (!_attached || ItemsSource is not { } source) return;

        _mirror = new MirrorList(source);
        _clientSortColumn = -1;
        RefreshClientSortGlyph();
        if (source is INotifyCollectionChanged notifying)
        {
            _watching = notifying;
            notifying.CollectionChanged += OnSourceChanged;
        }
        Grid.ItemsSource = _mirror;
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_mirror is null || ItemsSource is not { } source) return;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null && e.NewStartingIndex >= 0:
                for (var i = 0; i < e.NewItems.Count; i++) _mirror.Insert(e.NewStartingIndex + i, e.NewItems[i]!);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null && e.OldStartingIndex >= 0:
                for (var i = 0; i < e.OldItems.Count; i++) _mirror.RemoveAt(e.OldStartingIndex);
                break;
            default:
                _mirror.Reset(source);
                break;
        }
        ApplyClientSort();
    }

    // ---- client-side sort (opt-in through ClientSort) ----

    private void ToggleClientSort(int column)
    {
        if (_clientSortColumn != column) { _clientSortColumn = column; _clientSortDescending = false; }
        else if (!_clientSortDescending) _clientSortDescending = true;
        else _clientSortColumn = -1;
        ApplyClientSort();
        RefreshClientSortGlyph();
    }

    private void ApplyClientSort()
    {
        if (_mirror is null || ItemsSource is not { } source) return;
        var rows = source.Cast<object>().ToList();
        if (_clientSortColumn >= 0)
        {
            var column = _clientSortColumn;
            var sign = _clientSortDescending ? -1 : 1;
            object? ValueOf(object row) => GridCellColumn.ValuesOf(row) is { } v && column < v.Length ? v[column] : null;
            rows = rows.Order(Comparer<object>.Create((a, b) =>
            {
                var (x, y) = (ValueOf(a), ValueOf(b));
                if (x is null || y is null) return x is null ? (y is null ? 0 : 1) : -1;   // NULL last, whatever the direction
                return sign * CompareValues(x, y);
            })).ToList();
        }
        _mirror.ReplaceWith(rows);
    }

    private static int CompareValues(object x, object y)
    {
        if (x is long a && y is long b) return a.CompareTo(b);
        if (x is IConvertible && y is IConvertible && CellFormatter.Format(x).IsNumber && CellFormatter.Format(y).IsNumber)
            return Convert.ToDecimal(x, System.Globalization.CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDecimal(y, System.Globalization.CultureInfo.InvariantCulture));
        return string.Compare(CellFormatter.Format(x).Text, CellFormatter.Format(y).Text, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshClientSortGlyph()
    {
        if (!ClientSort || Features is not null) return;
        for (var i = 0; i < _headers.Count; i++)
        {
            var sorted = i == _clientSortColumn;
            _headers[i].SortGlyph.IsVisible = sorted;
            if (sorted) _headers[i].SortGlyph.Data = Icons.Find(_clientSortDescending ? "IconSortDown" : "IconSortUp");
        }
    }

    private sealed class MirrorList : ObservableCollection<object>
    {
        public MirrorList(IEnumerable source) : base(source.Cast<object>()) { }

        public void ReplaceWith(IReadOnlyList<object> rows)
        {
            if (rows.Count == Items.Count && rows.Zip(Items).All(p => ReferenceEquals(p.First, p.Second))) return;
            Items.Clear();
            foreach (var row in rows) Items.Add(row);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public void Reset(IEnumerable source)
        {
            Items.Clear();
            foreach (var item in source) Items.Add(item);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        ClearFlash();
        if (_panLast is not null)
        {
            _panLast = null;   // detached mid-pan: no release will reach us, so the pan cursor must not stay
            Grid.ClearValue(CursorProperty);
        }
        SubscribeToFeatures();
        AttachEditing();
        BindItems();
        base.OnDetachedFromVisualTree(e);
    }

    private HeaderParts BuildHeader(ResultColumn column)
    {
        // Two stacked bands: the column name (darker header) and, in table tabs, its filter box (lighter band under it).
        var title = new TextBlock
        {
            Text = column.Name, FontFamily = FontFamily.Default, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var left = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        if (column.IsPrimaryKey) left.Children.Add(HeaderIcon("IconKey", "pk"));
        if (column.LinkTarget is not null) left.Children.Add(HeaderIcon("IconLink", "fk"));
        left.Children.Add(title);

        var glyph = HeaderIcon("IconSortUp", "sortGlyph");
        glyph.IsVisible = false;
        glyph.Width = glyph.Height = 14;
        var line = new Avalonia.Controls.Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        line.Children.Add(left);
        Avalonia.Controls.Grid.SetColumn(glyph, 1);
        line.Children.Add(glyph);

        var nameBand = new Border { Child = line };
        nameBand.Classes.Add("nameBand");
        var root = new StackPanel();
        var tip = column.TypeName is { Length: > 0 } type ? $"{column.Name} ({type})" : column.Name;
        if (column.IsPrimaryKey) tip += "\nPrimary key";
        if (column.LinkTarget is not null) tip += $"\nForeign key → {column.LinkTarget} (Ctrl+click or double-click a value to open it)";
        ToolTip.SetTip(nameBand, tip);
        root.Children.Add(nameBand);

        TextBox? filter = null;
        if (ClientSort && Features is null)
        {
            var index = _headers.Count;   // the header is added to the list right after this call
            nameBand.Background = Brushes.Transparent;
            nameBand.Cursor = new Cursor(StandardCursorType.Hand);
            nameBand.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(nameBand).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                ToggleClientSort(index);
            };
        }
        if (Features is { } features)
        {
            // Clicking the name sorts server-side; the built-in client-side sort is off.
            nameBand.Background = Brushes.Transparent;
            nameBand.Cursor = new Cursor(StandardCursorType.Hand);
            nameBand.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(nameBand).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                _ = RunSafe(() => features.ToggleSortAsync(column.Name));
            };

            filter = new TextBox { Watermark = "filter…", Text = features.GetFilter(column.Name) };
            ToolTip.SetTip(filter,
                "text: contains  ·  =v  !=v  <v  >v  <=v  >=v  ·  null  !null  ·  in (a, b)\n" +
                "Enter applies. Note: != does not match NULL rows (standard SQL); use !null / null for those.");
            filter.TextChanged += (_, _) =>
            {
                if (!_syncingFilters) features.SetFilter(column.Name, filter.Text ?? "");
            };
            filter.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                features.SetFilter(column.Name, filter.Text ?? "");
                _ = RunSafe(features.ApplyFiltersAsync);
            };
            var band = new Border { Child = filter };
            band.Classes.Add("filterBand");
            root.Children.Add(band);
        }

        return new HeaderParts(column, root, glyph, filter);
    }

    // Small vector icon of a header; its colour comes from a style class (ResultGrid.axaml), so it follows the theme.
    private static PathIcon HeaderIcon(string key, string cssClass)
    {
        var icon = new PathIcon { Width = 11, Height = 11, Data = Icons.Find(key), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        icon.Classes.Add(cssClass);
        return icon;
    }

    private void OnFeaturesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ITableGridActions.Sort) or "ColumnFilters" or null) RefreshHeaderState();
    }

    private void RefreshHeaderState()
    {
        if (Features is not { } features) return;
        var sort = features.Sort;
        _syncingFilters = true;
        try
        {
            foreach (var header in _headers)
            {
                var sorted = sort is not null && string.Equals(sort.Column, header.Column.Name, StringComparison.OrdinalIgnoreCase);
                header.SortGlyph.IsVisible = sorted;
                if (sorted) header.SortGlyph.Data = Icons.Find(sort!.Descending ? "IconSortDown" : "IconSortUp");
                var text = features.GetFilter(header.Column.Name);
                if (header.Filter is { } box && (box.Text ?? "") != text) box.Text = text;
            }
        }
        finally
        {
            _syncingFilters = false;
        }
    }

    // ---- cell interaction (table features only) ----

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var cell = CellAt(e.Source);
        var point = e.GetCurrentPoint(Grid);
        if (point.Properties.IsRightButtonPressed) _contextCell = cell;
        if (Features is null || cell is null || !cell.IsLink || !point.Properties.IsLeftButtonPressed) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        e.Handled = true;
        _ = FollowAsync(cell);
    }

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Features is not null && CellAt(e.Source) is { IsLink: true } cell) _ = FollowAsync(cell);
    }

    private Task FollowAsync(GridCell cell) =>
        RunSafe(async () =>
        {
            if (Features is { } features && RowIndexOf(cell) is >= 0 and var row)
                await features.FollowForeignKeyAsync(row, cell.ColumnName);
        });

    private static GridCell? CellAt(object? source) => (source as Visual)?.FindAncestorOfType<GridCell>(includeSelf: true);

    private int RowIndexOf(GridCell cell) => (ItemsSource as IList)?.IndexOf(cell.Row) ?? -1;

    private static async Task RunSafe(Func<Task> action)
    {
        try { await action(); }
        catch (Exception) { /* view-model methods report their own failures; a click must never crash the app */ }
    }

    // ---- cell editing (opt-in through Editing) ----

    private void AttachEditing()
    {
        if (_editing is not null)
        {
            _editing.PropertyChanged -= OnEditingChanged;
            _editing.RowChanged -= OnRowChanged;
        }
        _editing = _attached ? Editing : null;   // like Features: the view-model outlives the view, so only subscribe while attached
        if (_editing is not null)
        {
            _editing.PropertyChanged += OnEditingChanged;
            _editing.RowChanged += OnRowChanged;
        }
        UpdateReadOnly();
    }

    private void OnEditingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ITableEditActions.CanEdit) or nameof(ITableEditActions.CanInsert) or null) UpdateReadOnly();
    }

    // The grid is editable when loaded rows can be changed (a key exists) or new rows can be added (no key needed);
    // which of the two applies to a given row is decided in OnBeginningEdit.
    private void UpdateReadOnly() => Grid.IsReadOnly = _editing is not { } e || !(e.CanEdit || e.CanInsert);

    /// <summary>Loaded rows need <c>CanEdit</c> (a key); a new row only needs <c>CanInsert</c>.</summary>
    private bool MayEdit(object? row) => _editing is { } e && (row is RowViewModel { IsNew: true } ? e.CanInsert : e.CanEdit);

    /// <summary>A staged edit changed a row: repaint its realised cells (the grid does not know values changed).</summary>
    private void OnRowChanged(RowViewModel row)
    {
        foreach (var cell in Grid.GetVisualDescendants().OfType<GridCell>())
            if (ReferenceEquals(cell.Row, row)) cell.Refresh();
    }

    private void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (!MayEdit(e.Row.DataContext) || e.Row.DataContext is RowViewModel { IsDeleted: true }) e.Cancel = true;
    }

    /// <summary>
    /// Commit goes through the view-model, which validates the text. A refused value keeps the editor open with the
    /// reason on the box and nothing staged; Esc (Cancel) just closes the editor.
    /// </summary>
    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (_editing is not { } editing) return;
        if (e.EditAction == DataGridEditAction.Cancel)
        {
            editing.DismissCellError();
            return;
        }
        if (e.EditingElement is not CellEditor editor || e.Column is not GridCellColumn column) return;
        if (e.Row.DataContext is not RowViewModel row || ItemsSource is not IList list || list.IndexOf(row) is not (>= 0 and var rowIndex)) return;

        var text = editor.Text ?? "";
        if (!editor.SetNull && text == editor.InitialText)
        {
            editing.DismissCellError();
            return;   // nothing typed: stage nothing
        }

        if (editing.SetCell(rowIndex, column.Index, editor.SetNull ? null : text))
        {
            editor.ClearValue(BorderBrushProperty);
            return;
        }
        e.Cancel = true;
        editor.SetNull = false;
        editor.BorderBrush = Brushes.IndianRed;
        ToolTip.SetTip(editor, editing.CellError);
    }

    // Ctrl+0: Set NULL on the cell being edited, or on the current cell.
    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control || e.Key is not (Key.D0 or Key.NumPad0) || _editing is not { } ed || !(ed.CanEdit || ed.CanInsert)) return;
        e.Handled = true;
        if (e.Source is CellEditor editor)
        {
            editor.SetNull = true;
            Grid.CommitEdit();
        }
        else if (Grid.SelectedItem is RowViewModel row && MayEdit(row) && Grid.CurrentColumn is GridCellColumn column
                 && ItemsSource is IList list && list.IndexOf(row) is >= 0 and var index)
        {
            _editing.SetCell(index, column.Index, null);
        }
    }

    /// <summary>
    /// Ends a cell edit in progress, staging its text (used before Save so a half-typed value is not lost). False when the
    /// text was refused (invalid): the editor stays open and the caller must not go on as if the value were staged.
    /// </summary>
    public bool CommitPendingEdit() => Grid.CommitEdit();

    /// <summary>The rows selected in the grid.</summary>
    public IReadOnlyList<RowViewModel> SelectedRows => Grid.SelectedItems.OfType<RowViewModel>().ToList();

    /// <summary>Selects and scrolls to a row (e.g. the one just added).</summary>
    public void SelectRow(int index)
    {
        if (ItemsSource is not IList list || index < 0 || index >= list.Count) return;
        Grid.SelectedIndex = index;
        Grid.ScrollIntoView(list[index], Grid.Columns.FirstOrDefault());
    }

    /// <summary>
    /// Selects and scrolls to a row just added and puts its first editable cell in edit mode, so the user can type right
    /// away. Generated keys are left alone: the first column that is not a single-column primary key (the database
    /// fills those in; the schema does not say whether it is an identity) and not binary. Runs after the layout that
    /// follows the scroll, when the row is realised.
    /// </summary>
    public void StartEditingNewRow(int index)
    {
        SelectRow(index);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_attached || ItemsSource is not IList list || index >= list.Count || FirstEditableColumn() is not { } column) return;
            if (!ReferenceEquals(Grid.SelectedItem, list[index])) return;   // the selection moved on meanwhile: leave it alone
            Grid.CurrentColumn = column;
            Grid.BeginEdit();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private GridCellColumn? FirstEditableColumn()
    {
        var pkCount = Columns?.Count(c => c.IsPrimaryKey) ?? 0;
        var cells = Grid.Columns.OfType<GridCellColumn>().Where(c => !c.IsReadOnly).ToList();
        bool Generated(GridCellColumn c) => pkCount == 1 && Columns is { } cols && c.Index < cols.Count && cols[c.Index].IsPrimaryKey;
        return cells.FirstOrDefault(c => !Generated(c)) ?? cells.FirstOrDefault();
    }

    // The context menu is built once; its items read the cell that was right-clicked.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        SubscribeToFeatures();
        AttachEditing();
        BindItems();
        RefreshHeaderState();   // changes made while detached
        if (_pendingColumn is { } pending)
        {
            _pendingColumn = null;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ScrollToColumn(pending), Avalonia.Threading.DispatcherPriority.Loaded);   // after the first layout
        }
        if (Grid.ContextMenu is not null) return;

        var filterItem = new MenuItem { Header = "Filter by this value" };
        filterItem.Click += (_, _) =>
        {
            if (_contextCell is { } c && Features is { } f && RowIndexOf(c) is >= 0 and var row)
                _ = RunSafe(() => f.FilterByCellAsync(row, c.ColumnName));
        };
        var followItem = new MenuItem { Header = "Follow foreign key" };
        followItem.Click += (_, _) =>
        {
            if (_contextCell is { } c) _ = FollowAsync(c);
        };
        var copyItem = new MenuItem { Header = "Copy value" };
        copyItem.Click += async (_, _) =>
        {
            if (_contextCell is not { } c || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            var text = GridCellColumn.ValuesOf(c.Row) is { } values && c.Index < values.Length && values[c.Index] is { } v
                ? CopyText(v)
                : "";
            await RunSafe(() => clipboard.SetTextAsync(text));
        };

        var editItem = new MenuItem { Header = "Edit cell", InputGesture = new KeyGesture(Key.F2) };
        editItem.Click += (_, _) =>
        {
            if (_contextCell is not { } c || c.Row is not RowViewModel) return;
            Grid.SelectedItem = c.Row;
            if (c.Index < Grid.Columns.Count) Grid.CurrentColumn = Grid.Columns[c.Index];
            Grid.BeginEdit();
        };
        var nullItem = new MenuItem { Header = "Set NULL", InputGesture = new KeyGesture(Key.D0, KeyModifiers.Control) };
        nullItem.Click += (_, _) =>
        {
            if (_contextCell is { } c && _editing is { } editing && RowIndexOf(c) is >= 0 and var row)
                editing.SetCell(row, c.Index, null);
        };
        var deleteItem = new MenuItem { Header = "Delete row" };
        deleteItem.Click += (_, _) =>
        {
            if (_contextCell is { } c && _editing is { } editing && RowIndexOf(c) is >= 0 and var row) editing.DeleteRow(row);
        };
        var restoreItem = new MenuItem { Header = "Restore row" };
        restoreItem.Click += (_, _) =>
        {
            if (_contextCell is { } c && _editing is { } editing && RowIndexOf(c) is >= 0 and var row) editing.UndeleteRow(row);
        };
        var addItem = new MenuItem { Header = "Add row" };
        addItem.Click += (_, _) =>
        {
            if (_editing is { } editing && editing.AddRow() is >= 0 and var index) StartEditingNewRow(index);
        };
        var editSeparator = new Separator();

        var menu = new ContextMenu();
        menu.Items.Add(filterItem);
        menu.Items.Add(followItem);
        menu.Items.Add(copyItem);
        menu.Items.Add(editSeparator);
        menu.Items.Add(editItem);
        menu.Items.Add(nullItem);
        menu.Items.Add(deleteItem);
        menu.Items.Add(restoreItem);
        menu.Items.Add(addItem);
        menu.Opening += (_, e2) =>
        {
            if (_contextCell is null) { e2.Cancel = true; return; }
            var table = Features is not null;
            filterItem.IsVisible = table;
            followItem.IsVisible = table && _contextCell.IsLink;
            followItem.Header = _contextCell.LinkTarget is { } target ? $"Follow foreign key → {target}" : "Follow foreign key";
            var hasValue = GridCellColumn.ValuesOf(_contextCell.Row) is { } vs && _contextCell.Index < vs.Length && vs[_contextCell.Index] is not null;
            followItem.IsEnabled = hasValue;

            var canEdit = MayEdit(_contextCell.Row);   // a new row of a keyless table is editable, its loaded rows are not
            var deleted = _contextCell.Row is RowViewModel { IsDeleted: true };
            var binary = _contextCell.Index < Grid.Columns.Count && Grid.Columns[_contextCell.Index].IsReadOnly;
            editSeparator.IsVisible = canEdit;
            editItem.IsVisible = canEdit;
            editItem.IsEnabled = !deleted && !binary;
            nullItem.IsVisible = canEdit;
            nullItem.IsEnabled = !deleted;
            deleteItem.IsVisible = canEdit && !deleted;
            restoreItem.IsVisible = canEdit && deleted;
            addItem.IsVisible = _editing?.CanInsert == true;
        };
        Grid.ContextMenu = menu;
    }

    /// <summary>The complete value (the cell may show a shortened form).</summary>
    private static string CopyText(object value) => value switch
    {
        string s => s,
        byte[] b => "0x" + Convert.ToHexString(b),
        _ => CellFormatter.Format(value).Text,
    };

    private sealed record HeaderParts(ResultColumn Column, Control Root, PathIcon SortGlyph, TextBox? Filter);
}
