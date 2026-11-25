using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.ViewModels;

/// <summary>A choice of the "Save script" folder list: the library root or one of its folders.</summary>
/// <param name="Path">Full path of the folder.</param>
/// <param name="Display">Name shown in the list (the root is "Scripts").</param>
/// <param name="Depth">Nesting level, for the indent.</param>
public sealed record SaveFolderItem(string Path, string Display, int Depth)
{
    /// <summary>Left margin in pixels for the nesting.</summary>
    public double Indent => Depth * 16;
}

/// <summary>
/// State of the "Save script" dialog: a name and a folder of the script library (or "choose another location", which falls
/// back to the file picker). Pure logic: the window only binds to it. Confirming takes a free file name in the folder (a name
/// that exists becomes <c>name (2).sql</c>, nothing is overwritten) and leaves its path in <see cref="SavedPath"/>.
/// </summary>
public sealed partial class SaveScriptViewModel : ObservableObject
{
    private readonly ScriptLibrary _library;

    public SaveScriptViewModel(ScriptLibrary library, string suggestedName, string? folder = null)
    {
        _library = library;
        Folders = [];
        LoadFolders();
        _selectedFolder = Folders.FirstOrDefault(f => string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase)) ?? Folders[0];
        _name = suggestedName;
        Validate();
    }

    public ObservableCollection<SaveFolderItem> Folders { get; }

    [ObservableProperty] private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    private SaveFolderItem _selectedFolder;

    /// <summary>Why the name is refused (shown under the box and disables Save), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(CanSave), nameof(Hint))]
    private string? _error;

    public bool HasError => Error is not null;

    public bool CanSave => Error is null;

    /// <summary>"A script with this name exists: it will be saved as name (2).sql", or null.</summary>
    public string? Hint
    {
        get
        {
            if (Error is not null) return null;
            try
            {
                var wanted = ScriptFileName(Name.Trim());
                var free = _library.PreviewNewName(SelectedFolder.Path, Name.Trim());
                return string.Equals(wanted, free, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : $"\"{wanted}\" already exists in this folder: the script will be saved as \"{free}\".";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ScriptLibraryException) { return null; }
        }
    }

    private static string ScriptFileName(string name) =>
        name.EndsWith(ScriptLibrary.Extension, StringComparison.OrdinalIgnoreCase) ? name : name + ScriptLibrary.Extension;

    partial void OnNameChanged(string value) => Validate();

    private void Validate() => Error = ScriptLibrary.ValidateName(Name);

    /// <summary>The user chose "Choose another location…": the caller shows the platform file picker instead.</summary>
    public bool UseFilePicker { get; private set; }

    /// <summary>The file taken in the library by <see cref="TryConfirm"/> (empty: the caller writes the script into it).</summary>
    public string? SavedPath { get; private set; }

    // ---- new folder, inside the dialog ----

    [ObservableProperty] private bool _isCreatingFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolderError))]
    private string _newFolderName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolderError))]
    private string? _folderError;

    public bool HasFolderError => FolderError is not null;

    partial void OnNewFolderNameChanged(string value) => FolderError = null;

    [RelayCommand]
    private void BeginNewFolder()
    {
        NewFolderName = "";
        IsCreatingFolder = true;
    }

    [RelayCommand]
    private void CancelNewFolder() => IsCreatingFolder = false;

    /// <summary>Creates the folder under the selected one and selects it. A refusal shows under the folder box; true when created.</summary>
    public bool TryCreateFolder()
    {
        var name = NewFolderName.Trim();
        if (ScriptLibrary.ValidateName(name) is { } problem)
        {
            FolderError = problem;
            return false;
        }
        try
        {
            var path = _library.CreateFolder(SelectedFolder.Path, name);
            LoadFolders();
            SelectedFolder = Folders.First(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
            IsCreatingFolder = false;
            return true;
        }
        catch (Exception e) when (e is ScriptLibraryException or IOException or UnauthorizedAccessException)
        {
            FolderError = e.Message;
            return false;
        }
    }

    // ---- result ----

    /// <summary>Save: takes the file name in the selected folder. False (with <see cref="Error"/>) when the name or the folder is refused.</summary>
    public bool TryConfirm()
    {
        Validate();
        if (Error is not null) return false;
        try
        {
            SavedPath = _library.ReserveNew(SelectedFolder.Path, Name.Trim());
            return true;
        }
        catch (Exception e) when (e is ScriptLibraryException or IOException or UnauthorizedAccessException)
        {
            Error = e.Message;
            return false;
        }
    }

    [RelayCommand]
    private void CreateFolder() => TryCreateFolder();

    /// <summary>"Choose another location…": ends the dialog without taking a name; the save goes through the platform file picker.</summary>
    public void ChooseAnotherLocation() => UseFilePicker = true;

    private void LoadFolders()
    {
        var keep = SelectedFolder?.Path;
        Folders.Clear();
        Folders.Add(new SaveFolderItem(_library.Root, "Scripts", 0));
        try
        {
            foreach (var f in _library.AllFolders())
                Folders.Add(new SaveFolderItem(f.Path, System.IO.Path.GetFileName(f.Path), f.Depth + 1));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the root alone is still a valid target
        }
        if (keep is not null && Folders.FirstOrDefault(f => f.Path == keep) is { } same) SelectedFolder = same;
    }
}
