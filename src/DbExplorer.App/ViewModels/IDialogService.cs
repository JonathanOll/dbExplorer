namespace DbExplorer.App.ViewModels;

/// <summary>Modal interactions the view models need; implemented by the window layer, faked in tests.</summary>
public interface IDialogService
{
    /// <summary>Shows the connection dialog; true when the user confirmed (the view model then holds the result).</summary>
    Task<bool> ShowConnectionDialogAsync(ConnectionDialogViewModel viewModel);

    /// <summary>Asks for a single line of text; null when cancelled.</summary>
    Task<string?> PromptTextAsync(string title, string label, string initial);

    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>Shows the group editor; true when the user confirmed (the view model then holds <see cref="GroupEditorViewModel.Result"/>).</summary>
    Task<bool> ShowGroupEditorAsync(GroupEditorViewModel viewModel);

    /// <summary>Shows the schema-difference report of a group (informational: one Close button).</summary>
    Task ShowSchemaDiffAsync(SchemaDiffViewModel viewModel);

    /// <summary>Asks to pick one of <paramref name="options"/>; the index of the choice, or null when cancelled.</summary>
    Task<int?> ChooseAsync(string title, string label, IReadOnlyList<string> options);
}
