using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Search;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// The script editor tab. The AvaloniaEdit editor lives in this view only, so nothing of it (control, theme, highlighting
/// definition) is created before the first script tab opens. The editor and the view-model are synchronised here:
/// text, caret and selection flow to the view-model; text changes made by the view-model (opening a file) flow back.
/// </summary>
public partial class ScriptTabView : UserControl
{
    private readonly StatementMarkRenderer _marks = new();
    private DispatcherTimer? _elapsedTimer;
    private ScriptTabViewModel? _vm;
    private bool _syncing;

    // Remembered while the results are maximized (double-click on the splitter).
    private (GridLength Editor, GridLength Results, double EditorMin)? _restoreSplit;

    public ScriptTabView()
    {
        InitializeComponent();
        Editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("TSQL");
        Editor.TextChanged += OnEditorTextChanged;
        Editor.TextArea.Caret.PositionChanged += (_, _) => PushCaretAndSelection();
        Editor.TextArea.SelectionChanged += (_, _) => PushCaretAndSelection();
        Editor.TextArea.TextView.BackgroundRenderers.Add(_marks);
        Editor.TextArea.LeftMargins.Add(_marks.Margin);   // the bars of the statements that ran, after the line numbers
        SearchPanel.Install(Editor);   // Ctrl+F, F3 / Shift+F3, case and whole-word options
        InitCompletion();
        ActualThemeVariantChanged += (_, _) => ApplyEditorTheme();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => Attach(DataContext as ScriptTabViewModel);
        DetachedFromVisualTree += (_, _) =>
        {
            Attach(null);
            StopElapsedTimer();
        };
        AttachedToVisualTree += (_, _) =>
        {
            Attach(DataContext as ScriptTabViewModel);
            ApplyEditorTheme();
            Editor.Focus();
        };
    }

    // The highlighting definition is a process-wide singleton: its colours are switched once per theme, not once per tab.
    private static ThemeVariant? s_syntaxVariant;

    private static readonly Dictionary<string, (string Light, string Dark)> SyntaxColors = new()
    {
        ["Keywords"] = ("#1F5FBF", "#6CB0F5"),
        ["Comment"] = ("#4F7F2D", "#7FA66A"),
        ["Char"] = ("#A8381C", "#E39A7A"),   // string literals
    };

    /// <summary>Gives the editor the colours of the current theme: syntax colours, gutter, selection, current line, run marks.</summary>
    private void ApplyEditorTheme()
    {
        var variant = ActualThemeVariant;
        if (!Equals(s_syntaxVariant, variant) && Editor.SyntaxHighlighting is { } definition)
        {
            foreach (var color in definition.NamedHighlightingColors)
                if (SyntaxColors.TryGetValue(color.Name ?? "", out var pair))
                    color.Foreground = new SimpleHighlightingBrush(Color.Parse(variant == ThemeVariant.Dark ? pair.Dark : pair.Light));
            s_syntaxVariant = variant;
        }

        IBrush? Token(string key) => this.TryFindResource(key, variant, out var value) ? value as IBrush : null;
        Editor.LineNumbersForeground = Token("DbxEditorGutterText");
        Editor.TextArea.SelectionBrush = Token("DbxEditorSelection");
        Editor.TextArea.TextView.CurrentLineBackground = Token("DbxEditorCurrentLine");
        Editor.Options.HighlightCurrentLine = true;

        // The gutter: a solid hairline with some air on both sides instead of the dotted line AvaloniaEdit draws.
        foreach (var rule in Editor.TextArea.LeftMargins.OfType<Line>())
        {
            rule.StrokeDashArray = null;
            rule.Stroke = Token("DbxEditorGutterBorder");
            rule.Margin = new Thickness(8, 0, 0, 0);
        }

        _marks.OkBrush = Token("DbxSuccess");
        _marks.ErrorBrush = Token("DbxError");
        _marks.BandBrush = Token("DbxAccentSoft");
        Editor.TextArea.TextView.Redraw();
    }

    // The tab control recreates this view when the tab is selected again: always follow the current view-model.
    private void Attach(ScriptTabViewModel? next)
    {
        if (ReferenceEquals(_vm, next)) return;
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.Results.CollectionChanged -= OnResultsChanged;
            _vm.RevealRequested -= OnRevealRequested;
        }
        _vm = next;
        _marks.Clear();
        if (_vm is null) return;

        _vm.PropertyChanged += OnViewModelChanged;
        _vm.Results.CollectionChanged += OnResultsChanged;
        _vm.RevealRequested += OnRevealRequested;
        _vm.EditorAttached();
        _syncing = true;
        try
        {
            Editor.Text = _vm.Text;
            Editor.CaretOffset = Math.Min(_vm.CaretOffset, Editor.Document.TextLength);
        }
        finally
        {
            _syncing = false;
        }
        UpdateElapsedTimer();
        RefreshMarks();
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncing || _vm is null) return;
        _vm.Text = Editor.Text;
        RefreshMarks();   // the marks point into the text the run used: they go as soon as it is edited
    }

    private void PushCaretAndSelection()
    {
        if (_syncing || _vm is null) return;
        _vm.CaretOffset = Editor.CaretOffset;
        _vm.Selection = Editor.SelectionLength > 0
            ? (Editor.SelectionStart, Editor.SelectionStart + Editor.SelectionLength)
            : null;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName == nameof(ScriptTabViewModel.Text) && Editor.Text != _vm.Text)
        {
            _syncing = true;
            try { Editor.Text = _vm.Text; }
            finally { _syncing = false; }
            RefreshMarks();
        }
        else if (e.PropertyName == nameof(ScriptTabViewModel.CaretOffset) && Editor.CaretOffset != _vm.CaretOffset)
        {
            _syncing = true;
            try { Editor.CaretOffset = Math.Min(_vm.CaretOffset, Editor.Document.TextLength); }
            finally { _syncing = false; }
        }
        else if (e.PropertyName == nameof(ScriptTabViewModel.SelectedResult))
        {
            RefreshMarks();
        }
        else if (e.PropertyName == nameof(ScriptTabViewModel.RunStartedAt))
        {
            UpdateElapsedTimer();
        }
    }

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshMarks();

    // What the last run did to the text: a bar for each statement that ran, a band over the one whose tab is selected.
    private void RefreshMarks()
    {
        if (_vm is null || !_vm.ResultsMatchText || _vm.Results.Count == 0)
        {
            if (_marks.HasMarks) _marks.Clear();
        }
        else
        {
            var marks = _vm.Results.Where(r => r.HasRange)
                .Select(r => new StatementMarkRenderer.Mark(r.StartOffset, r.EndOffset, r.HasError)).ToList();
            var selected = _vm.SelectedResult is { HasRange: true } s ? (s.StartOffset, s.EndOffset) : ((int, int)?)null;
            _marks.Set(marks, selected);
        }
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    // "Go to statement": select it in the editor and bring it into view.
    private void OnRevealRequested(int start, int end)
    {
        var length = Editor.Document.TextLength;
        start = Math.Clamp(start, 0, length);
        end = Math.Clamp(end, start, length);
        Editor.Select(start, end - start);
        Editor.TextArea.Caret.BringCaretToView();
        Editor.Focus();
    }

    // ---- elapsed time of the running script (the view-model only says when it started) ----

    private void UpdateElapsedTimer()
    {
        if (_vm?.RunStartedAt is null)
        {
            StopElapsedTimer();
            return;
        }
        ShowElapsed();
        if (_elapsedTimer is null)
        {
            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _elapsedTimer.Tick += (_, _) => ShowElapsed();
        }
        _elapsedTimer.Start();
    }

    private void StopElapsedTimer()
    {
        _elapsedTimer?.Stop();
        ElapsedText.Text = "";
    }

    private void ShowElapsed()
    {
        if (_vm?.RunStartedAt is not { } started) return;
        var elapsed = DateTime.Now - started;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        ElapsedText.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    // ---- toolbar: row limit, run on group ----

    private void OnRowLimitPresetClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { Tag: int limit }) return;
        _vm.RowLimit = limit;
        RowLimitButton.Flyout?.Hide();
    }

    private void OnCustomRowLimitKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _vm is null || sender is not TextBox box) return;
        e.Handled = true;
        var text = (box.Text ?? "").Replace(",", "").Replace(" ", "").Replace(" ", "");
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)) return;   // not a number: stay open
        _vm.RowLimit = limit;   // clamped to 1 .. 100,000
        box.Text = "";
        RowLimitButton.Flyout?.Hide();
    }

    private void OnRunStatementOnGroupMenu(object? sender, RoutedEventArgs e)
    {
        if (_vm is { CanRun: true } vm) _ = RunSafe(vm, vm.RunStatementOnGroupAsync);
    }

    private void OnRunScriptOnGroupMenu(object? sender, RoutedEventArgs e)
    {
        if (_vm is { CanRun: true } vm) _ = RunSafe(vm, vm.RunScriptOnGroupAsync);
    }

    // ---- results ----

    private ResultSetViewModel? _exportSet;

    private ResultGrid? GridOf(ResultSetViewModel set) =>
        this.GetVisualDescendants().OfType<ResultGrid>().FirstOrDefault(g => ReferenceEquals(g.ItemsSource, set.Rows));

    // The selected rows in the order of the result, or null when none are selected (then all rows are used).
    private IReadOnlyList<object?[]>? SelectedRowsOf(ResultSetViewModel set)
    {
        if (GridOf(set)?.Grid.SelectedItems is not { Count: > 0 } items) return null;
        var picked = new HashSet<object>(items.Cast<object>(), ReferenceEqualityComparer.Instance);
        return set.Rows.Where(picked.Contains).ToList();
    }

    private void OnResultSelectionChanged(object? sender, EventArgs e)
    {
        if (sender is ResultGrid { DataContext: ResultSetViewModel set } grid) set.SelectedCount = grid.Grid.SelectedItems.Count;
    }

    private async void OnCopyResultClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || (sender as Control)?.DataContext is not ResultSetViewModel set) return;
        try
        {
            var text = vm.CopyText(set, SelectedRowsOf(set));
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // the clipboard can be busy: nothing to report for a copy
        }
    }

    // The menu items do not inherit the data context of the button: the button says which set the menu is about.
    private void OnExportMenuClick(object? sender, RoutedEventArgs e) => _exportSet = (sender as Control)?.DataContext as ResultSetViewModel;

    private void OnExportCsvClick(object? sender, RoutedEventArgs e) => Export(ExportFormat.Csv);

    private void OnExportJsonClick(object? sender, RoutedEventArgs e) => Export(ExportFormat.Json);

    private void Export(ExportFormat format)
    {
        if (_vm is not { } vm || _exportSet is not { } set) return;
        _ = RunSafe(vm, () => vm.ExportAsync(set, format, SelectedRowsOf(set)));
    }

    private void OnRaiseLimitClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is { } vm) _ = RunSafe(vm, vm.RaiseRowLimitAndRerunAsync);
    }

    private void OnGoToStatementClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is { } vm && (sender as Control)?.DataContext is StatementResultViewModel result) vm.GoToStatement(result);
    }

    private async void OnCopyErrorClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not StatementResultViewModel { Error: { } error }) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(error);
        }
        catch (Exception)
        {
            // the clipboard can be busy: nothing to report for a copy
        }
    }

    // The two halves of "All rows | Differences" exclude each other: clicking the one that is on keeps it on.
    private void OnSegmentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { IsChecked: not true } button) button.IsChecked = true;
    }

    // ---- editor and results panes ----

    // Double-click on the splitter: the results take the whole area, again to go back.
    private void OnSplitterDoubleTapped(object? sender, TappedEventArgs e)
    {
        var rows = SplitGrid.RowDefinitions;
        if (_restoreSplit is { } restore)
        {
            rows[0].MinHeight = restore.EditorMin;
            rows[0].Height = restore.Editor;
            rows[2].Height = restore.Results;
            _restoreSplit = null;
            Editor.Focus();
        }
        else
        {
            _restoreSplit = (rows[0].Height, rows[2].Height, rows[0].MinHeight);
            rows[0].MinHeight = 0;
            rows[0].Height = new GridLength(0);
            rows[2].Height = new GridLength(1, GridUnitType.Star);
        }
        e.Handled = true;
    }

    // F6: the editor and the results take turns to have the focus.
    private void SwitchPane()
    {
        if (Editor.IsKeyboardFocusWithin) ResultTabStrip.Focus();
        else Editor.Focus();
    }

    // Ctrl+1 to Ctrl+9: the Nth result tab (of the group run when there is one).
    private bool SelectResultTab(int index)
    {
        if (_vm is null) return false;
        if (_vm.GroupRun is { } run)
        {
            if (index >= run.Tabs.Count) return false;
            run.SelectedTab = run.Tabs[index];
            return true;
        }
        if (index >= _vm.ResultTabs.Count) return false;
        _vm.SelectedResultTab = _vm.ResultTabs[index];
        return true;
    }

    // Ctrl+/: puts "-- " in front of the selected lines, or takes it off when they all have it.
    private void ToggleComment()
    {
        var document = Editor.Document;
        var selectionStart = Editor.SelectionStart;
        var selectionEnd = selectionStart + Editor.SelectionLength;
        var first = document.GetLineByOffset(selectionStart).LineNumber;
        var last = document.GetLineByOffset(selectionEnd).LineNumber;
        if (selectionEnd > selectionStart && last > first && document.GetLineByNumber(last).Offset == selectionEnd) last--;   // the line the selection only touches

        var lines = Enumerable.Range(first, last - first + 1).Select(document.GetLineByNumber)
            .Select(l => (Line: l, Text: document.GetText(l.Offset, l.Length)))
            .Where(l => l.Text.Trim().Length > 0).ToList();
        if (lines.Count == 0) return;
        var uncomment = lines.All(l => l.Text.TrimStart().StartsWith("--", StringComparison.Ordinal));

        using (document.RunUpdate())
        {
            foreach (var (line, text) in Enumerable.Reverse(lines))
            {
                var indent = text.Length - text.TrimStart().Length;
                if (uncomment)
                    document.Remove(line.Offset + indent, text.Length > indent + 2 && text[indent + 2] == ' ' ? 3 : 2);
                else
                    document.Insert(line.Offset + indent, "-- ");
            }
        }
    }

    // Tunnelling: runs before the editor sees the key, so Ctrl+Enter never inserts a line break.
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is not { } vm) return;
        Func<Task>? action = null;
        switch (ScriptTabKeys.Resolve(e.Key, e.KeyModifiers))
        {
            case ScriptKeyAction.RunStatement: action = vm.RunStatementAsync; break;
            case ScriptKeyAction.RunScript: action = vm.RunScriptAsync; break;
            case ScriptKeyAction.RunStatementOnGroup: action = vm.RunStatementOnGroupAsync; break;
            case ScriptKeyAction.RunScriptOnGroup: action = vm.RunScriptOnGroupAsync; break;
            case ScriptKeyAction.Save: action = () => vm.SaveAsync(null); break;
            case ScriptKeyAction.EstimatedPlan: action = vm.ShowEstimatedPlanAsync; break;
            case ScriptKeyAction.ToggleActualPlan:
                if (vm.CanUseActualPlan) vm.ActualPlanEnabled = !vm.ActualPlanEnabled;
                e.Handled = true;
                return;
            case ScriptKeyAction.Cancel:
                vm.Cancel();   // also cancels a group run
                e.Handled = true;
                return;
            case ScriptKeyAction.CancelIfBusy:
                if (!vm.IsBusy) return;   // Esc keeps its other jobs (find panel, completion) when nothing runs
                vm.Cancel();
                e.Handled = true;
                return;
            case ScriptKeyAction.ToggleComment:
                if (!Editor.IsKeyboardFocusWithin) return;
                ToggleComment();
                e.Handled = true;
                return;
            case ScriptKeyAction.SwitchPane:
                SwitchPane();
                e.Handled = true;
                return;
            case ScriptKeyAction.Complete:
                if (!Editor.IsKeyboardFocusWithin) return;
                ShowCompletion();
                e.Handled = true;
                return;
            default:
                if (ScriptTabKeys.ResultTabIndex(e.Key, e.KeyModifiers) is { } index && SelectResultTab(index)) e.Handled = true;
                return;
        }
        if (action is null) return;

        e.Handled = true;
        _ = RunSafe(vm, action);
    }

    // The view-model reports its own failures; this only keeps an unexpected one from crashing the app.
    private static async Task RunSafe(ScriptTabViewModel vm, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { vm.ErrorMessage = ex.Message; }
    }
}
