using CommunityToolkit.Mvvm.ComponentModel;

namespace DbExplorer.App.ViewModels;

/// <summary>The "Scripts" section of the navigator: the script library (folders and scripts). Always present while the section is shown.</summary>
public sealed partial class ScriptsSectionNodeViewModel : TreeNodeViewModel
{
    public override string Title => "Scripts";

    /// <summary>Full path of the library folder (drops and "new here" commands land in it).</summary>
    public string Path { get; set; } = "";

    /// <summary>Owner whose commands the context menu binds to.</summary>
    public ConnectionTreeViewModel? Tree { get; set; }

    /// <summary>True while the library holds nothing: the section then says how to start.</summary>
    [ObservableProperty] private bool _isLibraryEmpty;
}

/// <summary>A folder of the script library (a real directory). Its children are listed when it is first expanded.</summary>
public sealed class ScriptFolderNodeViewModel(string path) : TreeNodeViewModel
{
    /// <summary>Full path of the directory.</summary>
    public string Path { get; } = path;

    public override string Title => System.IO.Path.GetFileName(Path);

    public ConnectionTreeViewModel? Tree { get; set; }
}

/// <summary>A script of the library (a .sql file). The title is the file name without its extension.</summary>
public sealed class ScriptFileNodeViewModel(string path) : TreeNodeViewModel
{
    /// <summary>Full path of the file.</summary>
    public string Path { get; } = path;

    public override string Title => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>The path, shown as the row tooltip.</summary>
    public string Tooltip => Path;

    public ConnectionTreeViewModel? Tree { get; set; }
}
