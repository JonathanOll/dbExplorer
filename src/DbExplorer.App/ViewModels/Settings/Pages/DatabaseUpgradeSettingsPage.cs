namespace DbExplorer.App.ViewModels;

/// <summary>
/// The dbupdate folder of the database upgrade feature. It keeps the validation rules of <see cref="OptionsViewModel"/>: the folder
/// (maybe a share) is checked when the box loses focus, after Browse and on Enter, and only a valid folder (or an empty box,
/// which switches the feature off) is stored.
/// </summary>
public sealed class DatabaseUpgradeSettingsPage(SettingsContext ctx)
    : SettingsPageViewModel("upgrade", "Database upgrade", "IconDatabase", "The dbupdate scripts used by Tools > Upgrade database.")
{
    public OptionsViewModel Options { get; } = ctx.Upgrade!;

    public override bool IsGeneric => false;
    public override bool CanReset => true;

    public override bool MatchesQuery(string query) =>
        string.IsNullOrWhiteSpace(query)
        || "dbupdate folder database upgrade database scripts".Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
        || Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Validates the typed folder and, when it is usable, stores it now. False (and an error under the box) otherwise.</summary>
    public bool Commit()
    {
        if (!Options.TryConfirm()) return false;
        ctx.ApplyDbUpdatePath?.Invoke(Options.Result!.DbUpdatePath);
        return true;
    }

    /// <summary>Default: no folder, the upgrade feature is off.</summary>
    public override void ResetToDefaults()
    {
        Options.DbUpdatePath = "";
        Commit();
    }
}
