namespace DbExplorer.App.ViewModels;

/// <summary>Modal windows of the N2F upgrade feature; implemented by the window layer, faked in tests.</summary>
public interface IUpgradeDialogs
{
    /// <summary>Shows the Options dialog; true when the user pressed OK (the confirmed values are in <see cref="OptionsViewModel.Result"/>).</summary>
    Task<bool> ShowOptionsAsync(OptionsViewModel viewModel);

    /// <summary>Shows the Upgrade dialog and returns when it is closed.</summary>
    Task ShowUpgradeAsync(UpgradeViewModel viewModel);
}
