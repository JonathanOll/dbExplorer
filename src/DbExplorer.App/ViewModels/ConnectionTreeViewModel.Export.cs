using DbExplorer.Core.Export;

namespace DbExplorer.App.ViewModels;

public sealed partial class ConnectionTreeViewModel
{
    /// <summary>Keeps the choices of the copy and export dialogs; same contract as <see cref="SetAutoUpdateCheck"/>.</summary>
    public bool SetExportPreferences(ExportPreferences preferences)
    {
        Config.Export = preferences;
        return SaveSetting();
    }
}
