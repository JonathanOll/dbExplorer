using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Upgrade;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// The gear-menu entries of the N2F upgrade feature. The command lives here because it needs the active connection and the
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

    [RelayCommand]
    private async Task OpenOptionsAsync()
    {
        try
        {
            if (_upgradeDialogs is not { } dialogs) return;
            var vm = new OptionsViewModel(
                Tree.Config.DbUpdatePath,
                DbUpdateCatalog.Validate,
                path => $"{DbUpdateCatalog.Scan(path).Folders.Count} version folders found",
                Tree.Config.RecordHistory,
                _history is { } history ? history.Clear : null);
            if (!await dialogs.ShowOptionsAsync(vm) || vm.Result is not { } result) return;
            Tree.SetDbUpdatePath(result.DbUpdatePath);
            if (result.RecordHistory != Tree.Config.RecordHistory) Tree.SetRecordHistory(result.RecordHistory);
            RefreshUpgradeAvailability();
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
            var vm = new UpgradeViewModel(catalog, targets, ResolveScriptSessionAsync, CopyToClipboard);
            await dialogs.ShowUpgradeAsync(vm);
        }
        catch (Exception ex)
        {
            Status.Error($"Could not open the upgrade: {ErrorText.Describe(ex)}");
        }
    }
}
