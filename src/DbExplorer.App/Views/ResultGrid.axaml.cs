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

    /// <summary>The filter boxes under the column names (only with <see cref="Features"/>); hiding them saves a row of height.</summary>
    public static readonly StyledProperty<bool> ShowFilterRowProperty =
        AvaloniaProperty.Register<ResultGrid, bool>(nameof(ShowFilterRow), defaultValue: true);

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
    private readonly Dictionary<int, double> _autoWidths = [];   // widths SizeUntypedColumns gave (to tell them from a user's resize)
    private ContextMenu? _headerMenu;
    private HeaderParts? _headerMenuTarget;
    private GridCell? _hoverCell;      // the cell under the pointer (Features only)
    private GridCell? _litLink;        // the link cell currently underlined

    public ResultGrid()
    {
        InitializeComponent();
        Grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
        Grid.AddHandler(PointerPressedEvent, OnPanPressed, RoutingStrategies.Tunnel);
        Grid.AddHandler(PointerMovedEvent, OnPanMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Grid.AddHandler(PointerMovedEvent, OnLinkPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        Grid.AddHandler(PointerExitedEvent, OnGridPointerExited, RoutingStrategies.Tunnel, handledEventsToo: true);
        Grid.AddHandler(PointerReleasedEvent, OnPanReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Grid.DoubleTapped += OnGridDoubleTapped;
        Grid.LoadingRow += (_, e) => e.Row.Classes.Set("odd", (e.Row.Index & 1) == 1);
        // Cells carry theme brushes chosen when they are drawn: repaint them when the theme switches while the app runs.
        ActualThemeVariantChanged += (_, _) =>
        {
            foreach (var cell in Grid.GetVisualDescendants().OfType<GridCell>()) cell.Refresh();
        };
        Grid.BeginningEdit += OnBeginningEdit;
        Grid.CellEditEnding += OnCellEditEnding;
        Grid.SelectionChanged += (_, _) => SelectedRowsChanged?.Invoke(this, EventArgs.Empty);
        Grid.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);
        Grid.AddHandler(KeyUpEvent, OnGridKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
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

    public bool ShowFilterRow
    {
        get => GetValue(ShowFilterRowProperty);
        set => SetValue(ShowFilterRowProperty, value);
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

    /// <summary>Raised when the set of selected rows changed (the table tab shows the count and labels its delete button).</summary>
    public event EventHandler? SelectedRowsChanged;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EditingProperty) AttachEditing();
        if (change.Property == ColumnsProperty || change.Property == FeaturesProperty || change.Property == ClientSortProperty) RebuildColumns();
        else if (change.Property == ItemsSourceProperty) BindItems();
        else if (change.Property == ShowFilterRowProperty) ApplyFilterRowVisibility();
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
        if (index < _headers.Count && _headers[index].Filter is { } filter && ShowFilterRow) filter.Focus();
        else if (item is not null) Grid.CurrentColumn = column;
        FlashColumn(index);
    }

    /// <summary>Puts the focus in the first column's filter box (Ctrl+Shift+F); false when there is no filter row to focus.</summary>
    public bool FocusFirstFilter()
    {
        if (!ShowFilterRow || _headers.FirstOrDefault(h => h.Filter is not null)?.Filter is not { } box) return false;
        box.Focus();
        box.SelectAll();
        return true;
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

    /// <summary>
    /// First width of a column, from its type (the rows are not known yet when the columns are built): a date must never be cut,
    /// a number does not need more than its digits, a flag needs very little. The name sets the floor. Result sets without type
    /// information keep a width from the name alone.
    /// </summary>
    internal static double DefaultWidth(ResultColumn column)
    {
        var name = column.Name.Length * 8.0 + (column.IsPrimaryKey ? 16 : 0) + (column.LinkTarget is not null ? 16 : 0) + 40;
        if (column.ClrType is null && column.TypeName is null) return Math.Clamp(column.Name.Length * 9 + 48, 120, 260);
        var data = column.IsGuid ? 290
            : column.IsBoolean ? 84
            : column.IsTemporal ? (column.TypeName?.Contains("TIME", StringComparison.OrdinalIgnoreCase) == true || column.ClrType == typeof(DateTime) || column.ClrType == typeof(DateTimeOffset) ? 176 : 124)
            : column.IsNumeric ? 112
            : 180;
        return Math.Clamp(Math.Max(name, data), 96, 300);
    }

    private void RebuildColumns()
    {
        SubscribeToFeatures();

        _headers.Clear();
        _autoWidths.Clear();
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
                Width = new DataGridLength(DefaultWidth(columns[i])),
                MinWidth = 64,
                IsReadOnly = columns[i].ClrType == typeof(byte[]),   // binary values cannot be typed in
                CanUserSort = false,
            });
        }
        ApplyFilterRowVisibility();
        RefreshHeaderState();
        SizeUntypedColumns();
    }

    /// <summary>
    /// A result set carries no column types, so its first widths come from the name alone and cut dates and long names. Once
    /// there are rows, widen those columns to the longest value among the first rows (never past a cap).
    /// </summary>
    private void SizeUntypedColumns()
    {
        if (Columns is not { } columns || ItemsSource is not IEnumerable source) return;
        List<int> untyped = [];
        for (var i = 0; i < columns.Count && i < Grid.Columns.Count; i++)
            if (columns[i].ClrType is null && columns[i].TypeName is null) untyped.Add(i);
        if (untyped.Count == 0) return;

        var longest = new int[columns.Count];
        var seen = 0;
        foreach (var row in source)
        {
            if (seen++ >= 40) break;
            if (GridCellColumn.ValuesOf(row) is not { } values) continue;
            foreach (var i in untyped)
                if (i < values.Length) longest[i] = Math.Max(longest[i], Math.Min(CellFormatter.Format(values[i]).Text.Length, CellFormatter.MaxCellChars));
        }
        if (seen == 0) return;
        foreach (var i in untyped)
        {
            // A column the user has resized since is left alone.
            var current = Grid.Columns[i].Width;
            var untouched = current.IsAbsolute && (Math.Abs(current.Value - DefaultWidth(columns[i])) < 0.5
                || (_autoWidths.TryGetValue(i, out var previous) && Math.Abs(current.Value - previous) < 0.5));
            if (!untouched) continue;
            var width = Math.Clamp(Math.Max(DefaultWidth(columns[i]), longest[i] * 7.2 + 24), 96, 380);
            _autoWidths[i] = width;
            Grid.Columns[i].Width = new DataGridLength(width);
        }
    }

    private void ApplyFilterRowVisibility()
    {
        foreach (var header in _headers)
            if (header.Band is { } band) band.IsVisible = ShowFilterRow;
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
        SizeUntypedColumns();
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
            _headers[i].Title.Classes.Set("sorted", sorted);
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
        ClearLinkHover();
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
        // Two stacked bands: the column name (header) and, in table tabs, its filter box (lighter band under it).
        // Numbers are right-aligned in the cells, so their name is too.
        var title = new TextBlock
        {
            Text = column.Name, FontFamily = FontFamily.Default, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };
        title.Classes.Add("colTitle");

        var glyph = HeaderIcon("IconSortUp", "sortGlyph");
        glyph.IsVisible = false;
        glyph.Width = glyph.Height = 14;
        var filterMark = HeaderIcon("IconFilter", "filterMark");
        filterMark.IsVisible = false;
        filterMark.Margin = new Thickness(4, 0, 0, 0);

        // Text columns: key and link icons, then the name filling the rest. Number columns: an empty first cell pushes the icons and the
        // name to the right edge, so the name sits over the digits and the icons stay next to it.
        var offset = column.IsNumeric ? 1 : 0;
        var line = new Avalonia.Controls.Grid { ColumnDefinitions = new ColumnDefinitions(column.IsNumeric ? "*,Auto,Auto,Auto,Auto,Auto" : "Auto,Auto,*,Auto,Auto") };
        if (column.IsPrimaryKey) AddTo(line, HeaderIcon("IconKey", "pk"), offset, new Thickness(0, 0, 4, 0));
        if (column.LinkTarget is not null) AddTo(line, HeaderIcon("IconLink", "fk"), offset + 1, new Thickness(0, 0, 4, 0));
        if (column.IsNumeric) title.MaxWidth = Math.Max(40, DefaultWidth(column) - 70);   // an Auto cell does not trim: cap the name instead
        AddTo(line, title, offset + 2, default);
        AddTo(line, filterMark, offset + 3, default);
        AddTo(line, glyph, offset + 4, new Thickness(4, 0, 0, 0));

        var nameBand = new Border { Child = line };
        nameBand.Classes.Add("nameBand");
        var root = new StackPanel();
        var tip = column.TypeName is { Length: > 0 } type ? $"{column.Name} ({type})" : column.Name;
        if (column.IsPrimaryKey) tip += "\nPrimary key";
        if (column.LinkTarget is not null) tip += $"\nForeign key → {column.LinkTarget} (Ctrl+click or double-click a value to open it)";
        if (Features is not null) tip += "\nClick to sort (ascending, descending, none). Right-click for more.";
        ToolTip.SetTip(nameBand, tip);
        Avalonia.Automation.AutomationProperties.SetName(nameBand, Features is not null ? $"Sort by {column.Name}" : column.Name);
        root.Children.Add(nameBand);

        TextBox? filter = null;
        Border? band = null;
        Button? clear = null;
        HeaderParts? parts = null;
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
            nameBand.ContextRequested += (_, e) =>
            {
                if (parts is null) return;
                e.Handled = true;
                OpenHeaderMenu(parts, nameBand);
            };

            filter = new TextBox { Watermark = FilterHint(column), Text = features.GetFilter(column.Name) };
            Avalonia.Automation.AutomationProperties.SetName(filter, $"Filter {column.Name}");
            ToolTip.SetTip(filter,
                "text: contains  ·  =v  !=v  <v  >v  <=v  >=v  ·  null  !null  ·  in (a, b)\n" +
                "Enter applies, Esc clears. Note: != does not match NULL rows (standard SQL); use !null / null for those.");
            clear = new Button
            {
                Classes = { "ghost" }, Focusable = false, IsVisible = !string.IsNullOrEmpty(filter.Text), Width = 18, Height = 18, MinHeight = 0,
                Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Content = new PathIcon { Data = Icons.Find("IconClose"), Width = 8, Height = 8 },
            };
            ToolTip.SetTip(clear, "Clear this filter");
            Avalonia.Automation.AutomationProperties.SetName(clear, $"Clear the filter on {column.Name}");
            filter.InnerRightContent = clear;
            filter.TextChanged += (_, _) =>
            {
                clear.IsVisible = !string.IsNullOrEmpty(filter.Text);
                if (_syncingFilters) return;
                features.SetFilter(column.Name, filter.Text ?? "");
                MarkFilter(filter, features, column.Name);
            };
            clear.Click += (_, _) =>
            {
                filter.Text = "";
                features.SetFilter(column.Name, "");
                _ = RunSafe(features.ApplyFiltersAsync);
            };
            filter.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && !string.IsNullOrEmpty(filter.Text))
                {
                    e.Handled = true;
                    var wasApplied = features.GetAppliedFilter(column.Name).Length > 0;
                    filter.Text = "";
                    features.SetFilter(column.Name, "");
                    if (wasApplied) _ = RunSafe(features.ApplyFiltersAsync);
                    return;
                }
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                features.SetFilter(column.Name, filter.Text ?? "");
                _ = RunSafe(features.ApplyFiltersAsync);
            };
            band = new Border { Child = filter };
            band.Classes.Add("filterBand");
            root.Children.Add(band);
        }

        parts = new HeaderParts(column, root, glyph, filter, title, filterMark, band);
        return parts;
    }

    private static void AddTo(Avalonia.Controls.Grid grid, Control child, int column, Thickness margin)
    {
        if (margin != default) child.Margin = margin;
        Avalonia.Controls.Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    // What an empty filter box suggests, by the kind of column: the syntax is the one in the tooltip.
    private static string FilterHint(ResultColumn column) =>
        column.IsNumeric ? ">10" : column.IsBoolean ? "true / false" : column.IsTemporal ? ">=2025-01-01" : "contains…";

    // A filter box is lit while its text is what the rows were loaded with, and flagged while it is typed but not applied yet.
    private static void MarkFilter(TextBox box, ITableGridActions features, string column)
    {
        var applied = features.GetAppliedFilter(column);
        var text = box.Text ?? "";
        box.Classes.Set("active", applied.Length > 0 && text == applied);
        box.Classes.Set("dirty", text != applied);
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
                header.Title.Classes.Set("sorted", sorted);
                if (sorted) header.SortGlyph.Data = Icons.Find(sort!.Descending ? "IconSortDown" : "IconSortUp");
                var text = features.GetFilter(header.Column.Name);
                if (header.Filter is { } box)
                {
                    if ((box.Text ?? "") != text) box.Text = text;
                    MarkFilter(box, features, header.Column.Name);
                    header.FilterMark.IsVisible = features.GetAppliedFilter(header.Column.Name).Length > 0;
                }
            }
        }
        finally
        {
            _syncingFilters = false;
        }
    }

    // ---- header context menu (table tabs) ----

    private void OpenHeaderMenu(HeaderParts header, Control anchor)
    {
        if (Features is not { } features) return;
        _headerMenuTarget = header;
        if (_headerMenu is null)
        {
            var asc = new MenuItem { Header = "Sort ascending" };
            asc.Click += (_, _) => { if (_headerMenuTarget is { } h) _ = RunSafe(() => features.SortAsync(h.Column.Name, false)); };
            var desc = new MenuItem { Header = "Sort descending" };
            desc.Click += (_, _) => { if (_headerMenuTarget is { } h) _ = RunSafe(() => features.SortAsync(h.Column.Name, true)); };
            var none = new MenuItem { Header = "Clear sort" };
            none.Click += (_, _) => { if (_headerMenuTarget is { } h) _ = RunSafe(() => features.SortAsync(h.Column.Name, null)); };
            var clearFilter = new MenuItem { Header = "Clear filter" };
            clearFilter.Click += (_, _) => { if (_headerMenuTarget is { } h) _ = RunSafe(() => features.ClearFilterAsync(h.Column.Name)); };
            var copyName = new MenuItem { Header = "Copy column name" };
            copyName.Click += async (_, _) =>
            {
                if (_headerMenuTarget is { } h && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                    await RunSafe(() => clipboard.SetTextAsync(h.Column.Name));
            };
            var fit = new MenuItem { Header = "Fit column to its content" };
            fit.Click += (_, _) =>
            {
                if (_headerMenuTarget is { } h && _headers.IndexOf(h) is >= 0 and var i && i < Grid.Columns.Count) Grid.Columns[i].Width = DataGridLength.Auto;
            };
            var fitAll = new MenuItem { Header = "Fit all columns to their content" };
            fitAll.Click += (_, _) =>
            {
                foreach (var column in Grid.Columns) column.Width = DataGridLength.Auto;
            };
            _headerMenu = new ContextMenu();
            foreach (var item in new Control[] { asc, desc, none, new Separator(), clearFilter, copyName, new Separator(), fit, fitAll }) _headerMenu.Items.Add(item);
            _headerMenu.Opening += (_, _) =>
            {
                var f = Features;
                var target = _headerMenuTarget;
                if (f is null || target is null) return;
                var sorted = f.Sort is { } s && string.Equals(s.Column, target.Column.Name, StringComparison.OrdinalIgnoreCase);
                none.IsVisible = sorted;
                clearFilter.IsEnabled = f.GetFilter(target.Column.Name).Length > 0;
            };
        }
        _headerMenu.Open(anchor);
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

    // Ctrl over a foreign-key value: underline + hand cursor (the value follows on Ctrl+click; the tooltip says so too).
    private void OnLinkPointerMoved(object? sender, PointerEventArgs e)
    {
        if (Features is null) return;
        _hoverCell = CellAt(e.Source);
        UpdateLinkHover(e.KeyModifiers.HasFlag(KeyModifiers.Control));
    }

    private void OnGridPointerExited(object? sender, PointerEventArgs e)
    {
        _hoverCell = null;
        ClearLinkHover();
    }

    private void OnGridKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) UpdateLinkHover(false);
    }

    private void UpdateLinkHover(bool ctrl)
    {
        var target = ctrl && _hoverCell is { IsLink: true } c ? c : null;
        if (ReferenceEquals(target, _litLink)) return;
        ClearLinkHover();
        if (target is null) return;
        target.SetLinkHover(true);
        _litLink = target;
    }

    private void ClearLinkHover()
    {
        _litLink?.SetLinkHover(false);
        _litLink = null;
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
    /// reason under the box and nothing staged; Esc (Cancel) just closes the editor.
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
            ClearEditorError(editor);
            return;
        }
        e.Cancel = true;
        editor.SetNull = false;
        editor.Classes.Add("error");
        ToolTip.SetTip(editor, editing.CellError);
        ToolTip.SetIsOpen(editor, true);   // the reason appears right under the cell, not only in the banner
        editor.TextChanged -= OnEditorTextChanged;
        editor.TextChanged += OnEditorTextChanged;
    }

    private static void OnEditorTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is CellEditor editor) ClearEditorError(editor);
    }

    private static void ClearEditorError(CellEditor editor)
    {
        editor.Classes.Remove("error");
        ToolTip.SetIsOpen(editor, false);
        editor.ClearValue(ToolTip.TipProperty);
    }

    // Grid keys that are not the DataGrid's own: Ctrl+0 (NULL), Delete / Insert (rows), Ctrl+C and Ctrl+Shift+C (copy), Alt+Enter (view value).
    // None of them applies while a text box (cell editor, filter) has the keyboard: there the keys keep their text meaning.
    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl && Features is not null) UpdateLinkHover(true);
        if (e.Source is TextBox && e.Source is not CellEditor) return;
        var editing = _editing;
        var canWrite = editing is not null && (editing.CanEdit || editing.CanInsert);

        if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.D0 or Key.NumPad0)
        {
            if (!canWrite) return;
            e.Handled = true;
            if (e.Source is CellEditor editor)
            {
                editor.SetNull = true;
                Grid.CommitEdit();
            }
            else if (Grid.SelectedItem is RowViewModel row && MayEdit(row) && Grid.CurrentColumn is GridCellColumn column
                     && ItemsSource is IList list && list.IndexOf(row) is >= 0 and var index)
            {
                editing!.SetCell(index, column.Index, null);
            }
            return;
        }
        if (e.Source is CellEditor) return;

        switch (e.Key, e.KeyModifiers)
        {
            case (Key.Delete, KeyModifiers.None) when canWrite:
                e.Handled = true;
                DeleteSelectedRows();
                break;
            case (Key.Insert, KeyModifiers.None) when editing is { CanInsert: true }:
                e.Handled = true;
                AddRowAndEdit();
                break;
            case (Key.C, KeyModifiers.Control):
                e.Handled = true;
                _ = RunSafe(() => CopySelectionAsync(withHeader: false));
                break;
            case (Key.C, KeyModifiers.Control | KeyModifiers.Shift):
                e.Handled = true;
                _ = RunSafe(() => CopySelectionAsync(withHeader: true));
                break;
            case (Key.Enter, KeyModifiers.Alt):
                e.Handled = true;
                _ = RunSafe(ViewCurrentValueAsync);
                break;
        }
    }

    /// <summary>
    /// Marks the selected rows for deletion (a new row is simply dropped). Returns how many rows were selected; the table tab
    /// uses 0 to tell the user to select rows first.
    /// </summary>
    public int DeleteSelectedRows()
    {
        if (_editing is not { } editing || ItemsSource is not IList list) return 0;
        var rows = SelectedRows;
        foreach (var row in rows)   // by identity: deleting a new row shifts the indexes
            if (list.IndexOf(row) is >= 0 and var index) editing.DeleteRow(index);
        return rows.Count;
    }

    /// <summary>Adds an empty row and puts its first editable cell in edit mode.</summary>
    public void AddRowAndEdit()
    {
        if (_editing is { } editing && editing.AddRow() is >= 0 and var index) StartEditingNewRow(index);
    }

    // ---- copy ----

    /// <summary>
    /// Ctrl+C: one row selected (or none) copies the current cell, several rows copy a TSV block; with <paramref name="withHeader"/>
    /// the rows go with a header line. Values are complete (not the shortened text of the cell); NULL is empty.
    /// </summary>
    private async Task CopySelectionAsync(bool withHeader)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var rows = Grid.SelectedItems.Cast<object>().ToList();
        string text;
        if (!withHeader && rows.Count <= 1 && Grid.CurrentColumn is GridCellColumn current)
        {
            var row = rows.FirstOrDefault() ?? Grid.SelectedItem;
            text = ValueText(row, current.Index);
        }
        else
        {
            if (rows.Count == 0 && Grid.SelectedItem is { } one) rows.Add(one);
            if (rows.Count == 0) return;
            text = RowsText(rows, withHeader);
        }
        await clipboard.SetTextAsync(text);
    }

    private static string ValueText(object? row, int column) =>
        GridCellColumn.ValuesOf(row) is { } values && column < values.Length && values[column] is { } v ? CopyText(v) : "";

    private string RowsText(IEnumerable<object> rows, bool withHeader)
    {
        var lines = new List<string>();
        var count = Columns?.Count ?? 0;
        if (withHeader && Columns is { } columns) lines.Add(string.Join('\t', columns.Select(c => c.Name)));
        foreach (var row in rows)
        {
            var width = count > 0 ? count : GridCellColumn.ValuesOf(row)?.Length ?? 0;
            lines.Add(string.Join('\t', Enumerable.Range(0, width).Select(i => ValueText(row, i).Replace('\t', ' ').Replace("\r\n", " ").Replace('\n', ' '))));
        }
        return string.Join(Environment.NewLine, lines);
    }

    // ---- value viewer ----

    private Task ViewCurrentValueAsync()
    {
        if (Grid.SelectedItem is not { } row || Grid.CurrentColumn is not GridCellColumn column) return Task.CompletedTask;
        return ShowValueAsync(row, column.Index);
    }

    private Task ShowValueAsync(object? row, int column)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || GridCellColumn.ValuesOf(row) is not { } values || column >= values.Length || values[column] is not { } value)
            return Task.CompletedTask;
        var name = Columns is { } columns && column < columns.Count ? columns[column].Name : $"column {column + 1}";
        return new ValueViewerDialog(name, CopyText(value), value is byte[]).ShowDialog(owner);
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
            Grid.ScrollIntoView(list[index], column);   // again: the pending-changes bar that just appeared took room from the viewport
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
        Grid.ContextMenu = BuildCellMenu();
    }

    private ContextMenu BuildCellMenu()
    {
        // Copy
        var copyItem = new MenuItem { Header = "Copy value", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        copyItem.Click += async (_, _) =>
        {
            if (_contextCell is not { } c || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            await RunSafe(() => clipboard.SetTextAsync(ValueText(c.Row, c.Index)));
        };
        var copyRowItem = new MenuItem { Header = "Copy row" };
        copyRowItem.Click += async (_, _) =>
        {
            if (_contextCell is not { } c || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            await RunSafe(() => clipboard.SetTextAsync(RowsText([c.Row!], withHeader: false)));
        };
        var copyRowsItem = new MenuItem { Header = "Copy selected rows with header", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift) };
        copyRowsItem.Click += (_, _) => _ = RunSafe(() => CopySelectionAsync(withHeader: true));
        var viewItem = new MenuItem { Header = "View value…", InputGesture = new KeyGesture(Key.Enter, KeyModifiers.Alt) };
        viewItem.Click += (_, _) =>
        {
            if (_contextCell is { } c) _ = RunSafe(() => ShowValueAsync(c.Row, c.Index));
        };

        // Filter
        var filterItem = new MenuItem { Header = "Filter by this value" };
        filterItem.Click += (_, _) =>
        {
            if (_contextCell is { } c && Features is { } f && RowIndexOf(c) is >= 0 and var row)
                _ = RunSafe(() => f.FilterByCellAsync(row, c.ColumnName));
        };
        var excludeItem = new MenuItem { Header = "Exclude this value" };
        excludeItem.Click += (_, _) =>
        {
            if (_contextCell is { } c && Features is { } f && RowIndexOf(c) is >= 0 and var row)
                _ = RunSafe(() => f.ExcludeByCellAsync(row, c.ColumnName));
        };
        var followItem = new MenuItem { Header = "Follow foreign key" };
        followItem.Click += (_, _) =>
        {
            if (_contextCell is { } c) _ = FollowAsync(c);
        };
        var filterSeparator = new Separator();

        // Edit
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
        var deleteItem = new MenuItem { Header = "Delete row", InputGesture = new KeyGesture(Key.Delete), Classes = { "danger" } };
        deleteItem.Click += (_, _) =>
        {
            if (_contextCell is { } c && _editing is { } editing && RowIndexOf(c) is >= 0 and var row) editing.DeleteRow(row);
        };
        var restoreItem = new MenuItem { Header = "Restore row" };
        restoreItem.Click += (_, _) =>
        {
            if (_contextCell is { } c && _editing is { } editing && RowIndexOf(c) is >= 0 and var row) editing.UndeleteRow(row);
        };
        var addItem = new MenuItem { Header = "Add row", InputGesture = new KeyGesture(Key.Insert) };
        addItem.Click += (_, _) => AddRowAndEdit();
        var editSeparator = new Separator();

        var menu = new ContextMenu();
        foreach (var item in new Control[]
                 {
                     copyItem, copyRowItem, copyRowsItem, viewItem, filterSeparator, filterItem, excludeItem, followItem,
                     editSeparator, editItem, nullItem, deleteItem, restoreItem, addItem,
                 })
            menu.Items.Add(item);

        menu.Opening += (_, e2) =>
        {
            if (_contextCell is null) { e2.Cancel = true; return; }
            var table = Features is not null;
            var hasValue = GridCellColumn.ValuesOf(_contextCell.Row) is { } vs && _contextCell.Index < vs.Length && vs[_contextCell.Index] is not null;
            copyRowsItem.IsVisible = Grid.SelectedItems.Count > 1;
            viewItem.IsEnabled = hasValue;
            filterSeparator.IsVisible = table;
            filterItem.IsVisible = table;
            excludeItem.IsVisible = table;
            filterItem.Header = hasValue ? "Filter by this value" : "Show only NULL";
            excludeItem.Header = hasValue ? "Exclude this value" : "Hide NULL";
            followItem.IsVisible = table && _contextCell.IsLink;
            followItem.Header = _contextCell.LinkTarget is { } target ? $"Follow foreign key → {target}" : "Follow foreign key";
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
        return menu;
    }

    /// <summary>The complete value (the cell may show a shortened form).</summary>
    private static string CopyText(object value) => value switch
    {
        string s => s,
        byte[] b => "0x" + Convert.ToHexString(b),
        _ => CellFormatter.Format(value).Text,
    };

    private sealed record HeaderParts(ResultColumn Column, Control Root, PathIcon SortGlyph, TextBox? Filter, TextBlock Title, PathIcon FilterMark, Border? Band);
}
