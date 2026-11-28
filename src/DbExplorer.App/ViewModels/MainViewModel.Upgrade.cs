using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Upgrade;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// The gear-menu entries of the database upgrade feature. The command lives here because it needs the active connection and the
/// session resolver of the shell. Nothing is read from disk until the menu opens or the options are saved.
/// </summary>
public partial class MainViewModel
{
    private IUpgradeDialogs? _upgradeDialogs;

    /// <summary>Installs the dialog service of the Options and Upgrade windows.</summary>
    public void UseUpgrade(IUpgradeDialogs dialogs) => _upgradeDialogs = dialogs;

    /// <summary>
    /// True once the configured dbupdate folder was validated. Starts false and stays false until
    /// <see cref="RefreshUpgradeAvailability"/> runs (menu opened, options saved): no disk access at construction.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenUpgradeCommand))]
    private bool _upgradeAvailable;

    /// <summary>Copies text to the clipboard (the Upgrade dialog's "Copy log"); set by the window layer.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Validates the configured dbupdate folder (a few directory reads, possibly on a share); on demand only.</summary>
    public void RefreshUpgradeAvailability()
    {
        UpgradeAvailable = DbUpdateCatalog.Validate(Tree.Config.DbUpdatePath) == null;
        OpenUpgradeCommand.NotifyCanExecuteChanged();
    }

    private void ApplyDbUpdatePath(string? path)
    {
        if (path == Tree.Config.DbUpdatePath) return;
        Tree.SetDbUpdatePath(path);
        RefreshUpgradeAvailability();
    }

    /// <summary>What the settings pages read and write: the live settings service plus the config flags of the menus.</summary>
    private SettingsContext SettingsContextFor(OptionsViewModel upgrade) => new(Settings.SettingsService.Instance)
    {
        Upgrade = upgrade,
        Claude = ClaudeSettingsAccess,
        ApplyDbUpdatePath = ApplyDbUpdatePath,
        RecordHistory = new(() => Tree.Config.RecordHistory, v => Tree.SetRecordHistory(v)),
        History = _history,
        GetFormat = () => Tree.Config.Format,
        SetFormat = f => Tree.SetFormatOptions(f),
        AutoUpdateCheck = new(() => Update.AutoCheckEnabled, v => Update.AutoCheckEnabled = v),
        ShowFolders = new(() => Tree.ShowFolders, v => { if (v != Tree.ShowFolders) Tree.ToggleFoldersCommand.Execute(null); }),
        ShowGroups = new(() => Tree.ShowGroups, v => { if (v != Tree.ShowGroups) Tree.ToggleGroupsCommand.Execute(null); }),
        ShowScripts = new(() => Tree.ShowScripts, v => { if (v != Tree.ShowScripts) Tree.ToggleScriptsCommand.Execute(null); }),
        CopyText = text => CopyToClipboard?.Invoke(text) ?? Task.CompletedTask,
    };

    /// <summary>Opens the Settings dialog (Tools > Settings, Ctrl+,). <paramref name="page"/> picks the page to start on.</summary>
    [RelayCommand]
    private async Task OpenOptionsAsync(string? page = null)
    {
        try
        {
            if (_upgradeDialogs is not { } dialogs) return;
            var upgrade = new OptionsViewModel(
                Tree.Config.DbUpdatePath,
                DbUpdateCatalog.Validate,
                path => $"{DbUpdateCatalog.Scan(path).Folders.Count} version folders found");
            using var vm = new SettingsViewModel(SettingsContextFor(upgrade), page);
            if (!await dialogs.ShowOptionsAsync(vm) || upgrade.Result is not { } result) return;
            ApplyDbUpdatePath(result.DbUpdatePath);
        }
        catch (Exception ex)
        {
            Status.Error($"Could not open the options: {ErrorText.Describe(ex)}");
        }
    }

    [RelayCommand(CanExecute = nameof(UpgradeAvailable))]
    private async Task OpenUpgradeAsync()
    {
        try
        {
            if (_upgradeDialogs is not { } dialogs) return;
            if (ScriptTarget() is not { } current)
            {
                Status.Info("Select a connection in the tree or open a tab first, then choose Upgrade database.");
                return;
            }

            var path = Tree.Config.DbUpdatePath;
            if (DbUpdateCatalog.Validate(path) is { } error)
            {
                Status.Error(error);
                RefreshUpgradeAvailability();
                return;
            }

            DbUpdateCatalog catalog;
            using (Status.Busy("Reading the dbupdate folder..."))
                catalog = await Task.Run(() => DbUpdateCatalog.Scan(path!));

            var targets = UpgradeRules.TargetsFor(Tree.Config, current);
            var vm = new UpgradeViewModel(catalog, targets, ResolveScriptSessionAsync, CopyToClipboard, guard: Guard);
            await dialogs.ShowUpgradeAsync(vm);
        }
        catch (Exception ex)
        {
            Status.Error($"Could not open the upgrade: {ErrorText.Describe(ex)}");
        }
    }
}
