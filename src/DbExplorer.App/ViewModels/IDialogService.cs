namespace DbExplorer.App.ViewModels;

/// <summary>Modal interactions the view models need; implemented by the window layer, faked in tests.</summary>
public interface IDialogService
{
    /// <summary>Shows the connection dialog; true when the user confirmed (the view model then holds the result).</summary>
    Task<bool> ShowConnectionDialogAsync(ConnectionDialogViewModel viewModel);

    /// <summary>Asks for a single line of text; null when cancelled.</summary>
    Task<string?> PromptTextAsync(string title, string label, string initial);

    Task<bool> ConfirmAsync(string title, string message);
}
