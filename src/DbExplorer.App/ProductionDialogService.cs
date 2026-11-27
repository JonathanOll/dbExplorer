using Avalonia.Controls;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App;

/// <summary>Real <see cref="IProductionDialogs"/>: a modal window over whichever window is in front.</summary>
public sealed class ProductionDialogService(Window main) : IProductionDialogs
{
    public async Task<ProductionWriteAnswer> ConfirmProductionWriteAsync(ProductionWriteRequest request)
    {
        // The upgrade runs from its own modal window: the confirmation must sit on top of that one.
        var owner = main;
        while (owner.OwnedWindows.LastOrDefault(w => w.IsVisible) is { } child) owner = child;
        return await ProductionDialog.Create(request).ShowDialog<ProductionWriteAnswer?>(owner)
            ?? new ProductionWriteAnswer(false);   // closed with the X
    }
}
