using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>The side panel of a question to Claude. Only focus, scrolling, keys and the clipboard live here.</summary>
public partial class ClaudePanelView : UserControl
{
    private ClaudePanelViewModel? _watched;

    public ClaudePanelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Watch(DataContext as ClaudePanelViewModel);
        AttachedToVisualTree += (_, _) =>
        {
            Watch(DataContext as ClaudePanelViewModel);
            if (_watched?.IsInput == true) Dispatcher.UIThread.Post(() => RequestBox.Focus(), DispatcherPriority.Background);
        };
        DetachedFromVisualTree += (_, _) => Watch(null);
        RequestBox.AddHandler(KeyDownEvent, OnRequestKeyDown, RoutingStrategies.Tunnel);
        KeyDown += OnPanelKeyDown;
    }

    private void Watch(ClaudePanelViewModel? next)
    {
        if (ReferenceEquals(_watched, next)) return;
        if (_watched is not null) _watched.PropertyChanged -= OnViewModelChanged;
        _watched = next;
        if (_watched is not null) _watched.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A finished answer is read from the top, where the proposed change is.
        if (e.PropertyName == nameof(ClaudePanelViewModel.Phase) && _watched is { IsDone: true })
            Dispatcher.UIThread.Post(() => Body.ScrollToHome(), DispatcherPriority.Background);
    }

    private void OnRequestKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.Control || _watched is not { } vm) return;
        e.Handled = true;
        if (vm.ContinueCommand.CanExecute(null)) vm.ContinueCommand.Execute(null);
    }

    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled || _watched is not { } vm) return;
        e.Handled = true;
        if (vm.IsRunning) vm.Stop();
        else vm.Close();
    }

    private async void OnCopyAnswerClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_watched is { } vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(vm.Answer);
        }
        catch (Exception)
        {
            // the clipboard can be busy: nothing to report for a copy
        }
    }
}
