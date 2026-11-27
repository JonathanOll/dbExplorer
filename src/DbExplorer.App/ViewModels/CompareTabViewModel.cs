using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Comparing;
using DbExplorer.Core.Export;

namespace DbExplorer.App.ViewModels;

/// <summary>A column present on both sides: whether it identifies the rows, and whether its values are compared.</summary>
public sealed partial class CompareColumnChoice(ColumnPair pair) : ObservableObject
{
    public ColumnPair Pair { get; } = pair;
    public string Name => Pair.Name;
    public string KeyName => $"Key column {Name}";
    public string ComparedName => $"Compare column {Name}";

    [ObservableProperty] private bool _isKey;
    [ObservableProperty] private bool _isCompared = true;
}

/// <summary>
/// The comparison of two results: A (selected first) against B. Matching and comparing run off the UI thread; every change
/// of an option cancels the comparison in flight and starts another.
/// </summary>
public sealed partial class CompareTabViewModel : TabViewModel, IGridExportHost
{
    private CompareSnapshot _left;
    private CompareSnapshot _right;
    private ColumnMatch _match;
    private IReadOnlyList<DiffRow> _all = [];
    private List<int> _differences = [];   // positions in Rows
    private CancellationTokenSource? _work;
    private bool _settingUp;
    private bool _keyDecided;   // proposed, or chosen by the user: not proposed again
    private string _columnsShown = "";

    public CompareTabViewModel(CompareSnapshot left, CompareSnapshot right) : base(right.Connection)
    {
        _left = left;
        _right = right;
        _match = ColumnMatching.Match(left.Data, right.Data);
        CreateChoices();
        Pending = RunAsync();
    }

    public override string Title => _left.Name == _right.Name
        ? $"Compare · {_left.Name}"
        : $"Compare · {_left.Name} / {_right.Name}";

    public override string IconKey => "IconCompare";

    /// <summary>The comparison in progress; completed when the grid is up to date.</summary>
    public Task Pending { get; private set; }

    public string LeftText => Side(_left);

    public string RightText => Side(_right);

    private static string Side(CompareSnapshot s) => $"{s.Detail} · {StatementResultViewModel.RowsText(s.Data.Rows.Count)}";

    [ObservableProperty] private IReadOnlyList<CompareColumnChoice> _choices = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsByPosition), nameof(MatchText))]
    private bool _isByKey = true;

    public bool IsByPosition => !IsByKey;

    [ObservableProperty] private bool _ignoreCase;

    [ObservableProperty] private bool _ignoreSpaces;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText), nameof(ShowEmpty), nameof(ShowGrid))]
    private bool _onlyDifferences;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowGrid), nameof(NavigateTip))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand), nameof(NextDifferenceCommand), nameof(PreviousDifferenceCommand))]
    private bool _isBusy;

    /// <summary>Why there is no comparison: cancelled, failed, or an option that makes none possible.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem), nameof(ShowEmpty), nameof(ShowGrid))]
    private string? _problem;

    public bool HasProblem => Problem is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(ChangedText), nameof(AddedText), nameof(RemovedText), nameof(SameText),
        nameof(IsIdentical), nameof(HasDifferences), nameof(EmptyText), nameof(ShowEmpty), nameof(ShowGrid), nameof(NavigateTip),
        nameof(DuplicateText), nameof(HasDuplicates))]
    private ComparisonResult? _result;

    [ObservableProperty] private IReadOnlyList<ResultColumn> _gridColumns = [];

    [ObservableProperty] private IReadOnlyList<DiffRow> _rows = [];

    /// <summary>The row of <see cref="Rows"/> the grid is on (the view keeps it); -1 for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    private int _currentRow = -1;

    /// <summary>Asks the view to show a cell (row of <see cref="Rows"/>, grid column).</summary>
    public event Action<int, int>? RevealRequested;

    // ---- what the comparison says ----

    public bool HasResult => Result is not null;
    public bool IsIdentical => Result is { Differences: 0 };
    public bool HasDifferences => Result is { Differences: > 0 };
    public string ChangedText => $"≠ {N(Result?.Changed)} changed";
    public string AddedText => $"+ {N(Result?.Added)} only in B";
    public string RemovedText => $"− {N(Result?.Removed)} only in A";
    public string SameText => $"= {N(Result?.Same)} same";

    private static string N(int? n) => (n ?? 0).ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"2 of 6" while the grid is on a difference, else "6 differences".</summary>
    public string PositionText
    {
        get
        {
            if (Result is null) return "";
            var at = _differences.BinarySearch(CurrentRow);
            if (at >= 0) return $"{N(at + 1)} of {N(_differences.Count)}";
            return _differences.Count == 1 ? "1 difference" : $"{N(_differences.Count)} differences";
        }
    }

    public string MatchText => IsByPosition ? "Rows are matched by position."
        : Choices.Where(c => c.IsKey).Select(c => c.Name).ToList() is { Count: > 0 } keys
            ? $"Rows are matched by {string.Join(", ", keys)}."
            : "No key column is chosen.";

    /// <summary>Why the key was chosen, until the user changes it.</summary>
    [ObservableProperty] private string _keyNote = "";

    /// <summary>Said when a side does not hold every row: the differences are only those of the loaded rows.</summary>
    public string? PartialText
    {
        get
        {
            var parts = new[] { ("A", _left.Partial), ("B", _right.Partial) }
                .Where(p => p.Item2 is not null).Select(p => $"{p.Item1}: {p.Item2}").ToList();
            return parts.Count == 0 ? null : "Only the loaded rows are compared. " + string.Join(" ", parts);
        }
    }

    public bool HasPartial => PartialText is not null;

    public string? ColumnsText
    {
        get
        {
            var parts = new List<string>();
            if (_match.OnlyLeft.Count > 0) parts.Add("Only in A: " + string.Join(", ", _match.OnlyLeft) + ".");
            if (_match.OnlyRight.Count > 0) parts.Add("Only in B: " + string.Join(", ", _match.OnlyRight) + ".");
            return parts.Count == 0 ? null : "Columns not compared. " + string.Join(" ", parts);
        }
    }

    public bool HasColumnsText => ColumnsText is not null;

    public string? TypesText => _match.TypeNotes.Count == 0 ? null
        : "Types differ (values are compared by value): " + string.Join("; ", _match.TypeNotes) + ".";

    public bool HasTypesText => TypesText is not null;

    public bool HasDuplicates => Result is { Duplicates.Any: true };

    public string? DuplicateText => Result?.Duplicates is { Any: true } d
        ? $"Duplicate keys: {N(d.LeftRows)} rows in A and {N(d.RightRows)} rows in B share their key ({string.Join("; ", d.Examples)}). "
          + "Rows with the same key are paired in order. Add a key column to tell them apart."
        : null;

    public bool ShowGrid => !HasProblem && HasResult && Rows.Count > 0;

    public bool ShowEmpty => !IsBusy && !HasProblem && HasResult && Rows.Count == 0;

    public string EmptyText => IsIdentical && OnlyDifferences ? "No difference between A and B" : "Neither side has a row";

    public string NavigateTip => HasDifferences ? "Next difference (F8), previous (Shift+F8)"
        : IsBusy ? "The comparison is running." : "There is no difference to go to.";

    // ---- options ----

    private void CreateChoices()
    {
        _settingUp = true;
        Choices = _match.Pairs.Select(p =>
        {
            var choice = new CompareColumnChoice(p);
            choice.PropertyChanged += (_, e) => OnChoiceChanged(choice, e.PropertyName);
            return choice;
        }).ToList();
        _settingUp = false;
    }

    private void OnChoiceChanged(CompareColumnChoice choice, string? property)
    {
        if (_settingUp) return;
        if (property == nameof(CompareColumnChoice.IsKey))
        {
            KeyNote = "";
            _keyDecided = true;
            // Picking a key column says how to match.
            if (choice.IsKey && !IsByKey)
            {
                IsByKey = true;   // starts the comparison
                return;
            }
        }
        OnPropertyChanged(nameof(MatchText));
        Recompare();
    }

    partial void OnIsByKeyChanged(bool value)
    {
        if (_settingUp) return;
        KeyNote = "";
        _keyDecided = true;
        Recompare();
    }

    partial void OnIgnoreCaseChanged(bool value) => Recompare();

    partial void OnIgnoreSpacesChanged(bool value) => Recompare();

    partial void OnOnlyDifferencesChanged(bool value) => ShowRows();

    /// <summary>Compares again with the current options (also after a cancel).</summary>
    [RelayCommand]
    public void Recompare() => Pending = RunAsync();

    [RelayCommand(CanExecute = nameof(IsBusy))]
    public void Cancel()
    {
        if (!IsBusy) return;
        _work?.Cancel();
        _work = null;
        IsBusy = false;
        Clear();
        Problem = "The comparison was cancelled.";
    }

    /// <summary>Exchanges A and B.</summary>
    [RelayCommand]
    public void Swap()
    {
        var keys = Choices.Where(c => c.IsKey).Select(c => c.Name).ToList();
        var skipped = Choices.Where(c => !c.IsCompared).Select(c => c.Name).ToList();
        (_left, _right) = (_right, _left);
        _match = ColumnMatching.Match(_left.Data, _right.Data);
        CreateChoices();
        _settingUp = true;
        foreach (var choice in Choices)
        {
            choice.IsKey = keys.Contains(choice.Name, StringComparer.OrdinalIgnoreCase);
            choice.IsCompared = !skipped.Contains(choice.Name, StringComparer.OrdinalIgnoreCase);
        }
        _settingUp = false;
        Connection = _right.Connection;
        _columnsShown = "";
        OnPropertyChanged(string.Empty);   // both sides, the notes and the title
        Recompare();
    }

    /// <summary>The tab is closed: stops the work in flight.</summary>
    public void Close()
    {
        _work?.Cancel();
        _work = null;
    }

    private void Clear()
    {
        _all = [];
        Result = null;
        ShowRows();
    }

    private async Task RunAsync()
    {
        _work?.Cancel();
        var work = _work = new CancellationTokenSource();
        var ct = work.Token;
        IsBusy = true;
        Problem = null;
        try
        {
            var (left, right, match) = (_left.Data, _right.Data, _match);
            if (match.Pairs.Count == 0)
            {
                Clear();
                Problem = "The two results have no column name in common, so there is nothing to compare.";
                return;
            }
            if (!_keyDecided)
            {
                var proposal = await Task.Run(() => KeyGuesser.Guess(left, right, match.Pairs, ct), ct);
                if (!ReferenceEquals(_work, work)) return;
                _settingUp = true;
                foreach (var choice in Choices) choice.IsKey = proposal.Keys.Contains(choice.Pair);
                IsByKey = proposal.Keys.Count > 0;
                _settingUp = false;
                _keyDecided = true;
                KeyNote = proposal.Reason;
                OnPropertyChanged(nameof(MatchText));
            }

            var keys = IsByKey ? Choices.Where(c => c.IsKey).Select(c => c.Pair).ToList() : [];
            if (IsByKey && keys.Count == 0)
            {
                Clear();
                Problem = "Choose at least one key column (Columns), or match the rows by position.";
                return;
            }
            var compared = Choices.Where(c => c.IsCompared).Select(c => c.Pair).ToList();
            var options = new ComparisonOptions(keys, compared, IgnoreCase, IgnoreSpaces);
            // Key columns first, then the compared ones.
            var columns = keys.Concat(compared.Except(keys)).Select(p => new DiffColumn(p, compared.IndexOf(p))).ToList();

            var (result, rows) = await Task.Run(() =>
            {
                var compare = ResultComparer.Compare(left, right, options, ct);
                var built = compare.Rows.Select(r => new DiffRow(r, columns, left, right)).ToArray();
                return (compare, (IReadOnlyList<DiffRow>)built);
            }, ct);
            if (!ReferenceEquals(_work, work)) return;

            ShowColumns(columns, keys);
            _all = rows;
            Result = result;
            ShowRows();
        }
        catch (OperationCanceledException)
        {
            // A newer run or Cancel took over.
        }
        catch (Exception e)
        {
            if (!ReferenceEquals(_work, work)) return;
            Clear();
            Problem = "The comparison failed: " + ErrorText.Describe(e);
        }
        finally
        {
            if (ReferenceEquals(_work, work)) IsBusy = false;
        }
    }

    // The grid rebuilds its columns when the list changes, so it is replaced only when it would differ.
    private void ShowColumns(IReadOnlyList<DiffColumn> columns, IReadOnlyList<ColumnPair> keys)
    {
        var shown = string.Join('|', columns.Select(c => $"{c.Pair.Left}:{c.Pair.Right}:{keys.Contains(c.Pair)}"));
        if (shown == _columnsShown) return;
        _columnsShown = shown;
        Rows = [];
        GridColumns = columns
            .Select(c => new ResultColumn(c.Pair.Name, IsPrimaryKey: keys.Contains(c.Pair)))
            .Prepend(new ResultColumn("Difference"))
            .ToList();
    }

    private void ShowRows()
    {
        var current = CurrentRow >= 0 && CurrentRow < Rows.Count ? Rows[CurrentRow] : null;
        var shown = OnlyDifferences ? _all.Where(r => r.IsDifference).ToList() : _all;
        var differences = new List<int>();
        for (var i = 0; i < shown.Count; i++)
            if (shown[i].IsDifference) differences.Add(i);
        _differences = differences;
        Rows = shown;
        CurrentRow = current is null ? -1 : IndexOf(shown, current);
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(ShowGrid));
        OnPropertyChanged(nameof(ShowEmpty));
        NextDifferenceCommand.NotifyCanExecuteChanged();
        PreviousDifferenceCommand.NotifyCanExecuteChanged();
    }

    private static int IndexOf(IReadOnlyList<DiffRow> rows, DiffRow row)
    {
        for (var i = 0; i < rows.Count; i++)
            if (ReferenceEquals(rows[i], row)) return i;
        return -1;
    }

    // ---- navigation ----

    public bool CanNavigate => !IsBusy && _differences.Count > 0;

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    public void NextDifference() => Go(forward: true);

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    public void PreviousDifference() => Go(forward: false);

    private void Go(bool forward)
    {
        if (!CanNavigate) return;
        var at = _differences.BinarySearch(CurrentRow);
        var next = at >= 0 ? at + (forward ? 1 : -1) : ~at - (forward ? 0 : 1);
        var row = _differences[(next + _differences.Count) % _differences.Count];
        CurrentRow = row;
        RevealRequested?.Invoke(row, Rows[row].FirstMarkedColumn);
    }

    // ---- copy and export of the grid ----

    /// <summary>Set by the shell, like for the other grids.</summary>
    public ExportServices? ExportServices { get; set; }

    public ExportContext CreateExportContext(IReadOnlyList<ResultColumn> columns, IReadOnlyList<object?[]> rows, GridSlice? selection) => new()
    {
        Columns = columns.Select(c => new ExportColumn(c.Name, c.TypeName, c.ClrType)).ToList(),
        // A changed cell is exported as the "left → right" text it shows.
        Rows = rows.Select(r => r.Select(v => v is CellChange change ? change.ToString() : v).ToArray()).ToList(),
        Selection = selection,
        Kind = Connection.Kind,
        BaseName = "comparison",
        AllRowsUnavailable = "A comparison holds only the rows that were loaded.",
    };
}
