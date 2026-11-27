namespace DbExplorer.App.ViewModels;

/// <summary>The user's answer about a write on production. <see cref="DontAskAgain"/> counts only when it was offered.</summary>
public sealed record ProductionWriteAnswer(bool Confirmed, bool DontAskAgain = false);

/// <summary>The confirmation of a write on production; implemented by the window layer, faked in tests.</summary>
public interface IProductionDialogs
{
    Task<ProductionWriteAnswer> ConfirmProductionWriteAsync(ProductionWriteRequest request);
}
