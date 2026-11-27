namespace DbExplorer.App.ViewModels;

/// <summary>
/// One entry of the command palette: a command, or one choice of a command that asks for a parameter. It is a snapshot taken
/// when the palette opens, so whether it is available is a value, not a callback.
/// </summary>
/// <param name="Id">Stable identifier (persisted in the "recently used" list): never rename one lightly.</param>
/// <param name="Category">Short group name shown on the row, and searched.</param>
/// <param name="Title">What the row says; a trailing "…" by convention when a choice or a dialog follows.</param>
/// <param name="Icon">Resource key of a vector icon (Styles/Icons.axaml).</param>
public sealed record AppCommand(string Id, string Category, string Title, string Icon = "IconKeyboard")
{
    /// <summary>The keyboard shortcut as shown on the row, or null.</summary>
    public string? Shortcut { get; init; }

    /// <summary>Other words that find the command (space separated).</summary>
    public string? Keywords { get; init; }

    /// <summary>Faint text after the title, e.g. "connected" or "current".</summary>
    public string? Detail { get; init; }

    /// <summary>Environment of the connection the row stands for (shown as a badge); None for any other command.</summary>
    public Core.Model.DbEnvironment Environment { get; init; }

    /// <summary>Why the command cannot run now (the row is greyed and says so), or null when it can.</summary>
    public string? DisabledReason { get; init; }

    /// <summary>What the command does. Not used when <see cref="Choices"/> is set.</summary>
    public Func<Task>? Run { get; init; }

    /// <summary>A second step: choosing the command lists these inside the palette (built only then).</summary>
    public Func<IReadOnlyList<AppCommand>>? Choices { get; init; }

    public bool IsEnabled => DisabledReason is null;

    public bool HasChoices => Choices is not null;
}
