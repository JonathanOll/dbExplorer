namespace DbExplorer.App.ViewModels;

/// <summary>The automatic update check (the same flag as the Help menu).</summary>
public sealed class UpdatesSettingsPage : SettingsPageViewModel
{
    public UpdatesSettingsPage(SettingsContext ctx)
        : base("updates", "Updates", "IconRefresh", "How dbExplorer looks for new versions.")
    {
        AddGroup("Updates",
            Make.Toggle("Check for updates automatically", "A few seconds after start the app looks for a newer release and offers to install it.", ctx.AutoUpdateCheck!, true));
    }
}
