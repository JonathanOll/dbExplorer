using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

// Ranking, on every keystroke: objects and columns are ranked apart, then interleaved so columns never drown the tables.
public sealed partial class QuickOpenViewModel
{
    /// <summary>Rows kept for columns when objects alone would fill the list.</summary>
    private const int ColumnShare = 15;

    // What the current ranking looks through: the rows of the source, with those of the other connections when extended.
    private QuickOpenItem[] _items = [];
    private QuickOpenColumn[] _columns = [];

    private void Use(QuickOpenSource source)
    {
        _source.Changed -= Rank;
        _source = source;
        _source.Changed += Rank;
    }

    /// <summary>
    /// Top <see cref="MaxResults"/> by score, then label, then connection name, then input position (so the order never
    /// depends on an unstable sort). Keeps a small sorted window instead of scoring into a big list.
    /// </summary>
    private void Rank()
    {
        var source = _source;
        var extension = source.Extension;
        var query = new QuickOpenQuery(Query, extension is null ? ScopesOf(source) : extension.Places.Append(extension.Here));
        var columnsWanted = source.HasColumns && Shows(QuickOpenTypes.Columns);

        var (main, columns) = Match(query, source.Items, columnsWanted ? source.Columns : null);
        string? because = null;
        if (extension is not null)
        {
            because = WordForElsewhere(source, extension, query, main.Matched + columns.Matched == 0);
            if (because is not null)
            {
                if (source.Elsewhere is null) Extending = source.Reading ??= ReadElsewhereAsync(source, extension);
                else (main, columns) = Match(query, source.AllItems, columnsWanted ? source.AllColumns : null);
            }
        }

        var results = Interleave(query, main, columns);
        // Once other connections are listed, the rows of this one say where they are too.
        if (because is not null && source.Elsewhere is not null)
            for (var i = 0; i < results.Length; i++)
                if (results[i] is { Context: null, Scope: { } scope } row) results[i] = row with { Context = scope.Badge };

        ExtensionText = because is null ? null : ExtensionMessage(source, extension!, because);
        Results = results;
        MatchCount = main.Matched + columns.Matched;
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(IsEmpty));
        SelectedIndex = results.Length == 0 ? -1 : 0;
    }

    // The connections a list spans (rows of one connection follow each other).
    private static IEnumerable<QuickOpenScope> ScopesOf(QuickOpenSource source)
    {
        var seen = new HashSet<QuickOpenScope>(ReferenceEqualityComparer.Instance);
        QuickOpenScope? last = null;
        foreach (var item in source.Items)
        {
            if (item.Scope is not { } scope || ReferenceEquals(scope, last)) continue;
            last = scope;
            seen.Add(scope);
        }

        return seen;
    }

    private (Window Main, Window Columns) Match(QuickOpenQuery query, QuickOpenItem[] items, QuickOpenColumn[]? allColumns)
    {
        _items = items;
        var main = new Window(ItemOrder);
        for (var i = 0; i < items.Length; i++)
        {
            if (!Shows(items[i].Type)) continue;
            if (query.Score(items[i]) is { } score) main.Add(score, i);
        }

        // Columns wait for a query (unless there is nothing else to list).
        _columns = allColumns is not null && (query.Words.Length > 0 || main.Count == 0) ? allColumns : [];
        var columns = new Window(ColumnOrder);
        for (var i = 0; i < _columns.Length; i++)
            if (query.Score(in _columns[i]) is { } score) columns.Add(score, i);

        return (main, columns);
    }

    // Both lists keep their own order. A column goes first only when its name matches what was typed better than the
    // object's name does (exact, then start, then inside, then scattered letters, then typo): at equal quality objects win.
    private QuickOpenItem[] Interleave(QuickOpenQuery query, Window main, Window columns)
    {
        var columnTake = Math.Min(columns.Count, Math.Max(ColumnShare, MaxResults - main.Count));
        var mainTake = Math.Min(main.Count, MaxResults - columnTake);

        var results = new QuickOpenItem[mainTake + columnTake];
        for (int n = 0, m = 0, c = 0; n < results.Length; n++)
        {
            var takeColumn = c < columnTake
                && (m >= mainTake || query.Quality(_columns[columns.Index(c)].Name)
                    > query.Quality(_items[main.Index(m)].Name ?? _items[main.Index(m)].Label));
            results[n] = takeColumn ? ColumnItem(_columns[columns.Index(c++)]) : _items[main.Index(m++)];
        }

        return results;
    }

    private static QuickOpenItem ColumnItem(QuickOpenColumn column)
    {
        var (owner, name) = column;
        return new QuickOpenItem($"{owner.Prefix}.{name}", owner.Table, () => owner.Open(name))
        {
            Title = name,
            Name = name,
            Context = owner.Context,
            Scope = owner.Scope,
            Environment = owner.Environment,
            Group = owner.Group,
            Icon = "IconColumn",
            Type = QuickOpenTypes.Columns,
        };
    }

    private int ItemOrder(int a, int b)
    {
        var (x, y) = (_items[a], _items[b]);
        var byLabel = StringComparer.OrdinalIgnoreCase.Compare(x.Label, y.Label);
        return byLabel != 0 ? byLabel : StringComparer.OrdinalIgnoreCase.Compare(x.Group, y.Group);
    }

    private int ColumnOrder(int a, int b)
    {
        var (x, y) = (_columns[a], _columns[b]);
        var byName = StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name);
        return byName != 0 ? byName : StringComparer.OrdinalIgnoreCase.Compare(x.Owner.Prefix, y.Owner.Prefix);
    }

    /// <summary>The best <see cref="MaxResults"/> matches seen so far, sorted.</summary>
    /// <param name="tieOrder">Orders two candidates of equal score; indexes are added in rising order, the last tie-break.</param>
    private sealed class Window(Comparison<int> tieOrder)
    {
        private readonly int[] _scores = new int[MaxResults];
        private readonly int[] _indexes = new int[MaxResults];

        public int Count { get; private set; }
        public int Matched { get; private set; }
        public int Index(int position) => _indexes[position];
        public int ScoreAt(int position) => _scores[position];

        public void Add(int score, int index)
        {
            Matched++;
            if (Count == MaxResults && !Better(score, index, MaxResults - 1)) return;

            var at = Count < MaxResults ? Count++ : MaxResults - 1;
            while (at > 0 && Better(score, index, at - 1))
            {
                _scores[at] = _scores[at - 1];
                _indexes[at] = _indexes[at - 1];
                at--;
            }

            _scores[at] = score;
            _indexes[at] = index;
        }

        private bool Better(int score, int index, int position) =>
            score != _scores[position] ? score > _scores[position] : tieOrder(index, _indexes[position]) < 0;
    }
}
