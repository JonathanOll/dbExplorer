namespace DbExplorer.App.ViewModels;

/// <summary>How a confirmation looks: the icon colour and, for <see cref="Danger"/>, a red confirm button with Cancel as the default.</summary>
public enum DialogTone { Info, Warning, Danger }

/// <summary>A confirmation: a verb on the confirm button, what will happen in <see cref="Detail"/>.</summary>
public sealed record ConfirmRequest(string Title, string Message, string ConfirmText = "OK", string? Detail = null, DialogTone Tone = DialogTone.Warning);

/// <summary>A one-line prompt; <see cref="Validate"/> returns the message to show under the box (and blocks the button), or null when fine.</summary>
public sealed record PromptRequest(string Title, string Label, string Initial = "", string OkText = "OK", Func<string, string?>? Validate = null, string? Placeholder = null);

/// <summary>Modal interactions the view models need; implemented by the window layer, faked in tests.</summary>
public interface IDialogService
{
    /// <summary>Shows the connection dialog; true when the user confirmed (the view model then holds the result).</summary>
    Task<bool> ShowConnectionDialogAsync(ConnectionDialogViewModel viewModel);

    /// <summary>Asks for a single line of text; null when cancelled.</summary>
    Task<string?> PromptTextAsync(string title, string label, string initial);

    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>A confirmation with a verb button, a detail line and a tone. Implementations that only know the plain form keep the default.</summary>
    Task<bool> ConfirmAsync(ConfirmRequest request) => ConfirmAsync(request.Title, request.Message);

    /// <summary>A prompt with validation and a verb button. Implementations that only know the plain form keep the default.</summary>
    Task<string?> PromptTextAsync(PromptRequest request) => PromptTextAsync(request.Title, request.Label, request.Initial);

    /// <summary>Shows the group editor; true when the user confirmed (the view model then holds <see cref="GroupEditorViewModel.Result"/>).</summary>
    Task<bool> ShowGroupEditorAsync(GroupEditorViewModel viewModel);

    /// <summary>Shows the schema-difference report of a group (informational: one Close button).</summary>
    Task ShowSchemaDiffAsync(SchemaDiffViewModel viewModel);

    /// <summary>Asks to pick one of <paramref name="options"/>; the index of the choice, or null when cancelled.</summary>
    Task<int?> ChooseAsync(string title, string label, IReadOnlyList<string> options);
}
