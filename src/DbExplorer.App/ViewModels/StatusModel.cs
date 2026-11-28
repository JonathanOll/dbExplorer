using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbExplorer.App.ViewModels;

public enum StatusSeverity { Info, Success, Warn, Error }

/// <summary>One line of the status history (the popup behind the status bar message).</summary>
public sealed record StatusEntry(DateTime Time, StatusSeverity Severity, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public bool IsError => Severity == StatusSeverity.Error;
    public bool IsWarn => Severity == StatusSeverity.Warn;
    public bool IsSuccess => Severity == StatusSeverity.Success;
}

/// <summary>
/// Status bar state shared by all view models: last message with a severity, a history of the last messages and a busy
/// indicator with an optional cancel. Information and success messages fade after <see cref="AutoClearDelay"/>; warnings and
/// errors stay until the next message or until they are dismissed.
/// </summary>
public partial class StatusModel : ObservableObject
{
    /// <summary>How many messages the history keeps.</summary>
    public const int MaxHistory = 30;

    private readonly List<Action> _cancels = [];
    private int _busyCount;
    private int _generation;
    private Guid? _connectionId;

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private StatusSeverity _severity;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private bool _canCancel;

    /// <summary>How long an information or success message stays; null or zero keeps them for ever.</summary>
    public TimeSpan? AutoClearDelay { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Runs an action after a delay; null = a timer that posts to the UI thread. Replaceable so tests need no clock.</summary>
    public Action<TimeSpan, Action>? Schedule { get; set; }

    /// <summary>The last messages, newest first.</summary>
    public ObservableCollection<StatusEntry> History { get; } = [];

    public bool HasHistory => History.Count > 0;

    public bool IsWarn => Severity == StatusSeverity.Warn;
    public bool IsSuccess => Severity == StatusSeverity.Success;

    /// <summary>The message line is drawn (a message exists and no operation is running: the busy text takes its place).</summary>
    public bool ShowMessage => Message.Length > 0 && !IsBusy;

    /// <summary>True while the message is a warning or an error: it stays until dismissed.</summary>
    public bool IsSticky => Message.Length > 0 && Severity is StatusSeverity.Warn or StatusSeverity.Error;

    public void Info(string message) => Set(message, StatusSeverity.Info);

    public void Success(string message) => Set(message, StatusSeverity.Success);

    /// <summary>A success about one connection: <see cref="ClearOtherConnections"/> drops it when the user moves to another one.</summary>
    public void Success(string message, Guid connectionId)
    {
        Set(message, StatusSeverity.Success);
        _connectionId = connectionId;
    }

    /// <summary>
    /// Removes an information or success message about a connection other than <paramref name="current"/>: once another
    /// connection's tab is active, "Connected to X" reads as if it were about the tab. Warnings and errors stay.
    /// </summary>
    public void ClearOtherConnections(Guid? current)
    {
        if (_connectionId is { } id && id != current && !IsSticky) Clear();
    }

    public void Warn(string message) => Set(message, StatusSeverity.Warn);

    public void Error(string message) => Set(message, StatusSeverity.Error);

    private void Set(string message, StatusSeverity severity)
    {
        var generation = ++_generation;
        _connectionId = null;
        Message = message;
        Severity = severity;
        IsError = severity == StatusSeverity.Error;
        OnPropertyChanged(nameof(IsSticky));
        if (message.Length > 0)
        {
            History.Insert(0, new StatusEntry(DateTime.Now, severity, message));
            while (History.Count > MaxHistory) History.RemoveAt(History.Count - 1);
            OnPropertyChanged(nameof(HasHistory));
        }

        if (severity is StatusSeverity.Info or StatusSeverity.Success && AutoClearDelay is { } delay && delay > TimeSpan.Zero)
            ScheduleClear(delay, generation);
    }

    private void ScheduleClear(TimeSpan delay, int generation)
    {
        void Fade()
        {
            if (generation == _generation) Clear();
        }

        if (Schedule is { } custom)
        {
            custom(delay, Fade);
            return;
        }
        // the timer runs on a pool thread; the message and its bindings belong to the UI thread
        Task.Delay(delay).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(Fade), TaskScheduler.Default);
    }

    /// <summary>Removes the current message (the history keeps it).</summary>
    [RelayCommand]
    public void Dismiss() => Clear();

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(ShowMessage));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowMessage));

    partial void OnSeverityChanged(StatusSeverity value)
    {
        OnPropertyChanged(nameof(IsWarn));
        OnPropertyChanged(nameof(IsSuccess));
    }

    private void Clear()
    {
        _generation++;
        _connectionId = null;
        Message = "";
        IsError = false;
        Severity = StatusSeverity.Info;
        OnPropertyChanged(nameof(IsSticky));
    }

    /// <summary>Shows the spinner until the returned scope is disposed. Nested scopes are counted.</summary>
    public IDisposable Busy(string text) => Busy(text, null);

    /// <summary>As <see cref="Busy(string)"/>; with <paramref name="cancel"/> the status bar offers a Cancel button for the operation.</summary>
    public IDisposable Busy(string text, Action? cancel)
    {
        _busyCount++;
        BusyText = text;
        IsBusy = true;
        if (cancel is not null)
        {
            _cancels.Add(cancel);
            CanCancel = true;
        }
        return new Scope(this, cancel);
    }

    /// <summary>Cancels the operations that registered a cancel action (the Cancel button of the status bar, or Escape).</summary>
    [RelayCommand]
    public void CancelBusy()
    {
        foreach (var cancel in _cancels.ToList()) cancel();
    }

    private sealed class Scope(StatusModel owner, Action? cancel) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (cancel is not null)
            {
                owner._cancels.Remove(cancel);
                owner.CanCancel = owner._cancels.Count > 0;
            }
            if (--owner._busyCount > 0) return;
            owner.IsBusy = false;
            owner.BusyText = "";
            owner.CanCancel = false;
        }
    }
}
