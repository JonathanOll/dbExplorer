using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// The script editor tab. The AvaloniaEdit editor lives in this view only, so nothing of it (control, theme, highlighting
/// definition) is created before the first script tab opens. The editor and the view-model are synchronised here:
/// text, caret and selection flow to the view-model; text changes made by the view-model (opening a file) flow back.
/// </summary>
public partial class ScriptTabView : UserControl
{
    private ScriptTabViewModel? _vm;
    private bool _syncing;

    public ScriptTabView()
    {
        InitializeComponent();
        Editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("TSQL");
        Editor.TextChanged += OnEditorTextChanged;
        Editor.TextArea.Caret.PositionChanged += (_, _) => PushCaretAndSelection();
        Editor.TextArea.SelectionChanged += (_, _) => PushCaretAndSelection();
        RowLimitBox.ValueChanged += OnRowLimitChanged;
        ActualThemeVariantChanged += (_, _) => ApplyEditorTheme();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => Attach(DataContext as ScriptTabViewModel);
        DetachedFromVisualTree += (_, _) => Attach(null);
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

    /// <summary>Gives the editor the colours of the current theme: syntax colours, line numbers, selection, current line.</summary>
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
        Editor.LineNumbersForeground = Token("DbxTextFaint");
        Editor.TextArea.SelectionBrush = Token("DbxRowSelected");
        Editor.TextArea.TextView.CurrentLineBackground = Token("DbxRowHover");
        Editor.Options.HighlightCurrentLine = true;
        Editor.TextArea.TextView.Redraw();
    }

    // The tab control recreates this view when the tab is selected again: always follow the current view-model.
    private void Attach(ScriptTabViewModel? next)
    {
        if (ReferenceEquals(_vm, next)) return;
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = next;
        if (_vm is null) return;

        _vm.PropertyChanged += OnViewModelChanged;
        _vm.EditorAttached();
        _syncing = true;
        try
        {
            Editor.Text = _vm.Text;
            Editor.CaretOffset = Math.Min(_vm.CaretOffset, Editor.Document.TextLength);
            RowLimitBox.Value = _vm.RowLimit;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncing || _vm is null) return;
        _vm.Text = Editor.Text;
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
        }
        else if (e.PropertyName == nameof(ScriptTabViewModel.CaretOffset) && Editor.CaretOffset != _vm.CaretOffset)
        {
            _syncing = true;
            try { Editor.CaretOffset = Math.Min(_vm.CaretOffset, Editor.Document.TextLength); }
            finally { _syncing = false; }
        }
        else if (e.PropertyName == nameof(ScriptTabViewModel.RowLimit) && RowLimitBox.Value != _vm.RowLimit)
        {
            RowLimitBox.Value = _vm.RowLimit;
        }
    }

    private void OnRowLimitChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_vm is null || e.NewValue is not { } value) return;
        _vm.RowLimit = (int)Math.Clamp(value, ScriptTabViewModel.MinRowLimit, ScriptTabViewModel.MaxRowLimit);
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
            case ScriptKeyAction.Cancel:
                vm.Cancel();   // also cancels a group run
                e.Handled = true;
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
