using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Export;

namespace DbExplorer.App.ViewModels;

/// <summary>"Export": format, which rows, options, a preview, then the file is written off the UI thread.</summary>
public sealed partial class ExportDialogViewModel : ObservableObject
{
    private const int PreviewRows = 20;
    private const int PreviewChars = 6000;

    public const string FormulaExplanation =
        "A spreadsheet runs a text that starts with =, +, - or @ as a formula, which can leak data or start a program. " +
        "This puts an apostrophe before such texts. It changes the exported values.";

    private readonly ExportContext _context;
    private readonly ExportServices _services;
    private readonly bool _ready;
    private CancellationTokenSource? _cts;

    [NotifyPropertyChangedFor(nameof(ShowEncoding), nameof(ShowDelimiter), nameof(ShowHeader), nameof(ShowTextOptions), nameof(ShowGuard), nameof(ShowTableName))]
    [ObservableProperty] private ExportChoice<ExportKind> _format;
    [NotifyPropertyChangedFor(nameof(IsSelectionScope), nameof(IsLoadedScope), nameof(IsAllScope), nameof(ScopeNote))]
    [ObservableProperty] private ExportScope _scope;
    [ObservableProperty] private ExportChoice<ExportEncoding> _encoding;
    [ObservableProperty] private ExportChoice<string> _delimiter;
    [ObservableProperty] private bool _header;
    [ObservableProperty] private string _nullText = "";
    [ObservableProperty] private string _dateFormat = "";
    [ObservableProperty] private bool _guardFormulas;
    [ObservableProperty] private string _tableName = "";
    [ObservableProperty] private string _preview = "";
    [NotifyPropertyChangedFor(nameof(HasError))]
    [ObservableProperty] private string? _error;
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string? _progressText;

    public ExportDialogViewModel(ExportContext context, ExportServices services)
    {
        _context = context;
        _services = services;
        var saved = services.Preferences;
        _format = Formats.FirstOrDefault(f => f.Value == saved.Format) ?? Formats[0];
        _encoding = Encodings.First(e => e.Value == saved.Encoding);
        _delimiter = Delimiters.FirstOrDefault(d => d.Value == saved.CsvDelimiter) ?? Delimiters[0];
        _header = saved.Header;
        _nullText = saved.NullText;
        _dateFormat = saved.DateFormat;
        _guardFormulas = saved.GuardFormulas;
        _tableName = context.Table?.ToString() ?? ExportSpec.UnnamedTable;
        // A selection of several rows or of some columns is what the user means; a single selected row is just the cursor.
        _scope = context.Selection is { } s && (s.Rows.Count > 1 || s.Columns.Count < context.Columns.Count)
            ? ExportScope.Selection
            : ExportScope.Loaded;
        _ready = true;
        RefreshPreview();
    }

    /// <summary>Raised when the dialog should close (exported, or cancelled while idle).</summary>
    public event Action? CloseRequested;

    public IReadOnlyList<ExportChoice<ExportKind>> Formats { get; } =
        new[] { ExportKind.Csv, ExportKind.Tsv, ExportKind.Json, ExportKind.Xlsx, ExportKind.SqlInsert, ExportKind.Markdown }
            .Select(k => new ExportChoice<ExportKind>(GridCopy.Name(k), k)).ToList();

    public IReadOnlyList<ExportChoice<ExportEncoding>> Encodings { get; } =
    [
        new("Automatic (BOM for CSV, TSV)", ExportEncoding.Automatic),
        new("UTF-8", ExportEncoding.Utf8),
        new("UTF-8 with BOM", ExportEncoding.Utf8Bom),
        new("UTF-16 LE with BOM", ExportEncoding.Utf16),
        new("Windows-1252 (other characters become ?)", ExportEncoding.Windows1252),
    ];

    public IReadOnlyList<ExportChoice<string>> Delimiters { get; } =
        [new("Comma  ,", ","), new("Semicolon  ;", ";"), new("Pipe  |", "|")];

    public bool ShowEncoding => Format.Value != ExportKind.Xlsx;
    public bool ShowDelimiter => Format.Value == ExportKind.Csv;
    public bool ShowHeader => Format.Value is ExportKind.Csv or ExportKind.Tsv or ExportKind.Xlsx;
    public bool ShowTextOptions => Format.Value is ExportKind.Csv or ExportKind.Tsv or ExportKind.Markdown;
    public bool ShowGuard => Format.Value is ExportKind.Csv or ExportKind.Tsv;
    public bool ShowTableName => Format.Value == ExportKind.SqlInsert;

    public bool IsSelectionScope { get => Scope == ExportScope.Selection; set { if (value) Scope = ExportScope.Selection; } }
    public bool IsLoadedScope { get => Scope == ExportScope.Loaded; set { if (value) Scope = ExportScope.Loaded; } }
    public bool IsAllScope { get => Scope == ExportScope.All; set { if (value) Scope = ExportScope.All; } }

    public bool CanSelection => _context.Selection is not null;
    public bool CanAll => _context.OpenAllRows is not null;

    public string SelectionLabel => _context.Selection is { } s
        ? $"Selection ({GridCopy.Rows(s.Rows.Count)} × {(s.Columns.Count == 1 ? "1 column" : $"{s.Columns.Count} columns")})"
        : "Selection";

    public string LoadedLabel => $"Loaded rows ({_context.Rows.Count.ToString("N0", CultureInfo.InvariantCulture)})";

    public string AllLabel => "All rows (runs the query again, no row limit)";

    public string SelectionTip => CanSelection ? "Only the selected cells" : "Nothing is selected in the grid";

    public string AllTip => CanAll ? "Read every row from the database while writing the file" : _context.AllRowsUnavailable ?? "Not available";

    /// <summary>The line under the scope choices: why all rows is not offered, or what reading them again means.</summary>
    public string? ScopeNote => !CanAll ? _context.AllRowsUnavailable : Scope == ExportScope.All ? _context.AllRowsNote : null;

    public bool HasError => Error is not null;

    public bool IsIdle => !IsExporting;

    private ExportSpec Spec => new(Format.Value)
    {
        Text = new DelimitedOptions
        {
            Delimiter = Delimiter.Value, Header = Header, NullText = NullText ?? "", DateFormat = DateFormat ?? "", GuardFormulas = GuardFormulas,
        },
        Database = _context.Kind,
        // Unchanged text keeps the real reference: a name with a dot in it must not be split.
        Table = _context.Table is { } t && TableName == t.ToString() ? t : SqlName.Parse(TableName),
        KeyColumns = _context.KeyColumns,
        Encoding = Encoding.Value,
    };

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ExportAsync()
    {
        if (_services.Dialogs is not { } dialogs) return;
        Error = null;
        var spec = Spec;
        var scope = Scope;
        var extension = Exporter.Extension(spec.Kind);
        IsExporting = true;
        using var cts = _cts = new CancellationTokenSource();
        try
        {
            var path = await dialogs.PickFileAsync($"{SafeName(_context.BaseName)}.{extension}", extension);
            if (path is null) return;
            _services.Remember(_services.Preferences with
            {
                Format = Format.Value, Encoding = Encoding.Value, CsvDelimiter = Delimiter.Value, Header = Header,
                NullText = NullText ?? "", DateFormat = DateFormat ?? "", GuardFormulas = GuardFormulas,
            });
            ProgressText = "Exporting…";
            RowSource source;
            if (scope == ExportScope.All) source = await _context.OpenAllRows!(cts.Token);
            else
            {
                var (columns, rows) = _context.Data(scope, spec.Kind);
                source = Exporter.FromRows(columns, rows);
            }
            var progress = new Progress<long>(n =>
            {
                if (IsExporting) ProgressText = $"Exporting… {GridCopy.Rows(n)} written";
            });
            var outcome = await Exporter.ToFileAsync(spec, path, source, progress, cts.Token);

            var message = $"Exported {GridCopy.Rows(outcome.Rows)} to {path}.";
            if (outcome.Warning is { } warning) _services.Status.Error($"{message} {warning}");
            else _services.Status.Info(message);
            CloseRequested?.Invoke();
        }
        catch (OperationCanceledException)
        {
            _services.Status.Info("Export cancelled: no file was written.");
            Error = "Export cancelled: no file was written.";
        }
        catch (ObjectDisposedException)
        {
            Error = "The connection was closed. Reconnect, then export again.";
        }
        catch (Exception e)
        {
            Error = "Cannot export: " + ErrorText.Describe(e);
            _services.Status.Error(Error);
        }
        finally
        {
            _cts = null;
            IsExporting = false;
            ProgressText = null;
        }
    }

    /// <summary>Stops the export in progress, or closes the dialog when there is none.</summary>
    [RelayCommand]
    public void Cancel()
    {
        if (_cts is null) CloseRequested?.Invoke();
        else
        {
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { /* it just ended */ }
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_ready && e.PropertyName is nameof(Format) or nameof(Scope) or nameof(Delimiter) or nameof(Header)
                or nameof(NullText) or nameof(DateFormat) or nameof(GuardFormulas) or nameof(TableName))
            RefreshPreview();
    }

    // The first rows of what is in memory: "all rows" is previewed with the loaded ones, nothing is queried here.
    private void RefreshPreview()
    {
        var spec = Spec;
        var (columns, rows) = _context.Data(Scope == ExportScope.Selection ? ExportScope.Selection : ExportScope.Loaded, spec.Kind);
        // The workbook is binary: its preview is the table of its cells.
        if (spec.Kind == ExportKind.Xlsx) spec = spec with { Kind = ExportKind.Tsv, Text = new DelimitedOptions { Header = Header } };
        try
        {
            var text = Exporter.ToText(spec, columns, rows.Take(PreviewRows));
            Preview = text.Length > PreviewChars ? text[..PreviewChars] + "…" : text;
            Error = null;
        }
        catch (ExportException e)
        {
            Preview = "";
            Error = e.Message;
        }
    }

    private static string SafeName(string name) => string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
}
