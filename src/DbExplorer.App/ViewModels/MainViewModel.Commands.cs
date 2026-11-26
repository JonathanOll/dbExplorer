using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbExplorer.App.ViewModels;

/// <summary>The command palette (Ctrl+Shift+K) and the registry of the application's commands behind it.</summary>
public partial class MainViewModel
{
    private CommandRegistry? _registry;

    /// <summary>
    /// Every command of the app. Built on first use (nothing at start-up); a feature adds its own entries with
    /// <see cref="CommandRegistry.Add"/>.
    /// </summary>
    public CommandRegistry Registry
    {
        get
        {
            if (_registry is not null) return _registry;
            _registry = new CommandRegistry();
            RegisterBuiltInCommands(_registry);
            return _registry;
        }
    }

    /// <summary>The palette currently shown, or null. A fresh view model per opening.</summary>
    [ObservableProperty] private CommandPaletteViewModel? _commandPalette;

    /// <summary>Closes the window; set by the window layer (File > Exit).</summary>
    public Action? ExitRequested { get; set; }

    /// <summary>Puts the keyboard in the navigator's filter box; set by the window layer.</summary>
    public Action? FocusNavigatorFilter { get; set; }

    /// <summary>The result or data grid of the active tab, or null when it shows none; set by the window layer.</summary>
    public Func<IGridCommands?>? FindActiveGrid { get; set; }

    /// <summary>Ctrl+Shift+K: shows the palette, or closes it when it is already showing.</summary>
    [RelayCommand]
    public void ToggleCommandPalette()
    {
        if (CommandPalette is { } open) open.Close();
        else ShowCommandPalette();
    }

    [RelayCommand]
    public void ShowCommandPalette()
    {
        if (CommandPalette is not null) return;
        try
        {
            // One overlay at a time: the palette replaces whatever floats over the window.
            QuickOpen?.Cancel();
            SearchOverlay?.Close();
            IsShortcutsVisible = false;

            var palette = new CommandPaletteViewModel(Registry.Snapshot(), Tree.Config.RecentCommands, ContextCategories(),
                id => Tree.AddRecentCommand(id), Status);
            palette.CloseRequested += () =>
            {
                if (ReferenceEquals(CommandPalette, palette)) CommandPalette = null;
            };
            CommandPalette = palette;
        }
        catch (Exception e)
        {
            Status.Error($"Cannot open the command palette: {ErrorText.Describe(e)}");
        }
    }

    private string[] ContextCategories() => ActiveTab switch
    {
        ScriptTabViewModel { SelectedResultTab: PlanResultTab } => ["Plan", "Script"],
        ScriptTabViewModel => ["Script", "Grid", "Plan"],
        TableTabViewModel => ["Table", "Grid"],
        DiagramTabViewModel => ["Diagram"],
        _ => [],
    };
}
