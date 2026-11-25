using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Core.Metadata;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.ViewModels;

/// <summary>One row of the quick-open list. <see cref="Group"/> (connection name) is only a ranking tie-break.</summary>
public record QuickOpenItem(string Label, string? Detail, Func<Task> Open)
{
    public string? Group { get; init; }

    /// <summary>Resource key of the vector icon drawn before the label (Styles/Icons.axaml).</summary>
    public string Icon { get; init; } = "IconTable";

    /// <summary>What the row shows as its main text when it differs from <see cref="Label"/> (the label is what is searched).</summary>
    public string? Title { get; init; }

    /// <summary>Faint chip at the right of the row (the connection of a table found in every connection).</summary>
    public string? Context { get; init; }

    public string DisplayLabel => Title ?? Label;

    public bool HasContext => !string.IsNullOrEmpty(Context);
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// <summary>
/// State of the quick-open overlay: a query, the top <see cref="MaxResults"/> fuzzy matches (re-ranked
/// synchronously on every keystroke) and a selection. Opening an item never throws: failures go to the status bar.
/// </summary>
public sealed partial class QuickOpenViewModel : ObservableObject
{
    public const int MaxResults = 50;

    private QuickOpenItem[] _items;
    private QuickOpenViewModel? _otherScope;
    private readonly StatusModel? _status;
    private bool _suppressRank;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private IReadOnlyList<QuickOpenItem> _results = [];
    [ObservableProperty] private int _selectedIndex = -1;

    /// <summary>How many items match the query (the list shows the best <see cref="MaxResults"/>).</summary>
    [ObservableProperty] private int _matchCount;

    /// <summary>Footer count: "12 results", "1 result" or "50 of 1,284".</summary>
    public string CountText => MatchCount > Results.Count ? $"{Results.Count:N0} of {MatchCount:N0}" : MatchCount == 1 ? "1 result" : $"{MatchCount:N0} results";

    /// <summary>Shown instead of the list when it is empty.</summary>
    public string EmptyText => _items.Length == 0 ? EmptyListText : $"No match for ‘{Query}’";

    /// <summary>What an empty list says when there is nothing to search at all.</summary>
    public string EmptyListText { get; set; } = "Nothing to search here yet.";

    /// <summary>Builds the other scope of this search (this connection / all connections); null when there is only one.</summary>
    public Func<QuickOpenViewModel?>? OtherScope { get; set; }

    /// <summary>Footer hint of the Tab key, e.g. "Search all connections"; null without another scope.</summary>
    public string? ScopeHint { get; set; }

    public bool HasScopes => OtherScope is not null || _otherScope is not null;

    /// <summary>
    /// Tab: swaps the searched items with those of the other scope (built on first use) without closing; the typed query is
    /// kept and re-ranked. Doing it again swaps back.
    /// </summary>
    public void SwitchScope()
    {
        var other = _otherScope ??= OtherScope?.Invoke();
        if (other is null) return;
        (_items, other._items) = (other._items, _items);
        (Title, other.Title) = (other.Title, Title);
        (Notice, other.Notice) = (other.Notice, Notice);
        (EmptyListText, other.EmptyListText) = (other.EmptyListText, EmptyListText);
        (ScopeHint, other.ScopeHint) = (other.ScopeHint, ScopeHint);
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(ScopeHint));
        Rank();
    }

    public bool IsEmpty => Results.Count == 0;

    /// <param name="initialQuery">Text the box starts with (the remembered query of the last opening); results are ranked for it.</param>
    public QuickOpenViewModel(string title, IReadOnlyList<QuickOpenItem> items, StatusModel? status = null, string? notice = null,
        string initialQuery = "")
    {
        Title = title;
        _query = initialQuery;
        LastQuery = initialQuery;
        _items = items as QuickOpenItem[] ?? items.ToArray();
        _status = status;
        Notice = notice;
        Rank();
    }

    /// <summary>Short caption shown above the text box (what is being searched).</summary>
    public string Title { get; private set; }

    /// <summary>
    /// The last text typed, kept when the overlay is cancelled (Esc clears the box but not this), so the next opening can start
    /// from it.
    /// </summary>
    public string LastQuery { get; private set; }

    /// <summary>Optional hint under the list, e.g. connections without a cache.</summary>
    public string? Notice { get; private set; }

    /// <summary>Raised when the overlay must go away (confirmed or cancelled).</summary>
    public event Action? CloseRequested;

    // ---- factories ----

    /// <summary>Tables and views of the LIVE schema of an open session, labelled <c>schema.table</c>.</summary>
    public static QuickOpenViewModel ForTables(Session session, Func<Session, TableRef, Task> openTable, StatusModel? status = null,
        string initialQuery = "")
    {
        var items = session.Schema
            .Select(t => new QuickOpenItem(t.Ref.ToString(), t.IsView ? "view" : null, () => openTable(session, t.Ref))
            {
                Group = session.Config.Name,
                Icon = t.IsView ? "IconView" : "IconTable",
            })
            .ToList();
        return new QuickOpenViewModel($"Go to table in {session.Config.Name}", items, status, initialQuery: initialQuery);
    }

    /// <summary>
    /// Tables of every connection that has a metadata cache, labelled <c>connection · schema.table</c>. Reads the
    /// cache only; confirming a result calls <paramref name="connect"/> (which may return null after reporting its
    /// own failure) and then opens the table, unless it vanished from the live schema.
    /// </summary>
    public static QuickOpenViewModel ForAllConnections(
        MetadataCache cache, IEnumerable<ConnectionConfig> configs,
        Func<ConnectionConfig, Task<Session?>> connect, Func<Session, TableRef, Task> openTable, StatusModel? status = null)
    {
        var items = new List<QuickOpenItem>();
        var withoutCache = 0;
        foreach (var config in configs)
        {
            var tables = cache.Load(config.Id);
            if (tables.Count == 0)
            {
                withoutCache++;
                continue;
            }

            foreach (var table in tables)
            {
                var (cfg, tableRef) = (config, table.Ref);
                items.Add(new QuickOpenItem($"{cfg.Name} · {tableRef}", table.IsView ? "view" : null,
                    () => ConnectThenOpen(cfg, tableRef, connect, openTable, status))
                {
                    Group = cfg.Name,
                    Title = tableRef.ToString(),
                    Context = cfg.Name,
                    Icon = table.IsView ? "IconView" : "IconTable",
                });
            }
        }

        var notice = withoutCache switch
        {
            0 => null,
            1 => "1 connection has no cache yet — open it once",
            _ => $"{withoutCache} connections have no cache yet — open them once",
        };
        return new QuickOpenViewModel("Go to table in any connection", items, status, notice);
    }

    /// <summary>Every connection, to connect to it (Ctrl+T with nothing selected): connected ones are marked in the detail column.</summary>
    public static QuickOpenViewModel ForConnections(
        IEnumerable<ConnectionConfig> configs, Func<ConnectionConfig, bool> isConnected,
        Func<ConnectionConfig, Task<Session?>> connect, Func<Session, Task> afterConnect, StatusModel? status = null)
    {
        var items = configs
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new QuickOpenItem(c.Name, isConnected(c) ? "connected" : c.Kind.ToString(), async () =>
            {
                if (await connect(c) is { } session) await afterConnect(session);
            })
            {
                Icon = "IconDatabase",
                Group = c.Folder,
            })
            .ToList();
        return new QuickOpenViewModel("Go to connection", items, status) { EmptyListText = "No connections yet. Add one from the navigator." };
    }

    /// <summary>Column names of a table view; confirming a result calls <paramref name="jumpTo"/> with its index.</summary>
    public static QuickOpenViewModel ForColumns(IReadOnlyList<string> columnNames, Action<int> jumpTo, StatusModel? status = null)
    {
        var items = new List<QuickOpenItem>(columnNames.Count);
        for (var i = 0; i < columnNames.Count; i++)
        {
            var index = i;
            items.Add(new QuickOpenItem(columnNames[i], null, () =>
            {
                jumpTo(index);
                return Task.CompletedTask;
            })
            {
                Icon = "IconColumn",
            });
        }

        return new QuickOpenViewModel("Go to column", items, status);
    }

    private static async Task ConnectThenOpen(
        ConnectionConfig config, TableRef table,
        Func<ConnectionConfig, Task<Session?>> connect, Func<Session, TableRef, Task> openTable, StatusModel? status)
    {
        var session = await connect(config);
        if (session is null) return; // the connect step already told the user why

        // The cache can be older than the database.
        if (!session.Schema.Any(t => t.Ref == table))
        {
            status?.Error($"Table {table} no longer exists in {config.Name} (the schema changed since it was cached).");
            return;
        }

        await openTable(session, table);
    }

    // ---- interaction ----

    partial void OnQueryChanged(string value)
    {
        if (_suppressRank) return;
        LastQuery = value;
        Rank();
    }

    public void MoveDown() => Move(1);

    public void MoveUp() => Move(-1);

    /// <summary>PageUp / PageDown: moves the selection by <paramref name="delta"/> rows and stops at the ends (no wrap).</summary>
    public void MoveBy(int delta)
    {
        if (Results.Count == 0) return;
        SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, Results.Count - 1);
    }

    private void Move(int delta)
    {
        if (Results.Count == 0) return;
        SelectedIndex = ((SelectedIndex + delta) % Results.Count + Results.Count) % Results.Count;
    }

    /// <summary>Opens the selected result. Closes the overlay first so a slow connect shows its spinner. Never throws.</summary>
    public Task ConfirmAsync() =>
        SelectedIndex >= 0 && SelectedIndex < Results.Count ? ConfirmAsync(Results[SelectedIndex]) : Task.CompletedTask;

    /// <summary>Opens a specific result (mouse click). Never throws.</summary>
    public async Task ConfirmAsync(QuickOpenItem item)
    {
        CloseRequested?.Invoke();
        try
        {
            await item.Open();
        }
        catch (OperationCanceledException)
        {
            _status?.Info($"Opening {item.Label} cancelled.");
        }
        catch (Exception e)
        {
            _status?.Error($"Cannot open {item.Label}: {ErrorText.Describe(e)}");
        }
    }

    /// <summary>Esc: forgets the query and results and asks to close.</summary>
    public void Cancel()
    {
        _suppressRank = true; // no point ranking the whole list for the empty query just to throw it away
        Query = "";
        _suppressRank = false;
        Results = [];
        SelectedIndex = -1;
        CloseRequested?.Invoke();
    }

    // ---- ranking ----

    /// <summary>
    /// Top <see cref="MaxResults"/> by fuzzy score, then label, then connection name, then input position (so the
    /// order never depends on an unstable sort). Keeps a small sorted window instead of scoring into a big list.
    /// </summary>
    private void Rank()
    {
        var query = Query;
        Span<int> topScore = stackalloc int[MaxResults];
        Span<int> topIndex = stackalloc int[MaxResults];
        var count = 0;
        var matched = 0;

        for (var i = 0; i < _items.Length; i++)
        {
            if (FuzzyMatcher.ScoreTolerant(query, _items[i].Label) is not { } score) continue;
            matched++;
            if (count == MaxResults && !Better(score, i, topScore[MaxResults - 1], topIndex[MaxResults - 1])) continue;

            var pos = count < MaxResults ? count++ : MaxResults - 1;
            while (pos > 0 && Better(score, i, topScore[pos - 1], topIndex[pos - 1]))
            {
                topScore[pos] = topScore[pos - 1];
                topIndex[pos] = topIndex[pos - 1];
                pos--;
            }

            topScore[pos] = score;
            topIndex[pos] = i;
        }

        var results = new QuickOpenItem[count];
        for (var i = 0; i < count; i++) results[i] = _items[topIndex[i]];
        Results = results;
        MatchCount = matched;
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(IsEmpty));
        SelectedIndex = count == 0 ? -1 : 0;
    }

    private bool Better(int scoreA, int indexA, int scoreB, int indexB)
    {
        if (scoreA != scoreB) return scoreA > scoreB;
        var a = _items[indexA];
        var b = _items[indexB];
        var byLabel = StringComparer.OrdinalIgnoreCase.Compare(a.Label, b.Label);
        if (byLabel != 0) return byLabel < 0;
        var byGroup = StringComparer.OrdinalIgnoreCase.Compare(a.Group, b.Group);
        return byGroup != 0 ? byGroup < 0 : indexA < indexB;
    }
}
