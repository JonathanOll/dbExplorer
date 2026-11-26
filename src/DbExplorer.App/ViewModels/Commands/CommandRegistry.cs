namespace DbExplorer.App.ViewModels;

/// <summary>
/// The single list of the application's commands. A feature adds its entries with <see cref="Add"/>: the provider is called
/// each time the palette opens, so it can answer for the current tab and connection. Providers must be cheap (no disk, no
/// database): the palette opens on a key press.
/// </summary>
public sealed class CommandRegistry
{
    private readonly List<Func<IEnumerable<AppCommand>>> _providers = [];

    public void Add(Func<IEnumerable<AppCommand>> provider) => _providers.Add(provider);

    /// <summary>Every command as of now, in registration order. A repeated id keeps its first entry.</summary>
    public IReadOnlyList<AppCommand> Snapshot()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return _providers.SelectMany(p => p()).Where(c => seen.Add(c.Id)).ToList();
    }
}
