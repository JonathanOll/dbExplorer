using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Tests.Upgrade;

/// <summary>Records the view models handed to the upgrade windows; the Options "dialog" runs a script and answers OK or cancel.</summary>
internal sealed class FakeUpgradeDialogs : IUpgradeDialogs
{
    public List<OptionsViewModel> Options { get; } = [];
    public List<UpgradeViewModel> Upgrades { get; } = [];

    /// <summary>What the user does in the Options window: edits the vm, then presses OK (true) or Cancel (false).</summary>
    public Func<OptionsViewModel, bool> OptionsScript { get; set; } = _ => false;

    public Task<bool> ShowOptionsAsync(SettingsViewModel settings)
    {
        var viewModel = settings.Upgrade!;
        Options.Add(viewModel);
        return Task.FromResult(OptionsScript(viewModel));
    }

    public Task ShowUpgradeAsync(UpgradeViewModel viewModel)
    {
        Upgrades.Add(viewModel);
        return Task.CompletedTask;
    }
}
