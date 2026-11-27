using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Core.Metadata;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.ViewModels;

/// <summary>What the search of one connection needs to reach the other connections (their metadata cache, never a connect).</summary>
/// <param name="Configs">Every connection, a snapshot; the searched one is left out.</param>
/// <param name="Connect">Connects when a result of another connection is chosen.</param>
public sealed record QuickOpenElsewhere(
    MetadataCache Cache, IReadOnlyList<ConnectionConfig> Configs, IReadOnlyList<ConnectionGroup> Groups,
    Func<ConnectionConfig, Task<Session?>> Connect);

// Scope extension: a word of the query names another connection, so the search of one connection looks at all of them.
public sealed partial class QuickOpenViewModel
{
    /// <summary>Says that rows of other connections are searched too, and why; null while only this one is.</summary>
    [ObservableProperty] private string? _extensionText;

    /// <summary>Done when the cached metadata of the other connections, once needed, has been read.</summary>
    public Task Extending { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The first word naming another connection that this one cannot satisfy: nothing here is called like it, or the query
    /// as a whole finds nothing here (<paramref name="nothingHere"/>).
    /// </summary>
    private static string? WordForElsewhere(QuickOpenSource source, QuickOpenExtension extension, QuickOpenQuery query, bool nothingHere)
    {
        foreach (var word in query.Words)
        {
            var named = extension.Places.Any(p => QuickOpenQuery.Names(p, word));
            if (!named)
            {
                // A typo in the name of another connection, which is not also close to this one.
                if (QuickOpenQuery.NearlyNames(extension.Here, word) || !extension.Places.Any(p => QuickOpenQuery.NearlyNames(p, word)))
                    continue;
            }

            if (nothingHere || !QuickOpenQuery.Names(extension.Here, word) && !source.Mentions(word)) return word;
        }

        return null;
    }

    private async Task ReadElsewhereAsync(QuickOpenSource source, QuickOpenExtension extension)
    {
        try
        {
            var found = await Task.Run(extension.Read);
            await Task.Yield(); // never ranks again from inside the ranking that asked for it
            source.Extend(found);
        }
        catch (Exception e)
        {
            _status?.Error($"Cannot search the other connections: {ErrorText.Describe(e)}");
        }
    }

    private static string ExtensionMessage(QuickOpenSource source, QuickOpenExtension extension, string word)
    {
        if (source.Elsewhere is not { } rows) return $"‘{word}’ names another connection: reading the cached objects of the others…";
        var here = extension.Here.Connection;
        // The word may well be known here: then it is the whole query that finds nothing.
        var reason = QuickOpenQuery.Names(extension.Here, word) || source.Mentions(word)
            ? $"Nothing in {here} matches, and ‘{word}’ also names another connection"
            : $"‘{word}’ is not in {here}";
        var text = $"{reason}: also searching other connections (cached objects).";
        var missing = rows.Uncached.Where(s => QuickOpenQuery.Names(s, word)).Select(s => s.Connection).ToList();
        return missing.Count == 0 ? text : $"{text} No cache yet for {string.Join(", ", missing)}: open it once.";
    }
}
