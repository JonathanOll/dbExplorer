using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.Views;

/// <summary>
/// Autocomplete of the script editor. The list is AvaloniaEdit's completion window, created the first time it is needed, over
/// the items of <see cref="SqlCompletion"/> (a pure function of the text, the caret and the schema already loaded: nothing is
/// ever queried while typing). It opens on Ctrl+Space, after a '.' that follows a name, and after the second letter of a word;
/// the as-you-type opening is skipped while a script runs and while text is selected.
/// </summary>
public partial class ScriptTabView
{
    private CompletionWindow? _completion;
    private static Application? s_styledApp;

    // The popup lives outside this view's visual tree: its look is added to the application once, on first use (lazy: startup stays lean).
    private static void EnsureCompletionStyles()
    {
        if (Application.Current is not { } app || ReferenceEquals(s_styledApp, app)) return;
        s_styledApp = app;
        app.Styles.Add(new StyleInclude(new Uri("avares://DbExplorer/")) { Source = new Uri("avares://DbExplorer/Views/SqlCompletionStyles.axaml") });
    }

    private void InitCompletion()
    {
        Editor.TextArea.TextEntering += OnTextEntering;
        Editor.TextArea.TextEntered += OnTextEntered;
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            // The list narrows itself on the caret move; once nothing matches any more it has nothing left to show.
            if (_completion is not null) Dispatcher.UIThread.Post(CloseWhenNothingMatches, DispatcherPriority.Background);
        };
        DetachedFromVisualTree += (_, _) => _completion?.Close();
    }

    /// <summary>The completion window while it is open; null otherwise.</summary>
    internal CompletionWindow? OpenCompletion => _completion;

    // Anything but a letter, a digit or '_' ends the word: the list closes and nothing is accepted (Enter and Tab accept).
    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (_completion is { } window && e.Text is { Length: > 0 } text && !IsIdentChar(text[0])) window.Close();
    }

    private void CloseWhenNothingMatches()
    {
        if (_completion is { CompletionList: { ListBox: not null, CurrentList: { Count: 0 } } } window) window.Close();
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_completion is not null || _vm is not { IsBusy: false } || Editor.SelectionLength > 0) return;
        if (e.Text is not { Length: 1 } text) return;

        var caret = Editor.CaretOffset;
        var document = Editor.Document;
        if (text[0] == '.')
        {
            // Only a dot that follows a name: not a decimal point, not an ellipsis.
            if (caret >= 2 && (IsIdentChar(document.GetCharAt(caret - 2)) || document.GetCharAt(caret - 2) is '"' or ']')) ShowCompletion();
        }
        else if (IsIdentChar(text[0]))
        {
            var start = caret;
            while (start > 0 && IsIdentChar(document.GetCharAt(start - 1))) start--;
            if (caret - start >= 2) ShowCompletion();
        }
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>Opens the list at the caret; nothing happens when there is nothing to offer here.</summary>
    internal void ShowCompletion()
    {
        if (_vm is not { } vm) return;
        _completion?.Close();

        var caret = Editor.CaretOffset;
        var items = SqlCompletion.Complete(Editor.Text, caret, vm.CompletionSchema, vm.Connection.Kind);
        if (items.Count == 0) return;

        EnsureCompletionStyles();
        var window = new CompletionWindow(Editor.TextArea);
        var start = items[0].ReplaceStart;
        // The list narrows on what follows an opening quote; the quote itself is part of the replaced text.
        if (start < caret && Editor.Document.GetCharAt(start) is '"' or '[') start++;
        window.StartOffset = start;
        foreach (var item in items) window.CompletionList.CompletionData.Add(new SqlCompletionData(item));
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_completion, window)) _completion = null;
        };
        _completion = window;
        window.Show();
        window.CompletionList.SelectedItem = window.CompletionList.CompletionData[0];   // the best match: Enter takes it
    }
}
