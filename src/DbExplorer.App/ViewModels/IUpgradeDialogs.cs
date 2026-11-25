namespace DbExplorer.App.ViewModels;

/// <summary>Modal windows of the N2F upgrade feature; implemented by the window layer, faked in tests.</summary>
public interface IUpgradeDialogs
{
    /// <summary>Shows the Settings dialog; true when it was closed with its Close button (a dbupdate folder is then confirmed in <see cref="OptionsViewModel.Result"/>).</summary>
    Task<bool> ShowOptionsAsync(SettingsViewModel viewModel);

    /// <summary>Shows the Upgrade dialog and returns when it is closed.</summary>
    Task ShowUpgradeAsync(UpgradeViewModel viewModel);
}
