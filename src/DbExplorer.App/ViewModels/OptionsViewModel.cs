using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.ViewModels;

/// <summary>What the Options dialog hands back: the dbupdate folder (null switches the upgrade feature off) and the SQL formatting style.</summary>
public sealed record OptionsResult(string? DbUpdatePath, SqlFormatOptions? Format = null);

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
    /// <param name="format">The stored SQL formatting style (null: the defaults).</param>
    public OptionsViewModel(string? currentDbUpdatePath, Func<string, string?> validate, Func<string, string?>? describe = null,
        SqlFormatOptions? format = null)
    {
        _validate = validate;
        _describe = describe;
        _dbUpdatePath = currentDbUpdatePath ?? "";
        _format = (format ?? new SqlFormatOptions()).Normalized();
        _keywordCaseIndex = (int)_format.KeywordCase;
        _indentIndex = _format.UseTabs ? IndentChoices.Count - 1 : Math.Max(0, Array.IndexOf(IndentSizes, _format.IndentSize));
        _commaIndex = (int)_format.CommaPosition;
        _preview = BuildPreview();
    }

    // ---- SQL formatting (live preview) ----

    public static IReadOnlyList<string> KeywordCaseChoices { get; } = ["UPPER CASE", "lower case", "As typed"];
    public static IReadOnlyList<string> IndentChoices { get; } = ["2 spaces", "4 spaces", "8 spaces", "Tab"];
    public static IReadOnlyList<string> CommaChoices { get; } = ["End of the line", "Start of the line"];

    private static readonly int[] IndentSizes = [2, 4, 8];

    private const string PreviewSql =
        "select o.id, c.name, sum(l.qty * l.price) as total from orders o join customers c on c.id = o.customer_id " +
        "left join lines l on l.order_id = o.id where o.status in ('new', 'paid') and o.created >= '2024-01-01' " +
        "group by o.id, c.name order by total desc";

    private SqlFormatOptions _format;

    [ObservableProperty]
    private int _keywordCaseIndex;

    [ObservableProperty]
    private int _indentIndex;

    [ObservableProperty]
    private int _commaIndex;

    /// <summary>A sample statement formatted with the options chosen above.</summary>
    [ObservableProperty]
    private string _preview;

    /// <summary>The formatting style chosen in the dialog.</summary>
    public SqlFormatOptions FormatOptions => _format;

    partial void OnKeywordCaseIndexChanged(int value) => UpdateFormat();
    partial void OnIndentIndexChanged(int value) => UpdateFormat();
    partial void OnCommaIndexChanged(int value) => UpdateFormat();

    private void UpdateFormat()
    {
        var tabs = IndentIndex == IndentChoices.Count - 1;
        _format = new SqlFormatOptions
        {
            KeywordCase = Enum.IsDefined((KeywordCase)KeywordCaseIndex) ? (KeywordCase)KeywordCaseIndex : KeywordCase.Upper,
            UseTabs = tabs,
            IndentSize = tabs || IndentIndex < 0 || IndentIndex >= IndentSizes.Length ? _format.IndentSize : IndentSizes[IndentIndex],
            CommaPosition = Enum.IsDefined((CommaPosition)CommaIndex) ? (CommaPosition)CommaIndex : CommaPosition.Trailing,
            MaxLineWidth = _format.MaxLineWidth,
        };
        Preview = BuildPreview();
    }

    private string BuildPreview() => SqlFormatter.Format(PreviewSql, DbKind.Postgres, _format).Replace("	", "    ");

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
        Result = new OptionsResult(path.Length == 0 ? null : path, _format);
        return true;
    }
}
