using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Utils;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// The single element of a grid cell: a TextBlock that remembers which row and column it shows. <see cref="Refresh"/>
/// (re)draws it from the row's current value, so a staged edit can repaint a realised cell without rebuilding the grid.
/// </summary>
public sealed class GridCell : TextBlock
{
    private static readonly IBrush FallbackLinkBrush = new SolidColorBrush(Color.Parse("#2B63A8"));

    // Only foreign-key cells ask: the link colour is a theme token, looked up for the theme that is showing now.
    private static IBrush LinkBrush() =>
        Application.Current is { } app && app.TryGetResource("DbxLink", app.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : FallbackLinkBrush;
    // Translucent so they read on both the light and the dark theme.
    private static readonly IBrush DirtyBrush = new SolidColorBrush(Color.Parse("#66F2B600"));   // amber: edited
    private static readonly IBrush NewRowBrush = new SolidColorBrush(Color.Parse("#4429A329"));
    private static readonly IBrush DeletedBrush = new SolidColorBrush(Color.Parse("#55B3261E"));

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public int Index { get; init; }
    public string ColumnName { get; init; } = "";
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
        var formatted = CellFormatter.Format(value);

        IsLink = CanLink && !formatted.IsNull;
        Text = IsLink ? formatted.Text + " ↗" : formatted.Text;
        TextAlignment = formatted.IsNumber ? TextAlignment.Right : TextAlignment.Left;
        Opacity = formatted.IsNull ? 0.4 : 1;
        FontStyle = formatted.IsNull ? FontStyle.Italic : FontStyle.Normal;
        if (IsLink && !formatted.IsNull) Foreground = LinkBrush();
        else ClearValue(ForegroundProperty);
        ToolTip.SetTip(this, formatted.Tooltip);

        var row = Row as RowViewModel;
        Background = row switch
        {
            { IsDeleted: true } => DeletedBrush,
            { IsNew: true } => NewRowBrush,
            { } r when r.IsDirty(Index) => DirtyBrush,
            _ => Brushes.Transparent,   // hit-testable across the whole cell
        };
        TextDecorations = row is { IsDeleted: true } ? Avalonia.Media.TextDecorations.Strikethrough : null;
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
        var text = TableTabViewModel.EditText(values is not null && index < values.Length ? values[index] : null);
        binding = NoBinding.Instance;
        return new CellEditor
        {
            Text = text,
            InitialText = text,
            AcceptsReturn = false,
            MinHeight = 0,
            Padding = new Thickness(6, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
    }

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
