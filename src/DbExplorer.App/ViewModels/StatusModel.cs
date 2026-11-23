using CommunityToolkit.Mvvm.ComponentModel;

namespace DbExplorer.App.ViewModels;

/// <summary>Status bar state shared by all view models: last message, error flag and a busy indicator.</summary>
public partial class StatusModel : ObservableObject
{
    private int _busyCount;

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";

    public void Info(string message)
    {
        Message = message;
        IsError = false;
    }

    public void Error(string message)
    {
        Message = message;
        IsError = true;
    }

    /// <summary>Shows the spinner until the returned scope is disposed. Nested scopes are counted.</summary>
    public IDisposable Busy(string text)
    {
        _busyCount++;
        BusyText = text;
        IsBusy = true;
        return new Scope(this);
    }

    private sealed class Scope(StatusModel owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (--owner._busyCount > 0) return;
            owner.IsBusy = false;
            owner.BusyText = "";
        }
    }
}
