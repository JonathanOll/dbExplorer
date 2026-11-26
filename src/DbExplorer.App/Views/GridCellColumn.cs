using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Utils;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>Theme brushes of the grid cells, looked up once per theme (a cell refresh must stay cheap) and read again after a switch.</summary>
internal static class CellBrushes
{
    private static readonly Dictionary<string, IBrush> Cache = [];
    private static ThemeVariant? _variant;

    public static IBrush Edited => Get("DbxCellEdited");
    public static IBrush NewRow => Get("DbxCellNew");
    public static IBrush Deleted => Get("DbxCellDeleted");
    public static IBrush Link => Get("DbxLink");
    public static IBrush Faint => Get("DbxTextFaint");

    private static IBrush Get(string key)
    {
        var app = Application.Current;
        var variant = app?.ActualThemeVariant;
        if (variant != _variant)
        {
            Cache.Clear();
            _variant = variant;
        }
        if (Cache.TryGetValue(key, out var cached)) return cached;
        if (app is null || !app.TryGetResource(key, variant, out var value) || value is not IBrush brush) return Brushes.Transparent;
        return Cache[key] = brush;
    }
}

/// <summary>
/// The single element of a grid cell: a TextBlock that remembers which row and column it shows. <see cref="Refresh"/>
/// (re)draws it from the row's current value, so a staged edit can repaint a realised cell without rebuilding the grid.
/// </summary>
public sealed class GridCell : TextBlock
{
    // Wide enough for Consolas at the grid font size; only used to guess whether a value is cut by its column.
    private const double CharWidth = 7.0;
    private const double CellPadding = 16;
    private static Cursor? _handCursor;

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public int Index { get; init; }
    public string ColumnName { get; init; } = "";

    /// <summary>The column that made this cell (its current width tells whether a value is cut).</summary>
    public GridCellColumn? Column { get; init; }

    /// <summary>The DataGrid cell around this element, which is what lights up when selected.</summary>
    public DataGridCell? Host { get; init; }

    /// <summary>Lights the cell, or not, as the cell selection says (a recycled cell shows another row).</summary>
    public void ApplyPicked() => Host?.Classes.Set("picked", Column?.IsPicked?.Invoke(Row, Index) == true);

    /// <summary>
    /// The row this cell currently shows. The DataGrid recycles row containers while scrolling (and when a row is added)
    /// WITHOUT rebuilding their cells, so this follows the inherited DataContext (the row item) instead of staying what
    /// <see cref="GridCellColumn"/> first created the cell for.
    /// </summary>
    public object? Row { get; set; }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is null || ReferenceEquals(DataContext, Row)) return;
        Row = DataContext;
        Refresh();
    }

    /// <summary>The column is a foreign key: a non-NULL value is a link.</summary>
    public bool CanLink { get; init; }
    public string? LinkTarget { get; init; }

    /// <summary>The cell currently shows a followable foreign-key value.</summary>
    public bool IsLink { get; private set; }

    public void Refresh()
    {
        var values = GridCellColumn.ValuesOf(Row);
        var value = values is not null && Index < values.Length ? values[Index] : null;
        var formatted = CellFormatter.FormatForDisplay(value);
        var row = Row as RowViewModel;

        IsLink = CanLink && !formatted.IsNull;
        var untouched = formatted.IsNull && row is { IsNew: true };   // an insert leaves NULL cells out: the column default applies
        Text = untouched ? "DEFAULT" : formatted.Text;
        TextAlignment = formatted.IsNumber ? TextAlignment.Right : value is bool ? TextAlignment.Center : TextAlignment.Left;
        FontStyle = formatted.IsNull ? FontStyle.Italic : FontStyle.Normal;
        if (formatted.IsNull || value is false) Foreground = CellBrushes.Faint;
        else if (IsLink) Foreground = CellBrushes.Link;
        else ClearValue(ForegroundProperty);

        var dirty = row is { IsNew: false, IsDeleted: false } && row.IsDirty(Index);
        if (dirty) FontWeight = FontWeight.SemiBold;   // not only a colour: an edited cell is also bolder
        else ClearValue(FontWeightProperty);
        Background = row switch
        {
            { IsDeleted: true } => CellBrushes.Deleted,
            { IsNew: true } => CellBrushes.NewRow,
            _ when dirty => CellBrushes.Edited,
            _ => Brushes.Transparent,   // hit-testable across the whole cell
        };
        TextDecorations = row is { IsDeleted: true } ? Avalonia.Media.TextDecorations.Strikethrough : null;
        ToolTip.SetTip(this, BuildTip(formatted, row, dirty, untouched));
        ApplyPicked();
    }

    private object? BuildTip(FormattedCell formatted, RowViewModel? row, bool dirty, bool untouched)
    {
        string? tip = null;
        if (row is { IsDeleted: true }) tip = "Marked for deletion: the row is deleted on save";
        else if (untouched) tip = "Left empty: the column default applies when the row is inserted";
        else if (dirty)
        {
            var was = CellFormatter.Format(row!.Original[Index]);
            tip = "Was: " + (was.IsNull ? "NULL" : Shorten(was.Text));
        }
        else if (formatted.Tooltip is { } full) tip = full;
        else if (IsCut(formatted.Text)) tip = formatted.Text;

        if (IsLink) tip = (tip is null ? "" : tip + "\n") + $"Ctrl+click or double-click to open {LinkTarget}";
        if (tip is null) return null;
        if (tip.Length < 80 && !tip.Contains('\n')) return tip;
        return new TextBlock
        {
            Text = tip, TextWrapping = TextWrapping.Wrap, MaxWidth = 560,
            FontFamily = Application.Current?.TryFindResource("DbxMonoFont", out var mono) == true && mono is FontFamily family ? family : FontFamily.Default,
        };
    }

    private static string Shorten(string text) => text.Length <= 200 ? text : text[..200] + "…";

    /// <summary>A guess, without measuring (this runs for every realised cell): the text is wider than its column.</summary>
    private bool IsCut(string text)
    {
        if (text.Length < 6 || Column is not { } column) return false;
        var width = column.ActualWidth;
        if (width <= 0 && column.Width.IsAbsolute) width = column.Width.Value;
        return width > 0 && text.Length * CharWidth > width - CellPadding;
    }

    /// <summary>Ctrl is down over a link cell: underline and hand cursor say that a click follows the foreign key.</summary>
    public void SetLinkHover(bool on)
    {
        Cursor = on ? _handCursor ??= new Cursor(StandardCursorType.Hand) : null;
        TextDecorations = on ? Avalonia.Media.TextDecorations.Underline
            : Row is RowViewModel { IsDeleted: true } ? Avalonia.Media.TextDecorations.Strikethrough : null;
    }
}

/// <summary>The text box shown while a cell is edited; remembers what it started with and whether NULL was asked for.</summary>
public sealed class CellEditor : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>Text when the edit began: committing it unchanged stages nothing (a NULL cell must not turn into '').</summary>
    public string InitialText { get; init; } = "";

    /// <summary>Set by the "Set NULL" gesture just before the edit is committed.</summary>
    public bool SetNull { get; set; }
}

/// <summary>
/// A column whose cells are built directly in <see cref="GenerateElement"/>: no bindings, no data template, text
/// formatted on demand from the runtime value (see <see cref="CellFormatter"/>). Editing (opt-in, see
/// <c>ResultGrid.Editing</c>) swaps the cell for a <see cref="CellEditor"/>; the grid reads the text back itself, so
/// there is no binding to commit.
/// </summary>
public sealed class GridCellColumn(int index, ResultColumn column, bool isLink) : DataGridColumn
{
    public int Index => index;

    /// <summary>Whether the cell of this row and column is part of the cell or column selection.</summary>
    public Func<object?, int, bool>? IsPicked { get; init; }

    /// <summary>The values of a row item: <c>object?[]</c> or <see cref="RowViewModel"/>.</summary>
    public static object?[]? ValuesOf(object? item) => item switch
    {
        RowViewModel row => row.Values,
        object?[] values => values,
        _ => null,
    };

    protected override Control GenerateElement(DataGridCell cell, object dataItem)
    {
        var element = new GridCell
        {
            Index = index,
            ColumnName = column.Name,
            Row = dataItem,
            Column = this,
            Host = cell,
            CanLink = isLink,
            LinkTarget = isLink ? column.LinkTarget : null,
            Padding = new Thickness(8, 2),
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        element.Refresh();
        return element;
    }

    protected override Control GenerateEditingElement(DataGridCell cell, object dataItem, out ICellEditBinding binding)
    {
        var values = ValuesOf(dataItem);
        var value = values is not null && index < values.Length ? values[index] : null;
        var text = TableTabViewModel.EditText(value);
        binding = NoBinding.Instance;
        var editor = new CellEditor
        {
            Text = text,
            InitialText = text,
            AcceptsReturn = false,
            MinHeight = 0,
            Padding = new Thickness(6, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Watermark = value is null ? "NULL" : TypeHint(column),
        };
        // The mouse way to Ctrl+0 (the keyboard way stays the one that takes focus, so this button does not).
        var setNull = new Button
        {
            Content = "NULL", Classes = { "ghost" }, Focusable = false, MinHeight = 0, Height = 20, Padding = new Thickness(6, 0),
            FontSize = 10.5, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(setNull, "Set this cell to NULL (Ctrl+0)");
        setNull.Click += (_, _) =>
        {
            editor.SetNull = true;
            editor.FindAncestorOfType<DataGrid>()?.CommitEdit();
        };
        editor.InnerRightContent = setNull;
        return editor;
    }

    /// <summary>What an empty editor shows: the shape of a value the column accepts.</summary>
    private static string? TypeHint(ResultColumn column) =>
        column.IsNumeric ? "number"
        : column.IsBoolean ? "true / false"
        : column.IsTemporal ? (column.TypeName?.Contains("TIME", StringComparison.OrdinalIgnoreCase) == true ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd")
        : null;

    protected override object PrepareCellForEdit(Control editingElement, RoutedEventArgs editingEventArgs)
    {
        if (editingElement is not CellEditor editor) return "";
        editor.Focus();
        editor.SelectAll();
        return editor.Text ?? "";
    }

    /// <summary>Stand-in for the data binding the DataGrid expects: the text is read back by <c>ResultGrid</c> on commit.</summary>
    private sealed class NoBinding : ICellEditBinding
    {
        public static readonly NoBinding Instance = new();
        public bool IsValid => true;
        public IEnumerable<Exception> ValidationErrors => [];
        public IObservable<bool> ValidationChanged { get; } = new Never();
        public bool CommitEdit() => true;

        private sealed class Never : IObservable<bool>
        {
            public IDisposable Subscribe(IObserver<bool> observer) => new Nothing();
        }

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }
}
