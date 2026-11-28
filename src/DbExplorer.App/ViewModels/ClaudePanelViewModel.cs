using System.Text;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Claude;
using DbExplorer.Core.Claude.Assist;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>Where a question is: typing it, reading what would be sent, waiting for the answer, or done.</summary>
public enum ClaudePanelPhase { Input, Preview, Running, Done, Failed }

/// <summary>One line of the proposed change.</summary>
public sealed record ClaudeDiffRow(string Sign, string Text, bool IsAdded, bool IsRemoved);

/// <summary>Puts the proposed SQL into the editor; returns why it could not, or null. Nothing is ever run.</summary>
public delegate string? ClaudeApply(string proposed);

/// <summary>
/// The side panel of one question to Claude: shows exactly what will be sent (always before the first question of a
/// connection), streams the answer, and offers proposed SQL as a difference the user applies or rejects.
/// </summary>
public sealed partial class ClaudePanelViewModel : ObservableObject
{
    private readonly ClaudeAssistant _assistant;
    private readonly IReadOnlyList<Guid> _connections;
    private readonly Func<ClaudeAccess, string?, ClaudePayload> _build;
    private readonly string _original;
    private readonly ClaudeApply? _apply;
    private ClaudePayload? _payload;
    private CancellationTokenSource? _cts;
    private string? _proposed;

    /// <param name="build">The payload for an access level and the typed request; it may throw <see cref="ClaudeRefusedException"/>.</param>
    /// <param name="original">The SQL a proposal would replace (empty when it would be inserted).</param>
    /// <param name="apply">Null: the answer is only shown.</param>
    /// <param name="needsInput">The user types what they want first.</param>
    public ClaudePanelViewModel(ClaudeAssistant assistant, string title, IReadOnlyList<Guid> connections,
        Func<ClaudeAccess, string?, ClaudePayload> build, string original = "", ClaudeApply? apply = null, bool needsInput = false)
    {
        _assistant = assistant;
        Title = title;
        _connections = connections;
        _build = build;
        _original = original;
        _apply = apply;
        NeedsInput = needsInput;
        _phase = needsInput ? ClaudePanelPhase.Input : ClaudePanelPhase.Preview;
    }

    public string Title { get; }

    public bool NeedsInput { get; }

    [NotifyPropertyChangedFor(nameof(IsInput), nameof(IsPreview), nameof(IsRunning), nameof(IsDone), nameof(IsFailed),
        nameof(ShowAnswer), nameof(CanGoBack), nameof(CanAskAgain),
        nameof(CanToggleSent))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand), nameof(SendCommand), nameof(StopCommand), nameof(ApplyCommand))]
    [ObservableProperty] private ClaudePanelPhase _phase;

    public bool IsInput => Phase == ClaudePanelPhase.Input;
    public bool IsPreview => Phase == ClaudePanelPhase.Preview;
    public bool IsRunning => Phase == ClaudePanelPhase.Running;
    public bool IsDone => Phase == ClaudePanelPhase.Done;
    public bool IsFailed => Phase == ClaudePanelPhase.Failed;
    public bool ShowAnswer => Phase is ClaudePanelPhase.Running or ClaudePanelPhase.Done or ClaudePanelPhase.Failed && Answer.Length > 0;
    public bool CanGoBack => NeedsInput && Phase is ClaudePanelPhase.Preview or ClaudePanelPhase.Done or ClaudePanelPhase.Failed;
    public bool CanAskAgain => Phase == ClaudePanelPhase.Failed;

    /// <summary>In the preview the text is always shown; afterwards it can be unfolded.</summary>
    public bool CanToggleSent => Phase is not (ClaudePanelPhase.Input or ClaudePanelPhase.Preview) && SentText.Length > 0;

    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    [ObservableProperty] private string _request = "";

    /// <summary>The whole text that is sent: the fixed instructions, then the message.</summary>
    [ObservableProperty] private string _sentText = "";

    /// <summary>What that text holds, in words.</summary>
    [ObservableProperty] private IReadOnlyList<string> _contents = [];

    /// <summary>"Schema only" or "Schema and data", with the connections.</summary>
    [ObservableProperty] private string _accessText = "";

    /// <summary>The sent text is unfolded (always in the preview).</summary>
    [ObservableProperty] private bool _showSent;

    [NotifyPropertyChangedFor(nameof(ShowAnswer))]
    [ObservableProperty] private string _answer = "";

    /// <summary>What is going on while waiting.</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>Why nothing came, or a note about the proposal.</summary>
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    [ObservableProperty] private string? _message;

    public bool HasMessage => Message is not null;

    [NotifyPropertyChangedFor(nameof(HasProposal), nameof(CanApply))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(RejectCommand))]
    [ObservableProperty] private IReadOnlyList<ClaudeDiffRow> _diff = [];

    public bool HasProposal => Diff.Count > 0;

    /// <summary>Null, or why the proposal cannot be applied.</summary>
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [ObservableProperty] private string? _applyDisabledReason;

    public bool CanApply => HasProposal && _apply is not null && ApplyDisabledReason is null && Phase == ClaudePanelPhase.Done;

    /// <summary>Raised by Close: the owner removes the panel.</summary>
    public event Action? Closed;

    /// <summary>The first step: asks for the request, or shows the preview, or sends at once when the connection was previewed.</summary>
    public Task StartAsync() => NeedsInput ? Task.CompletedTask : PrepareAsync();

    private bool CanContinue() => Phase == ClaudePanelPhase.Input && !string.IsNullOrWhiteSpace(Request);

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private Task ContinueAsync() => PrepareAsync();

    [RelayCommand]
    private void Back()
    {
        if (!CanGoBack) return;
        ClearAnswer();
        Phase = ClaudePanelPhase.Input;
    }

    private Task PrepareAsync()
    {
        ClearAnswer();
        if (!TryBuild()) return Task.CompletedTask;
        if (_assistant.NeedsPreview(_connections))
        {
            ShowSent = true;
            Phase = ClaudePanelPhase.Preview;
            return Task.CompletedTask;
        }
        ShowSent = false;
        return RunAsync();
    }

    private bool TryBuild()
    {
        var grant = _assistant.GrantFor(_connections);
        if (!grant.Allowed) return Fail(grant.Refusal ?? "Not allowed.");
        try
        {
            _payload = _assistant.Scrubbed(_build(grant.Access, NeedsInput ? Request.Trim() : null));
        }
        catch (ClaudeRefusedException e)
        {
            return Fail(e.Message);
        }
        catch (Exception e)
        {
            return Fail("The question could not be prepared: " + e.Message);
        }
        SentText = $"Instructions\n{ClaudeHeadless.SystemPrompt}\n\nMessage\n{_payload.Prompt}";
        Contents = _payload.Contents;
        AccessText = (grant.Access == ClaudeAccess.SchemaAndData ? "Schema and data" : "Schema only")
                     + " · " + string.Join(", ", _connections.Select(_assistant.NameOf));
        return true;
    }

    private bool Fail(string why)
    {
        Message = why;
        Phase = ClaudePanelPhase.Failed;
        return false;
    }

    private bool CanSend() => Phase == ClaudePanelPhase.Preview;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task SendAsync() => RunAsync(accepted: true);

    [RelayCommand]
    private Task RetryAsync() => Phase == ClaudePanelPhase.Failed ? PrepareAsync() : Task.CompletedTask;

    private async Task RunAsync(bool accepted = false)
    {
        if (_payload is not { } payload) return;
        // The settings may have changed while the preview was open.
        var grant = _assistant.GrantFor(_connections);
        if (!grant.Allowed)
        {
            Fail(grant.Refusal ?? "Not allowed.");
            return;
        }
        if (grant.Access != payload.Access)
        {
            if (!TryBuild()) return;
            Message = "The access of this connection changed: check what is sent again.";
            ShowSent = true;
            Phase = ClaudePanelPhase.Preview;
            return;
        }

        var cts = _cts = new CancellationTokenSource();
        Message = null;
        Answer = "";
        ShowSent = false;
        StatusText = "Waiting for Claude…";
        Phase = ClaudePanelPhase.Running;
        try
        {
            _assistant.Record(payload.Task, _connections, payload.Access, "sent");
            if (accepted) _assistant.MarkPreviewed(_connections);

            var updates = Channel.CreateUnbounded<ClaudeUpdate>();
            var asking = _assistant.AskAsync(payload, u => updates.Writer.TryWrite(u), cts.Token);
            _ = asking.ContinueWith(_ => updates.Writer.TryComplete(), TaskScheduler.Default);
            var text = new StringBuilder();
            // Read on the caller's thread; everything that is waiting is shown in one change.
            while (await updates.Reader.WaitToReadAsync(cts.Token))
            {
                var grew = false;
                while (updates.Reader.TryRead(out var update))
                {
                    if (update.Text is { } more) { text.Append(more); grew = true; StatusText = "Claude is answering…"; }
                    if (update.Status is { } status) StatusText = status;
                }
                if (grew) Answer = text.ToString();
            }
            var answer = await asking;
            if (cts.IsCancellationRequested) throw new OperationCanceledException(cts.Token);
            if (answer.Text.Length > 0) Answer = answer.Text;
            if (answer.Failure is not null)
            {
                Fail(answer.Message ?? "Claude Code failed.");
                return;
            }
            Phase = ClaudePanelPhase.Done;
            Propose(payload, answer.Text);
        }
        catch (OperationCanceledException)
        {
            Fail("Stopped.");
        }
        catch (Exception e)
        {
            Fail("The question failed: " + e.Message);
        }
        finally
        {
            StatusText = "";
            if (_cts == cts) _cts = null;
            cts.Dispose();
        }
    }

    // The answer is text from outside: what is offered for the editor is shown as it would run.
    private void Propose(ClaudePayload payload, string answer)
    {
        if (_apply is null || ClaudeProposal.Find(answer) is not { } block) return;
        var complete = true;
        var sql = MainViewModel.Visible(block, keepLines: true);
        if (payload.Redactor is { } redactor) sql = redactor.Restore(sql, out complete);
        // The statement was taken without its ";": the one in the script stays, a second one is not added.
        if (_original.Length > 0 && !_original.TrimEnd().EndsWith(';')) sql = sql.TrimEnd().TrimEnd(';');
        var rows = ClaudeProposal.Diff(_original, sql);
        if (rows.All(r => r.Kind == DiffKind.Same))
        {
            Message = "The proposed SQL is the same as yours.";
            return;
        }
        _proposed = sql;
        // The block is shown as the difference, with the values back in it.
        Answer = ClaudeProposal.Without(Answer);
        ApplyDisabledReason = complete ? null : "The proposal holds a placeholder that cannot be turned back into your value.";
        Diff = rows.Select(r => new ClaudeDiffRow(r.Kind == DiffKind.Added ? "+" : r.Kind == DiffKind.Removed ? "−" : " ",
            r.Text, r.Kind == DiffKind.Added, r.Kind == DiffKind.Removed)).ToList();
    }

    private bool CanStop() => Phase == ClaudePanelPhase.Running;

    /// <summary>Kills the claude process and everything it started.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    public void Stop()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* it just ended */ }
    }

    /// <summary>Puts the proposal in the editor as one undo step. Nothing is run.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (!CanApply || _proposed is null) return;
        if (_apply!(_proposed) is { } why)
        {
            ApplyDisabledReason = why;
            Message = why;
            return;
        }
        ClearProposal();
        Message = "Applied to the editor. Nothing was run; Ctrl+Z undoes it.";
    }

    [RelayCommand(CanExecute = nameof(HasProposal))]
    private void Reject()
    {
        ClearProposal();
        Message = "Rejected. Your SQL is unchanged.";
    }

    [RelayCommand]
    public void Close()
    {
        Stop();
        Closed?.Invoke();
    }

    private void ClearProposal()
    {
        _proposed = null;
        Diff = [];
        ApplyDisabledReason = null;
    }

    private void ClearAnswer()
    {
        ClearProposal();
        Answer = "";
        Message = null;
    }
}
