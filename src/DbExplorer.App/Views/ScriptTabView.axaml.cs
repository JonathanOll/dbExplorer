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
using DbExplorer.App.Settings;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Views;

/// <summary>
/// The script editor tab. The AvaloniaEdit editor lives in this view only, so nothing of it (control, theme, highlighting
/// definition) is created before the first script tab opens. The editor and the view-model are synchronised here:
/// text, caret and selection flow to the view-model; text changes made by the view-model (opening a file) flow back.
/// </summary>
public partial class ScriptTabView : UserControl
{
    private readonly StatementMarkRenderer _marks = new();
    private readonly EditorPairing _pairing;
    private DispatcherTimer? _elapsedTimer;
    private ScriptTabViewModel? _vm;
    private bool _syncing;
    private bool _chordPending;   // Ctrl+K was pressed in the editor: the next key belongs to the chord

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
        // After the completion: a character that ends the word closes the list first, then it is paired.
        _pairing = new EditorPairing(Editor.TextArea, () => _vm?.Connection.Kind, () => SettingsService.Instance.Current.Editor.AutoCloseBrackets);
        Editor.TextArea.TextEntering += _pairing.OnTextEntering;
        ActualThemeVariantChanged += (_, _) => ApplyEditorTheme();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        // A click, or the focus going elsewhere, gives up a pending chord: a key typed much later must not be eaten.
        AddHandler(PointerPressedEvent, (_, _) => _chordPending = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        Editor.TextArea.LostFocus += (_, _) => _chordPending = false;
        DataContextChanged += (_, _) => Attach(DataContext as ScriptTabViewModel);
        DetachedFromVisualTree += (_, _) =>
        {
            SettingsService.Instance.Changed -= OnSettingsChanged;
            Attach(null);
            StopElapsedTimer();
        };
        AttachedToVisualTree += (_, _) =>
        {
            SettingsService.Instance.Changed += OnSettingsChanged;
            ApplyEditorSettings(SettingsService.Instance.Current.Editor);
            Attach(DataContext as ScriptTabViewModel);
            ApplyEditorTheme();
            Editor.Focus();
        };
    }

    private void OnSettingsChanged(AppSettings settings) => ApplyEditorSettings(settings.Editor);

    /// <summary>Editor options of the Settings dialog (font family and size come through the DbxEditorFont* resources).</summary>
    private void ApplyEditorSettings(EditorSettings editor)
    {
        Editor.ShowLineNumbers = editor.LineNumbers;
        Editor.WordWrap = editor.WordWrap;
        Editor.Options.ShowSpaces = editor.ShowWhitespace;
        Editor.Options.ShowTabs = editor.ShowWhitespace;
        Editor.Options.HighlightCurrentLine = editor.HighlightCurrentLine;
        Editor.Options.IndentationSize = editor.TabSize;
        Editor.TextArea.TextView.Redraw();
    }

    // The highlighting definition is a process-wide singleton: its colours are switched once per theme, not once per tab.
    private static ThemeVariant? s_syntaxVariant;

    // TSQL highlighting colour name -> theme token (Theme.axaml)
    private static readonly Dictionary<string, string> SyntaxTokens = new()
    {
        ["Keywords"] = "DbxSyntaxKeyword",
        ["Comment"] = "DbxSyntaxComment",
        ["Char"] = "DbxSyntaxString",   // string literals
    };

    /// <summary>Gives the editor the colours of the current theme: syntax colours, gutter, selection, current line, run marks.</summary>
    private void ApplyEditorTheme()
    {
        var variant = ActualThemeVariant;
        IBrush? Token(string key) => this.TryFindResource(key, variant, out var value) ? value as IBrush : null;

        if (!Equals(s_syntaxVariant, variant) && Editor.SyntaxHighlighting is { } definition)
        {
            var found = false;
            foreach (var color in definition.NamedHighlightingColors)
                if (SyntaxTokens.TryGetValue(color.Name ?? "", out var key) && Token(key) is ISolidColorBrush brush)
                {
                    color.Foreground = new SimpleHighlightingBrush(brush.Color);
                    found = true;
                }
            // Not yet in a tree with the theme resources: try again on the next call instead of caching nothing.
            if (found)
                s_syntaxVariant = variant;
        }

        Editor.LineNumbersForeground = Token("DbxEditorGutterText");
        Editor.TextArea.SelectionBrush = Token("DbxEditorSelection");
        Editor.TextArea.TextView.CurrentLineBackground = Token("DbxEditorCurrentLine");
        Editor.Options.HighlightCurrentLine = SettingsService.Instance.Current.Editor.HighlightCurrentLine;

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
            _vm.FormatRequested -= OnFormatRequested;
        }
        _vm = next;
        _chordPending = false;
        _marks.Clear();
        if (_vm is null) return;

        _vm.PropertyChanged += OnViewModelChanged;
        _vm.Results.CollectionChanged += OnResultsChanged;
        _vm.RevealRequested += OnRevealRequested;
        _vm.FormatRequested += OnFormatRequested;
        _vm.EditorAttached();
        _vm.RefreshClaude();   // access may have changed while the tab was behind
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
        // A search result opened this tab before its editor existed: select the hit now that the text is in.
        if (_vm.TakePendingReveal() is { } reveal) Dispatcher.UIThread.Post(() => OnRevealRequested(reveal.Start, reveal.End), DispatcherPriority.Loaded);
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
        _vm?.TakePendingReveal();   // shown now: nothing is left for a later attach
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

    // ---- questions to Claude ----

    private void OnClaudeExplainClick(object? sender, RoutedEventArgs e) => Ask(vm => vm.ExplainErrorAsync());

    private void OnClaudeOptimiseClick(object? sender, RoutedEventArgs e) => Ask(vm => vm.OptimiseQueryAsync());

    private void OnClaudeGenerateClick(object? sender, RoutedEventArgs e) => Ask(vm => vm.GenerateSqlAsync());

    private void OnClaudeAnalyseClick(object? sender, RoutedEventArgs e) => Ask(vm => vm.AnalysePlanAsync());

    private void OnExplainErrorClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is StatementResultViewModel result) Ask(vm => vm.ExplainErrorAsync(result));
    }

    private void Ask(Func<ScriptTabViewModel, Task> question)
    {
        if (_vm is { } vm) _ = RunSafe(vm, () => question(vm));
    }

    // ---- results ----

    private ResultSetViewModel? _exportSet;

    private ResultGrid? GridOf(ResultSetViewModel set) =>
        this.GetVisualDescendants().OfType<ResultGrid>().FirstOrDefault(g => ReferenceEquals(g.ItemsSource, set.Rows));

    private void OnResultSelectionChanged(object? sender, EventArgs e)
    {
        if (sender is ResultGrid { DataContext: ResultSetViewModel set } grid) set.SelectedCount = grid.SelectedRowCount;
    }

    private async void OnCopyResultClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ResultSetViewModel set || GridOf(set) is not { } grid) return;
        try
        {
            await grid.CopyWithHeadersAsync();
        }
        catch (Exception)
        {
            // the clipboard can be busy: nothing to report for a copy
        }
    }

    // The menu items do not inherit the data context of the button: the button says which set the menu is about.
    private void OnExportMenuClick(object? sender, RoutedEventArgs e) => _exportSet = (sender as Control)?.DataContext as ResultSetViewModel;

    private void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is { } vm && _exportSet is { } set && GridOf(set) is { } grid) _ = RunSafe(vm, () => grid.ShowExportAsync());
    }

    private void OnAdvancedCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is { } vm && _exportSet is { } set && GridOf(set) is { } grid) _ = RunSafe(vm, () => grid.ShowAdvancedCopyAsync());
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

    // Ctrl+Shift+F: the view-model computed the change; here it becomes ONE edit of the document, so Ctrl+Z restores it all.
    private void OnFormatRequested(ScriptTextEdit edit)
    {
        var document = Editor.Document;
        document.UndoStack.StartUndoGroup();
        try
        {
            using (document.RunUpdate()) document.Replace(edit.Start, edit.Length, edit.Text);
        }
        finally
        {
            document.UndoStack.EndUndoGroup();
        }
        if (edit.SelectionLength > 0) Editor.Select(edit.SelectionStart, edit.SelectionLength);
        else Editor.CaretOffset = Math.Min(edit.Caret, document.TextLength);
        Editor.TextArea.Caret.BringCaretToView();
    }

    // Puts "-- " in front of the selected lines (comment: true) or takes it off (false). Ctrl+/ passes null: it takes it
    // off when they all have it, and puts it otherwise.
    private void CommentSelectedLines(bool? comment = null)
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
        static bool IsCommented(string text) => text.TrimStart().StartsWith("--", StringComparison.Ordinal);
        var uncomment = comment is { } wanted ? !wanted : lines.All(l => IsCommented(l.Text));
        if (uncomment) lines.RemoveAll(l => !IsCommented(l.Text));

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
        // The keys typed in the Claude panel are its own: Ctrl+Enter there must never run the script.
        if ((e.Source as Visual)?.FindAncestorOfType<ClaudePanelView>(includeSelf: true) is not null) return;
        ScriptKeyAction resolved;
        if (_chordPending)
        {
            if (ScriptTabKeys.IsModifier(e.Key)) return;   // Ctrl pressed again for the second key
            // Whatever the second key is, it stops here: the editor would take Ctrl+D as "delete line".
            e.Handled = true;
            if (e.Key == Key.K && e.KeyModifiers == KeyModifiers.Control) return;   // Ctrl+K again (or held): still waiting
            _chordPending = false;
            resolved = ScriptTabKeys.ResolveChord(e.Key, e.KeyModifiers);
            if (resolved == ScriptKeyAction.None) return;
        }
        else
        {
            if (Editor.TextArea.IsFocused && HandleEditingKey(e)) return;
            resolved = ScriptTabKeys.Resolve(e.Key, e.KeyModifiers);
        }

        Func<Task>? action = null;
        switch (resolved)
        {
            case ScriptKeyAction.StartChord:
                if (!Editor.IsKeyboardFocusWithin) return;
                _chordPending = true;
                e.Handled = true;
                return;
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
                CommentSelectedLines();
                e.Handled = true;
                return;
            case ScriptKeyAction.CommentLines:
            case ScriptKeyAction.UncommentLines:
                CommentSelectedLines(resolved == ScriptKeyAction.CommentLines);
                return;
            case ScriptKeyAction.FormatSql:
                vm.FormatSqlCommand.Execute(null);
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

    // The keys that edit differently from AvaloniaEdit; true when nothing else has to look at the key.
    private bool HandleEditingKey(KeyEventArgs e)
    {
        if (e.Key == Key.Back && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift) return e.Handled = _pairing.Backspace();
        if (ScriptTabKeys.CollapseSelection(e.Key, e.KeyModifiers) is not { } collapse || Editor.TextArea.Selection.IsEmpty) return false;
        if (Editor.TextArea.Selection.SurroundingSegment is not { } selected) return false;
        Editor.TextArea.ClearSelection();
        Editor.CaretOffset = collapse.ToStart ? selected.Offset : selected.EndOffset;
        if (collapse.ThenMove) return false;   // Up / Down: the editor moves one line from that edge
        Editor.TextArea.Caret.BringCaretToView();
        return e.Handled = true;
    }

    // The view-model reports its own failures; this only keeps an unexpected one from crashing the app.
    private static async Task RunSafe(ScriptTabViewModel vm, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { vm.ErrorMessage = ex.Message; }
    }
}
