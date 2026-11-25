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

    /// <summary>A debounced check is under way (the folder may be on a slow share).</summary>
    [ObservableProperty]
    private bool _isChecking;

    /// <summary>No folder is set: the upgrade feature is off.</summary>
    public bool IsFeatureOff => string.IsNullOrWhiteSpace(DbUpdatePath);

    /// <summary>The line under the folder box while it has no error, no check under way and no info: what an empty or untested box means.</summary>
    public string FeatureHint => IsFeatureOff
        ? "Empty: the upgrade feature is off."
        : "Not checked yet. The folder is checked when you leave the box or press Save.";

    /// <summary>The confirmed options, set by <see cref="TryConfirm"/> only when the path is blank or valid.</summary>
    public OptionsResult? Result { get; private set; }

    private CancellationTokenSource? _checkCts;
    private string? _validatedPath;

    partial void OnDbUpdatePathChanged(string value)
    {
        PathError = null;
        PathInfo = null;
        OnPropertyChanged(nameof(IsFeatureOff));
        OnPropertyChanged(nameof(FeatureHint));
    }

    partial void OnPathErrorChanged(string? value) => OnPropertyChanged(nameof(ShowHint));
    partial void OnPathInfoChanged(string? value) => OnPropertyChanged(nameof(ShowHint));
    partial void OnIsCheckingChanged(bool value) => OnPropertyChanged(nameof(ShowHint));

    /// <summary>Nothing else to say about the folder: show <see cref="FeatureHint"/>.</summary>
    public bool ShowHint => PathError is null && PathInfo is null && !IsChecking;

    /// <summary>
    /// Checks the typed folder shortly after the last keystroke, off the UI thread (it may be a share), and shows the result under the
    /// box. A newer call or <see cref="TryConfirm"/> supersedes it. Never throws.
    /// </summary>
    public async Task ValidateSoonAsync(int delayMs = 450)
    {
        _checkCts?.Cancel();
        var cts = _checkCts = new CancellationTokenSource();
        var path = DbUpdatePath.Trim();
        if (path.Length == 0)
        {
            IsChecking = false;
            return;
        }

        if (path == _validatedPath && (PathError is not null || PathInfo is not null))
        {
            IsChecking = false;   // already answered
            return;
        }

        IsChecking = true;
        try
        {
            await Task.Delay(delayMs, cts.Token);
            var (error, info) = await Task.Run(() =>
            {
                var problem = _validate(path);
                return (problem, problem is null ? _describe?.Invoke(path) : null);
            }, cts.Token);
            if (cts.IsCancellationRequested || DbUpdatePath.Trim() != path) return;
            PathError = error;
            PathInfo = info;
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer keystroke or by OK
        }
        catch (Exception e)
        {
            if (!cts.IsCancellationRequested) PathError = $"The folder could not be checked: {e.Message}";
        }
        finally
        {
            if (_checkCts == cts) IsChecking = false;
        }
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

        _validatedPath = path;
        PathError = _validate(path);
        PathInfo = PathError is null ? _describe?.Invoke(path) : null;
    }

    /// <summary>Validates and builds <see cref="Result"/>. False (and no result) when the path is not usable.</summary>
    public bool TryConfirm()
    {
        Result = null;
        _checkCts?.Cancel();
        IsChecking = false;
        ValidatePath();
        if (PathError is not null) return false;
        var path = DbUpdatePath.Trim();
        Result = new OptionsResult(path.Length == 0 ? null : path);
        return true;
    }
}
