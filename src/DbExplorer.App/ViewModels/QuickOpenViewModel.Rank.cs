using DbExplorer.Core.Metadata;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

// Ranking, on every keystroke: objects and columns are ranked apart, then interleaved so columns never drown the tables.
public sealed partial class QuickOpenViewModel
{
    /// <summary>Rows kept for columns when objects alone would fill the list.</summary>
    private const int ColumnShare = 15;

    /// <summary>Scores this low come from the typo-tolerant match only.</summary>
    private const int TypoScore = -50_000;

    private void Use(QuickOpenSource source)
    {
        _source.Changed -= Rank;
        _source = source;
        _source.Changed += Rank;
    }

    /// <summary>
    /// Top <see cref="MaxResults"/> by fuzzy score, then label, then connection name, then input position (so the
    /// order never depends on an unstable sort). Keeps a small sorted window instead of scoring into a big list.
    /// </summary>
    private void Rank()
    {
        var query = Query;
        var items = _source.Items;
        var main = new Window(ItemOrder);
        for (var i = 0; i < items.Length; i++)
        {
            if (!Shows(items[i].Type)) continue;
            if (FuzzyMatcher.ScoreTolerant(query, items[i].Label) is { } score) main.Add(score, i);
        }

        // Columns wait for a query (unless there is nothing else to list), and match strictly: no typo search over so many.
        var columns = new Window(ColumnOrder);
        var all = _source.HasColumns && Shows(QuickOpenTypes.Columns) && (query.Length > 0 || main.Count == 0)
            ? _source.Columns
            : [];
        var qualified = query.AsSpan().IndexOfAny('.', ' ') >= 0;   // "orders.total": the table is part of what is typed
        for (var i = 0; i < all.Length; i++)
        {
            var text = qualified ? string.Concat(all[i].Owner.Prefix, ".", all[i].Name) : all[i].Name;
            if (FuzzyMatcher.Score(query, text) is { } score) columns.Add(score, i);
        }

        var results = Interleave(query, items, main, all, columns);
        Results = results;
        MatchCount = main.Matched + columns.Matched;
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(IsEmpty));
        SelectedIndex = results.Length == 0 ? -1 : 0;
    }

    // Both lists keep their own order. A column goes first only when its name matches what was typed better than the
    // object's name does (exact, then start, then inside, then scattered letters, then typo): at equal quality objects win.
    private static QuickOpenItem[] Interleave(
        string query, QuickOpenItem[] items, Window main, QuickOpenColumn[] all, Window columns)
    {
        var columnTake = Math.Min(columns.Count, Math.Max(ColumnShare, MaxResults - main.Count));
        var mainTake = Math.Min(main.Count, MaxResults - columnTake);
        var term = query[(query.LastIndexOfAny(['.', ' ']) + 1)..];

        var results = new QuickOpenItem[mainTake + columnTake];
        for (int n = 0, m = 0, c = 0; n < results.Length; n++)
        {
            var takeColumn = c < columnTake
                && (m >= mainTake || Quality(all[columns.Index(c)].Name, term, columns.ScoreAt(c))
                    > Quality(items[main.Index(m)].Name ?? items[main.Index(m)].Label, term, main.ScoreAt(m)));
            results[n] = takeColumn ? ColumnItem(all[columns.Index(c++)]) : items[main.Index(m++)];
        }

        return results;
    }

    private static int Quality(string name, string term, int score) =>
        score <= TypoScore ? -1
        : name.Equals(term, StringComparison.OrdinalIgnoreCase) ? 3
        : name.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 2
        : name.Contains(term, StringComparison.OrdinalIgnoreCase) ? 1
        : 0;

    private static QuickOpenItem ColumnItem(QuickOpenColumn column)
    {
        var (owner, name) = column;
        return new QuickOpenItem($"{owner.Prefix}.{name}", owner.Table, () => owner.Open(name))
        {
            Title = name,
            Name = name,
            Context = owner.Context,
            Environment = owner.Environment,
            Group = owner.Group,
            Icon = "IconColumn",
            Type = QuickOpenTypes.Columns,
        };
    }

    private int ItemOrder(int a, int b)
    {
        var (x, y) = (_source.Items[a], _source.Items[b]);
        var byLabel = StringComparer.OrdinalIgnoreCase.Compare(x.Label, y.Label);
        return byLabel != 0 ? byLabel : StringComparer.OrdinalIgnoreCase.Compare(x.Group, y.Group);
    }

    private int ColumnOrder(int a, int b)
    {
        var (x, y) = (_source.Columns[a], _source.Columns[b]);
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
