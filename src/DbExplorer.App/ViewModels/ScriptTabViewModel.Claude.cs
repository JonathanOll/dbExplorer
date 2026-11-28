using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Claude.Assist;
using DbExplorer.Core.Model;
using DbExplorer.Core.Plans;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.ViewModels;

/// <summary>The questions a script tab can ask Claude: explain an error, optimise, write SQL, analyse a plan.</summary>
public sealed partial class ScriptTabViewModel
{
    /// <summary>The open side panel, or null.</summary>
    [ObservableProperty] private ClaudePanelViewModel? _claudePanel;

    private ClaudeAssistant? Assistant => _hooks.Claude;

    /// <summary>The Claude menu shows: the integration is on. Off, nothing of it appears.</summary>
    public bool ClaudeEnabled => Assistant?.Enabled == true;

    /// <summary>Why nothing may be asked about this tab's connection; null when something may.</summary>
    public string? ClaudeDisabledReason =>
        Assistant is not { } assistant ? "Not available here"
        : assistant.GrantFor([Connection.Id]) is { Allowed: false } refused ? refused.Refusal
        : null;

    public bool CanAskClaude => ClaudeDisabledReason is null;

    public string ClaudeTip => ClaudeDisabledReason ?? "Ask Claude Code about this script. You see what is sent; nothing is ever run.";

    /// <summary>The settings changed (or the menu opens): read the access again.</summary>
    public void RefreshClaude()
    {
        OnPropertyChanged(nameof(ClaudeEnabled));
        OnPropertyChanged(nameof(ClaudeDisabledReason));
        OnPropertyChanged(nameof(CanAskClaude));
        OnPropertyChanged(nameof(ClaudeTip));
        foreach (var plan in ResultTabs.OfType<PlanResultTab>()) plan.RefreshAnalyse();
        if (!ClaudeEnabled) ClaudePanel?.Close();
    }

    private IReadOnlyList<TableInfo> LoadedSchema => (_session ?? _hooks.FindSession?.Invoke(Connection.Id))?.Schema ?? [];

    private Task Open(ClaudePanelViewModel panel)
    {
        ClaudePanel?.Close();
        panel.Closed += () =>
        {
            if (ReferenceEquals(ClaudePanel, panel)) ClaudePanel = null;
        };
        ClaudePanel = panel;
        return panel.StartAsync();
    }

    private bool Refused()
    {
        if (ClaudeDisabledReason is not { } why) return false;
        _status.Info(why);
        return true;
    }

    private (int Start, int End)? TargetRange()
    {
        var statements = StatementsAtCaretOrSelection(Connection.Kind);
        if (Selection is { } sel && sel.Start != sel.End)
            return (Math.Clamp(Math.Min(sel.Start, sel.End), 0, Text.Length), Math.Clamp(Math.Max(sel.Start, sel.End), 0, Text.Length));
        return statements.Count == 0 ? null : (statements[0].Start, statements[^1].End);
    }

    // Replaces the range the question was about, only while the script and the connection are those of the question.
    private ClaudeApply Replace(int start, string original, Guid connection)
    {
        var asked = Text;
        return proposed => ApplyProposal(proposed, asked, start, original, connection);
    }

    private string? ApplyProposal(string proposed, string asked, int start, string original, Guid connection)
    {
        if (_closed) return "The tab is closed.";
        if (Connection.Id != connection) return "The tab was switched to another database since the question: ask again.";
        var text = Text;
        // For an error, the question is about the text of the run, which may be older than the question.
        if (text != asked || start + original.Length > text.Length
                          || string.CompareOrdinal(text, start, original, 0, original.Length) != 0)
            return "The script changed since the question: ask again.";
        var at = start;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var replacement = proposed.ReplaceLineEndings(newline);
        // Inserted text starts on its own line.
        if (original.Length == 0)
        {
            if (at > 0 && text[at - 1] is not ('\n' or '\r')) replacement = newline + replacement;
            if (at < text.Length && text[at] is not ('\n' or '\r')) replacement += newline;
        }
        var edit = new ScriptTextEdit(at, original.Length, replacement, at + replacement.Length, at, replacement.Length);
        if (FormatRequested is { } apply) apply(edit);   // the editor applies it as one undo step
        else Text = string.Concat(text.AsSpan(0, at), replacement, text.AsSpan(at + original.Length));
        return null;
    }

    /// <summary>Explains the error of the last run (of <paramref name="result"/>, else of the statement that failed).</summary>
    [RelayCommand]
    public Task ExplainErrorAsync(StatementResultViewModel? result = null)
    {
        if (Assistant is not { } assistant || Refused()) return Task.CompletedTask;
        var (shared, note) = ErrorToShare;
        result ??= Results.LastOrDefault(r => r.HasError);
        var error = shared is null ? null : result?.Error ?? shared;
        if (error is null)
        {
            _status.Info(note ?? "There is no error of the last run on this database to explain.");
            return Task.CompletedTask;
        }

        var (start, end) = result is { HasRange: true } && result.EndOffset <= _runText.Length
            ? (result.StartOffset, result.EndOffset)
            : _lastStatements.Count > 0 ? (_lastStatements[0].Start, _lastStatements[^1].End) : (0, _runText.Length);
        var sql = _runText[Math.Clamp(start, 0, _runText.Length)..Math.Clamp(end, 0, _runText.Length)];
        var connection = Connection;
        var schema = LoadedSchema;
        return Open(new ClaudePanelViewModel(assistant, "Explain error", [connection.Id],
            (access, _) => ClaudePayload.Build(
                new ClaudeQuestion(ClaudeTask.ExplainError, connection.Kind) { Sql = sql, Error = error, Schema = schema }, access),
            sql, Replace(start, sql, connection.Id)));
    }

    /// <summary>Optimises the selection or the statement at the caret, with its plan and statistics when the last run has them.</summary>
    [RelayCommand]
    public Task OptimiseQueryAsync()
    {
        if (Assistant is not { } assistant || Refused()) return Task.CompletedTask;
        if (TargetRange() is not { } range || range.End <= range.Start)
        {
            _status.Info("Put the caret in a statement, or select it, first.");
            return Task.CompletedTask;
        }
        var sql = Text[range.Start..range.End];
        var connection = Connection;
        var schema = LoadedSchema;

        // Only what the last run measured on this database, outside production, for these very statements.
        var usable = !_lastRunWasGroup && !_lastRunOnProduction && _lastRunConnection == connection.Id;
        var wanted = ScriptSplitter.SplitForRun(sql, connection.Kind).Select(s => s.Text.Trim()).ToHashSet(StringComparer.Ordinal);
        var ran = _lastStatements.Select((s, i) => (Index: i + 1, Text: s.Text.Trim())).Where(s => wanted.Contains(s.Text))
            .Select(s => s.Index).ToHashSet();
        var plans = !usable ? [] : ResultTabs.OfType<PlanResultTab>()
            .SelectMany(t => t.Statements.Where(s => ran.Contains(s.Statement.ScriptStatement)).Select(s => (s.Statement, t.Mode)))
            .ToList();
        var statistics = !usable ? [] : Results.Where(r => ran.Contains(r.Index))
            .SelectMany(r => r.Messages.SelectMany(m => StatisticsParser.Parse(m.Text))).ToList();

        return Open(new ClaudePanelViewModel(assistant, "Optimise query", [connection.Id],
            (access, _) => ClaudePayload.Build(new ClaudeQuestion(ClaudeTask.OptimiseQuery, connection.Kind)
                { Sql = sql, Plans = plans, Statistics = statistics, Schema = schema }, access),
            sql, Replace(range.Start, sql, connection.Id)));
    }

    /// <summary>Writes SQL from a description; it replaces the selection or is inserted at the caret when applied.</summary>
    [RelayCommand]
    public Task GenerateSqlAsync()
    {
        if (Assistant is not { } assistant || Refused()) return Task.CompletedTask;
        var text = Text;
        var (start, end) = Selection is { } sel
            ? (Math.Clamp(Math.Min(sel.Start, sel.End), 0, text.Length), Math.Clamp(Math.Max(sel.Start, sel.End), 0, text.Length))
            : (Math.Clamp(CaretOffset, 0, text.Length), Math.Clamp(CaretOffset, 0, text.Length));
        var original = text[start..end];
        var connection = Connection;
        return Open(new ClaudePanelViewModel(assistant, "Generate SQL", [connection.Id],
            (access, request) => ClaudePayload.Build(new ClaudeQuestion(ClaudeTask.GenerateSql, connection.Kind)
                { Description = request, Sql = original.Length > 0 ? original : null, Schema = LoadedSchema }, access),
            original, Replace(start, original, connection.Id), needsInput: true));
    }

    /// <summary>Explains the costly operators of a plan and suggests indexes; the answer is only shown.</summary>
    public Task AnalysePlanAsync(PlanResultTab? tab = null)
    {
        if (Assistant is not { } assistant || Refused()) return Task.CompletedTask;
        tab ??= SelectedResultTab as PlanResultTab ?? ResultTabs.OfType<PlanResultTab>().LastOrDefault();
        if (tab?.Selected is not { } selected)
        {
            _status.Info("There is no execution plan to analyse: show one first (Ctrl+L).");
            return Task.CompletedTask;
        }
        if (_lastRunOnProduction || _lastRunConnection != Connection.Id || !ResultTabs.Contains(tab))
        {
            _status.Info("This plan is not from the database of the tab now: run it again.");
            return Task.CompletedTask;
        }
        var statement = selected.Statement;
        var mode = tab.Mode;
        var connection = Connection;
        var index = statement.ScriptStatement - 1;
        var sql = index >= 0 && index < _lastStatements.Count ? _lastStatements[index].Text : statement.Text;
        var schema = LoadedSchema;
        return Open(new ClaudePanelViewModel(assistant, "Analyse plan", [connection.Id],
            (access, _) => ClaudePayload.Build(new ClaudeQuestion(ClaudeTask.AnalysePlan, connection.Kind)
                { Sql = sql, Plans = [(statement, mode)], Schema = schema }, access)));
    }
}
