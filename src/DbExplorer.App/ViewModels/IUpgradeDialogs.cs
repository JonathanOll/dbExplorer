namespace DbExplorer.App.ViewModels;

/// <summary>Modal windows of the database upgrade feature; implemented by the window layer, faked in tests.</summary>
public interface IUpgradeDialogs
{
    /// <summary>Shows the Settings dialog; true when it was closed with its Close button (a typed dbupdate folder is then confirmed in the Database upgrade page).</summary>
    Task<bool> ShowOptionsAsync(SettingsViewModel viewModel);

    /// <summary>Shows the Upgrade dialog and returns when it is closed.</summary>
    Task ShowUpgradeAsync(UpgradeViewModel viewModel);
}
