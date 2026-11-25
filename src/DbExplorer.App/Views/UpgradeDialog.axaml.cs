using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Views;

/// <summary>
/// The Upgrade dialog: sprint, preview, confirmation and the per-database status of the run, all in one window (the phase lives in
/// <see cref="UpgradeViewModel"/>). Closing it while a run is in progress is refused and asks, inside the window, whether to stop.
/// </summary>
public partial class UpgradeDialog : Window
{
    /// <summary>The tooltip of the Close button: when it is off, why.</summary>
    public static readonly IValueConverter CloseTip = new FuncValueConverter<bool, string>(canClose =>
        canClose ? "Close (Esc)" : "A run is in progress. Stop it first, or wait for it to end.");

    private UpgradeViewModel? _watched;

    public UpgradeDialog()
    {
        InitializeComponent();
        Closing += OnClosing;
        Opened += (_, _) => SprintBox.Focus();
        DataContextChanged += (_, _) => Watch();
        FolderBox.SelectionChanged += OnFolderChosen;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not UpgradeViewModel { CanClose: false } vm) return;
        e.Cancel = true;   // a run in progress is not abandoned by closing the window: the dialog asks "Stop the upgrade?" first
        if (vm.RequestStopCommand.CanExecute(null)) vm.RequestStopCommand.Execute(null);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // ---- focus follows the phase ----

    private void Watch()
    {
        if (_watched is not null) _watched.PropertyChanged -= OnViewModelChanged;
        _watched = DataContext as UpgradeViewModel;
        if (_watched is not null) _watched.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UpgradeViewModel.Phase) || _watched is not { } vm) return;
        // After the layout has swapped the views: land on what the user does next.
        Dispatcher.UIThread.Post(() =>
        {
            switch (vm.Phase)
            {
                case UpgradePhase.Confirm when vm.NeedsTypedConfirmation:
                    ConfirmBox.Focus();
                    break;
                case UpgradePhase.Confirm:
                    RunButton.Focus();
                    break;
                case UpgradePhase.Setup:
                    SprintBox.Focus();
                    break;
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>Picking a folder in the list loads it at once: there is nothing else to type.</summary>
    private void OnFolderChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || DataContext is not UpgradeViewModel vm) return;
        if (!(FolderBox.IsKeyboardFocusWithin || FolderBox.IsPointerOver || FolderBox.IsDropDownOpen)) return;   // the user chose it
        if (vm.LoadCommand.CanExecute(null)) vm.LoadCommand.Execute(null);
    }

    // ---- the preview list ----

    /// <summary>A click anywhere on a script row ticks or unticks it (the check box itself does its own toggling).</summary>
    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: UpgradeScriptRow row }) return;
        if ((e.Source as Visual)?.FindAncestorOfType<CheckBox>(includeSelf: true) is not null) return;
        if (row.CanSelect) row.IsSelected = !row.IsSelected;
    }

    // ---- the log ----

    private async void OnSaveLogClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not UpgradeViewModel vm) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the upgrade log",
                SuggestedFileName = "upgrade-log.txt",
                DefaultExtension = "txt",
                FileTypeChoices = [new FilePickerFileType("Text file") { Patterns = ["*.txt"] }],
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(vm.Log);
        }
        catch (Exception ex)
        {
            vm.Message = $"The log could not be saved: {ex.Message}";
        }
    }
}
