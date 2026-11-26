using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Core.Export;

namespace DbExplorer.App.ViewModels;

/// <summary>An entry of a combo box in the copy and export dialogs.</summary>
public sealed record ExportChoice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>"Advanced copy": how the selected cells (or all loaded rows) are written to the clipboard.</summary>
public sealed partial class AdvancedCopyViewModel : ObservableObject
{
    public const int PreviewRows = 20;

    /// <summary>Marks the "Custom" delimiter entry (never a real delimiter: it is replaced by <see cref="CustomDelimiter"/>).</summary>
    private const string Custom = "";

    private readonly ExportServices _services;
    private readonly IReadOnlyList<ExportColumn> _columns;
    private readonly IReadOnlyList<object?[]> _rows;
    private readonly bool _ready;

    [NotifyPropertyChangedFor(nameof(IsCustomDelimiter))]
    [ObservableProperty] private ExportChoice<string> _delimiter;
    [ObservableProperty] private string _customDelimiter = "";
    [ObservableProperty] private ExportChoice<string> _rowSeparator;
    [ObservableProperty] private bool _header;
    [ObservableProperty] private bool _rowNumbers;
    [ObservableProperty] private ExportChoice<QuoteMode> _quoting;
    [ObservableProperty] private string _nullText = "";
    [ObservableProperty] private string _dateFormat = "";
    [ObservableProperty] private bool _guardFormulas;
    [ObservableProperty] private string _preview = "";
    [NotifyPropertyChangedFor(nameof(HasError), nameof(CanCopy))]
    [ObservableProperty] private string? _error;

    public AdvancedCopyViewModel(ExportContext context, ExportServices services)
    {
        _services = services;
        (_columns, _rows) = context.Data(ExportScope.Selection, ExportKind.Delimited);
        var saved = services.Preferences.Copy;

        var known = Delimiters.FirstOrDefault(d => d.Value == saved.Delimiter);
        _delimiter = known ?? Delimiters[^1];
        if (known is null) _customDelimiter = saved.Delimiter;
        _rowSeparator = RowSeparators.FirstOrDefault(s => s.Value == saved.RowSeparator) ?? RowSeparators[0];
        _header = saved.Header;
        _rowNumbers = saved.RowNumbers;
        _quoting = Quotings.First(q => q.Value == saved.Quoting);
        _nullText = saved.NullText;
        _dateFormat = saved.DateFormat;
        _guardFormulas = saved.GuardFormulas;
        _ready = true;
        Refresh();
    }

    public IReadOnlyList<ExportChoice<string>> Delimiters { get; } =
    [
        new("Tab", "\t"), new("Comma  ,", ","), new("Semicolon  ;", ";"), new("Pipe  |", "|"), new("Space", " "), new("Custom…", Custom),
    ];

    public IReadOnlyList<ExportChoice<string>> RowSeparators { get; } =
    [
        new("New line (Windows, CRLF)", "\r\n"), new("New line (Unix, LF)", "\n"), new("Comma", ", "), new("Semicolon", "; "), new("Space", " "),
    ];

    public IReadOnlyList<ExportChoice<QuoteMode>> Quotings { get; } =
    [
        new("When needed", QuoteMode.WhenNeeded), new("Always", QuoteMode.Always), new("Never", QuoteMode.Never),
    ];

    public bool IsCustomDelimiter => Delimiter.Value == Custom;

    public bool HasError => Error is not null;

    public bool CanCopy => Error is null;

    /// <summary>"3 rows × 2 columns".</summary>
    public string Subtitle => $"{GridCopy.Rows(_rows.Count)} × {(_columns.Count == 1 ? "1 column" : $"{_columns.Count} columns")}";

    public DelimitedOptions Options => new()
    {
        Delimiter = IsCustomDelimiter ? CustomDelimiter : Delimiter.Value,
        RowSeparator = RowSeparator.Value,
        Header = Header,
        RowNumbers = RowNumbers,
        Quoting = Quoting.Value,
        NullText = NullText ?? "",
        DateFormat = DateFormat ?? "",
        GuardFormulas = GuardFormulas,
    };

    /// <summary>The text to copy, remembering the choices; null (with <see cref="Error"/> set) when it cannot be written.</summary>
    public string? Copy()
    {
        if (Render(_rows) is not { } text) return null;
        _services.Remember(_services.Preferences with { Copy = Options });
        _services.Status.Info($"Copied {GridCopy.Rows(_rows.Count)}.");
        return text;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_ready && e.PropertyName is not (nameof(Preview) or nameof(Error) or nameof(HasError) or nameof(CanCopy))) Refresh();
    }

    private void Refresh() => Preview = Render(_rows.Take(PreviewRows)) ?? "";

    private string? Render(IEnumerable<object?[]> rows)
    {
        if (Options.Delimiter.Length == 0)
        {
            Error = "Type the delimiter.";
            return null;
        }
        try
        {
            var text = Exporter.ToText(new ExportSpec(ExportKind.Delimited) { Text = Options }, _columns, rows);
            Error = null;
            return text;
        }
        catch (ExportException e)
        {
            Error = e.Message;
            return null;
        }
    }
}
