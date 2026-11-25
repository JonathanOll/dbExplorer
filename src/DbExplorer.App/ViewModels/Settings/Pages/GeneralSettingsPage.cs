namespace DbExplorer.App.ViewModels;

/// <summary>What the navigator lists (the same flags as the View menu).</summary>
public sealed class GeneralSettingsPage : SettingsPageViewModel
{
    public GeneralSettingsPage(SettingsContext ctx)
        : base("general", "General", "IconSettings", "Behaviour of the navigator.")
    {
        var items = new List<SettingItemViewModel>();
        if (ctx.ShowFolders is { } folders) items.Add(Make.Toggle("Show folders", "Group connections under their folder. Off lists every connection flat.", folders, true));
        if (ctx.ShowGroups is { } groups) items.Add(Make.Toggle("Show the Groups section", null, groups, true));
        if (ctx.ShowScripts is { } scripts) items.Add(Make.Toggle("Show the Scripts section", "The script library.", scripts, true));
        AddGroup("Navigator", [.. items]);
    }
}
