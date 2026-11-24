using CommunityToolkit.Mvvm.ComponentModel;

namespace DbExplorer.App.ViewModels;

/// <summary>What the Options dialog hands back: the dbupdate folder, or null to switch the upgrade feature off.</summary>
public sealed record OptionsResult(string? DbUpdatePath);

/// <summary>
/// The Options dialog: today one setting, the dbupdate folder of the n2f repo. Validation and the optional info line are
/// injected delegates (the folder may be a network share, so they run on OK, after Browse and on focus loss, never per keystroke).
/// A blank path is valid and means "feature off".
/// </summary>
public sealed partial class OptionsViewModel : ObservableObject
{
    private readonly Func<string, string?> _validate;
    private readonly Func<string, string?>? _describe;

    /// <param name="currentDbUpdatePath">The stored path (null when the feature is off).</param>
    /// <param name="validate">Returns an error message for an unusable folder, null when fine.</param>
    /// <param name="describe">Returns the info line shown for a valid folder (e.g. "147 version folders found"), or null.</param>
    public OptionsViewModel(string? currentDbUpdatePath, Func<string, string?> validate, Func<string, string?>? describe = null)
    {
        _validate = validate;
        _describe = describe;
        _dbUpdatePath = currentDbUpdatePath ?? "";
    }

    [ObservableProperty]
    private string _dbUpdatePath;

    /// <summary>Why the folder cannot be used (set by validation, cleared by the next edit); null when fine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPathError))]
    private string? _pathError;

    /// <summary>Info line for a valid folder; null otherwise.</summary>
    [ObservableProperty]
    private string? _pathInfo;

    public bool HasPathError => PathError is not null;

    /// <summary>The confirmed options, set by <see cref="TryConfirm"/> only when the path is blank or valid.</summary>
    public OptionsResult? Result { get; private set; }

    partial void OnDbUpdatePathChanged(string value)
    {
        PathError = null;
        PathInfo = null;
    }

    /// <summary>Validates the current text: sets <see cref="PathError"/> or <see cref="PathInfo"/>. Blank text is not validated.</summary>
    public void ValidatePath()
    {
        var path = DbUpdatePath.Trim();
        if (path.Length == 0)
        {
            PathError = null;
            PathInfo = null;
            return;
        }

        PathError = _validate(path);
        PathInfo = PathError is null ? _describe?.Invoke(path) : null;
    }

    /// <summary>Validates and builds <see cref="Result"/>. False (and no result) when the path is not usable.</summary>
    public bool TryConfirm()
    {
        Result = null;
        ValidatePath();
        if (PathError is not null) return false;
        var path = DbUpdatePath.Trim();
        Result = new OptionsResult(path.Length == 0 ? null : path);
        return true;
    }
}
