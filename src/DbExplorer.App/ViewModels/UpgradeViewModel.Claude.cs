using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Claude.Assist;

namespace DbExplorer.App.ViewModels;

/// <summary>"Review with Claude": the ticked scripts are read for risks before they run. Nothing is run by it.</summary>
public sealed partial class UpgradeViewModel
{
    private ClaudeAssistant? _claude;

    [ObservableProperty] private ClaudePanelViewModel? _claudePanel;

    /// <summary>Gives the dialog the questions to Claude; without it nothing of them shows.</summary>
    public void UseClaude(ClaudeAssistant? claude)
    {
        _claude = claude;
        RefreshClaude();
    }

    /// <summary>The button shows: the integration is on and the scripts are being chosen or confirmed.</summary>
    public bool ShowClaudeReview => _claude?.Enabled == true && Phase is UpgradePhase.Preview or UpgradePhase.Confirm;

    public string? ClaudeReviewDisabledReason =>
        _claude is null || SelectedTarget is not { } target ? "Not available"
        : _claude.GrantFor([.. target.Members.Select(m => m.Id)]) is { Allowed: false } refused ? refused.Refusal
        : !Rows.Any(r => r.IsSelected) ? "Tick at least one script to review."
        : null;

    public bool CanReviewWithClaude => ClaudeReviewDisabledReason is null;

    public string ClaudeReviewTip => ClaudeReviewDisabledReason
        ?? "Ask Claude Code about locks, long operations and statements that cannot run twice. Nothing is run.";

    private void RefreshClaude()
    {
        OnPropertyChanged(nameof(ShowClaudeReview));
        OnPropertyChanged(nameof(ClaudeReviewDisabledReason));
        OnPropertyChanged(nameof(CanReviewWithClaude));
        OnPropertyChanged(nameof(ClaudeReviewTip));
    }

    [RelayCommand]
    public Task ReviewWithClaudeAsync()
    {
        if (_claude is not { } assistant || !ShowClaudeReview || !CanReviewWithClaude || SelectedTarget is not { } target)
            return Task.CompletedTask;
        var scripts = Rows.Where(r => r.IsSelected && r.Item.Text is not null)
            .Select(r => new ClaudeScript(r.DisplayName, r.Item.Text!)).ToList();
        var kind = target.Members[0].Kind;
        var connections = target.Members.Select(m => m.Id).ToList();
        CloseClaudePanel();
        var panel = new ClaudePanelViewModel(assistant, $"Review of {scripts.Count} {(scripts.Count == 1 ? "script" : "scripts")}",
            connections, (access, _) => ClaudePayload.Build(
                new ClaudeQuestion(ClaudeTask.ReviewUpgrade, kind) { Scripts = scripts }, access));
        panel.Closed += () =>
        {
            if (ReferenceEquals(ClaudePanel, panel)) ClaudePanel = null;
        };
        ClaudePanel = panel;
        return panel.StartAsync();
    }

    /// <summary>The review was about what is no longer on screen, or the dialog closes.</summary>
    public void CloseClaudePanel() => ClaudePanel?.Close();
}
